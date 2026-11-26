using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HLPassword
{
    public sealed class SupabaseSession
    {
        public string UserId { get; }
        public string Email { get; }
        public string AccessToken { get; private set; }
        public string RefreshToken { get; private set; }
        public DateTime ExpiresAtUtc { get; private set; }

        internal SupabaseSession(string userId, string email, string accessToken, string refreshToken, int expiresIn)
        {
            UserId = userId;
            Email = email;
            AccessToken = accessToken;
            RefreshToken = refreshToken;
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn);
        }

        internal void UpdateTokens(string accessToken, string refreshToken, int expiresIn)
        {
            AccessToken = accessToken;
            RefreshToken = refreshToken;
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn);
        }
    }

    public sealed class SupabaseProfile
    {
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    public sealed class SupabaseClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly SupabaseConfiguration _configuration;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private SupabaseSession? _session;

        public SupabaseSession? Session => _session;

        public SupabaseClient(SupabaseConfiguration configuration)
        {
            configuration.EnsureConfigured();
            _configuration = configuration;
        }

        public async Task<SupabaseSession> SignInWithGoogleAsync(CancellationToken cancellationToken = default)
        {
            byte[] verifierBytes = RandomNumberGenerator.GetBytes(32);
            string verifier = Base64UrlEncode(verifierBytes);
            string challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string callbackPath = Base64UrlEncode(RandomNumberGenerator.GetBytes(24));

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Uri redirectUri = new Uri($"http://127.0.0.1:{port}/{callbackPath}/");

            string authorizationUrl = BuildUrl("/auth/v1/authorize") +
                "?provider=google" +
                "&redirect_to=" + Uri.EscapeDataString(redirectUri.ToString()) +
                "&code_challenge=" + Uri.EscapeDataString(challenge) +
                "&code_challenge_method=s256";

            Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));

            string expectedPath = redirectUri.AbsolutePath;
            TcpClient callbackClient;
            Uri callbackRequest;
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token);
                string? requestLine = await ReadRequestLineAsync(client.GetStream(), timeout.Token);
                if (requestLine == null)
                {
                    client.Dispose();
                    continue;
                }

                callbackRequest = new Uri(redirectUri, requestLine);
                if (string.Equals(callbackRequest.AbsolutePath, "/favicon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteNotFoundResponseAsync(client.GetStream(), timeout.Token);
                    client.Dispose();
                    continue;
                }

                callbackClient = client;
                break;
            }

            using (callbackClient)
            {
                if (!string.Equals(callbackRequest.AbsolutePath, expectedPath, StringComparison.Ordinal))
                {
                    await WriteCallbackResponseAsync(callbackClient.GetStream(), "Invalid sign-in callback.", timeout.Token);
                    throw new InvalidOperationException("The Google sign-in callback did not match the requested callback.");
                }

                Dictionary<string, string> callbackParameters = ParseQuery(callbackRequest.Query);
                if (callbackParameters.TryGetValue("error_description", out string? errorDescription) ||
                    callbackParameters.TryGetValue("error", out errorDescription))
                {
                    await WriteCallbackResponseAsync(callbackClient.GetStream(), "Sign-in was not completed. You can close this window.", timeout.Token);
                    throw new InvalidOperationException("Google sign-in failed: " + WebUtility.UrlDecode(errorDescription));
                }

                if (!callbackParameters.TryGetValue("code", out string? authorizationCode))
                {
                    await WriteCallbackResponseAsync(callbackClient.GetStream(), "Sign-in did not return an authorization code. You can close this window.", timeout.Token);
                    throw new InvalidOperationException("Google sign-in did not return an authorization code.");
                }

                try
                {
                    using JsonDocument tokenResponse = await PostJsonAsync(
                        "/auth/v1/token?grant_type=pkce",
                        new { auth_code = authorizationCode, code_verifier = verifier },
                        null,
                        timeout.Token);

                    _session = ParseSession(tokenResponse.RootElement);
                }
                catch (Exception ex)
                {
                    await WriteCallbackResponseAsync(
                        callbackClient.GetStream(),
                        "Sign-in could not be completed. Return to HLPassword and try again.",
                        timeout.Token);
                    throw new InvalidOperationException("Supabase could not complete the Google sign-in: " + ex.Message, ex);
                }

                await WriteCallbackResponseAsync(
                    callbackClient.GetStream(),
                    "HLPassword sign-in complete. Return to the app to continue.",
                    timeout.Token);
                return _session;
            }
        }

        public async Task<SupabaseProfile?> GetProfileAsync(CancellationToken cancellationToken = default)
        {
            await EnsureFreshSessionAsync(cancellationToken);
            string query = "select=username,email&user_id=eq." + Uri.EscapeDataString(_session!.UserId) + "&limit=1";
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "/rest/v1/user_profiles?" + query, _session.AccessToken);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);

            using JsonDocument rows = JsonDocument.Parse(body);
            if (rows.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement row = rows.RootElement[0];
            return new SupabaseProfile
            {
                Username = row.GetProperty("username").GetString() ?? string.Empty,
                Email = row.GetProperty("email").GetString() ?? string.Empty
            };
        }

        public async Task CreateProfileAsync(string username, string email, CancellationToken cancellationToken = default)
        {
            await EnsureFreshSessionAsync(cancellationToken);
            var profile = new { user_id = _session!.UserId, username, email };
            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/rest/v1/user_profiles", _session.AccessToken);
            request.Content = new StringContent(JsonSerializer.Serialize(profile), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);
        }

        public async Task<Dictionary<string, string>> GetDocumentsAsync(IEnumerable<string> documentKeys, CancellationToken cancellationToken = default)
        {
            await EnsureFreshSessionAsync(cancellationToken);
            string inClause = string.Join(",", documentKeys.Select(k => Uri.EscapeDataString(k)));
            string query = $"select=document_key,payload&document_key=in.({inClause})";
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "/rest/v1/user_documents?" + query, _session!.AccessToken);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            using JsonDocument rows = JsonDocument.Parse(body);
            foreach (JsonElement row in rows.RootElement.EnumerateArray())
            {
                string? key = row.GetProperty("document_key").GetString();
                if (!string.IsNullOrEmpty(key))
                {
                    result[key] = row.GetProperty("payload").GetRawText();
                }
            }
            return result;
        }

        public async Task<string?> GetDocumentAsync(string documentKey, CancellationToken cancellationToken = default)
        {
            await EnsureFreshSessionAsync(cancellationToken);
            string query = "select=payload&document_key=eq." + Uri.EscapeDataString(documentKey) + "&limit=1";
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "/rest/v1/user_documents?" + query, _session!.AccessToken);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);

            using JsonDocument rows = JsonDocument.Parse(body);
            return rows.RootElement.GetArrayLength() == 0
                ? null
                : rows.RootElement[0].GetProperty("payload").GetRawText();
        }

        public async Task PutDocumentAsync(string documentKey, string jsonPayload, CancellationToken cancellationToken = default)
        {
            await EnsureFreshSessionAsync(cancellationToken);
            using JsonDocument payload = JsonDocument.Parse(jsonPayload);
            var row = new
            {
                user_id = _session!.UserId,
                document_key = documentKey,
                payload = payload.RootElement
            };

            using HttpRequestMessage request = CreateRequest(
                HttpMethod.Post,
                "/rest/v1/user_documents?on_conflict=user_id%2Cdocument_key",
                _session.AccessToken);
            request.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");
            request.Content = new StringContent(JsonSerializer.Serialize(row), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);
        }

        public async Task DeleteDocumentAsync(string documentKey, CancellationToken cancellationToken = default)
        {
            await EnsureFreshSessionAsync(cancellationToken);
            string query = "user_id=eq." + Uri.EscapeDataString(_session!.UserId) +
                           "&document_key=eq." + Uri.EscapeDataString(documentKey);
            using HttpRequestMessage request = CreateRequest(HttpMethod.Delete, "/rest/v1/user_documents?" + query, _session.AccessToken);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);
        }

        public async Task SignOutAsync()
        {
            SupabaseSession? currentSession = _session;
            _session = null;
            if (currentSession == null)
            {
                return;
            }

            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/auth/v1/logout", currentSession.AccessToken);
            using HttpResponseMessage response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Supabase sign-out failed with HTTP {(int)response.StatusCode}.");
            }
        }

        private async Task EnsureFreshSessionAsync(CancellationToken cancellationToken)
        {
            SupabaseSession session = _session ?? throw new InvalidOperationException("Sign in with Google before accessing Supabase data.");
            if (session.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return;
            }

            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                if (session.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(1))
                {
                    return;
                }

                using JsonDocument response = await PostJsonAsync(
                    "/auth/v1/token?grant_type=refresh_token",
                    new { refresh_token = session.RefreshToken },
                    null,
                    cancellationToken);
                SupabaseSession refreshed = ParseSession(response.RootElement);
                session.UpdateTokens(refreshed.AccessToken, refreshed.RefreshToken, Math.Max(1, (int)(refreshed.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds));
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task<JsonDocument> PostJsonAsync(string path, object payload, string? accessToken, CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path, accessToken);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);
            return JsonDocument.Parse(body);
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path, string? accessToken)
        {
            var request = new HttpRequestMessage(method, BuildUrl(path));
            request.Headers.TryAddWithoutValidation("apikey", _configuration.PublishableKey);
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            }
            return request;
        }

        private string BuildUrl(string path) => _configuration.Url + path;

        private static SupabaseSession ParseSession(JsonElement json)
        {
            string accessToken = json.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Supabase did not return an access token.");
            string refreshToken = json.GetProperty("refresh_token").GetString() ?? throw new InvalidOperationException("Supabase did not return a refresh token.");
            int expiresIn = json.TryGetProperty("expires_in", out JsonElement expires) ? expires.GetInt32() : 3600;
            JsonElement user = json.GetProperty("user");
            string userId = user.GetProperty("id").GetString() ?? throw new InvalidOperationException("Supabase did not return a user id.");
            string email = user.TryGetProperty("email", out JsonElement emailElement) ? emailElement.GetString() ?? string.Empty : string.Empty;
            return new SupabaseSession(userId, email, accessToken, refreshToken, expiresIn);
        }

        private static void EnsureSuccess(HttpResponseMessage response, string body)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            string message = $"Supabase request failed (HTTP {(int)response.StatusCode}).";
            try
            {
                using JsonDocument error = JsonDocument.Parse(body);
                if ((error.RootElement.TryGetProperty("msg", out JsonElement msg) ||
                     error.RootElement.TryGetProperty("message", out msg) ||
                     error.RootElement.TryGetProperty("error_description", out msg)) &&
                    msg.ValueKind == JsonValueKind.String)
                {
                    string? detail = msg.GetString();
                    if (!string.IsNullOrWhiteSpace(detail))
                    {
                        message += " " + detail;
                    }
                }
            }
            catch (JsonException)
            {
            }
            throw new InvalidOperationException(message);
        }

        private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            using var received = new MemoryStream();
            while (received.Length < 4096)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (count == 0)
                {
                    return null;
                }

                received.Write(buffer, 0, count);
                string text = Encoding.ASCII.GetString(received.ToArray());
                int lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
                if (lineEnd >= 0)
                {
                    string line = text.Substring(0, lineEnd);
                    string[] parts = line.Split(' ');
                    if (parts.Length >= 3 && string.Equals(parts[0], "GET", StringComparison.Ordinal) && parts[2].StartsWith("HTTP/", StringComparison.Ordinal))
                    {
                        return parts[1];
                    }
                    break;
                }
            }

            throw new InvalidOperationException("The local sign-in callback request was invalid.");
        }

        private static async Task WriteNotFoundResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static async Task WriteCallbackResponseAsync(NetworkStream stream, string message, CancellationToken cancellationToken)
        {
            string html = "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>HLPassword</title></head><body><main><h1>HLPassword</h1><p>" + WebUtility.HtmlEncode(message) + "</p></main><script>history.replaceState(null, document.title, location.pathname);</script></body></html>";
            byte[] body = Encoding.UTF8.GetBytes(html);
            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split('=', 2);
                string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
                string value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty;
                values[key] = value;
            }
            return values;
        }

        private static string Base64UrlEncode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
