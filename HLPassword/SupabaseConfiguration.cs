using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace HLPassword
{
    public sealed class SupabaseConfiguration
    {
        public string? Url { get; }
        public string? PublishableKey { get; }
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(PublishableKey);

        private SupabaseConfiguration(string? url, string? publishableKey)
        {
            Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
            PublishableKey = string.IsNullOrWhiteSpace(publishableKey) ? null : publishableKey.Trim();
        }

        public static SupabaseConfiguration LoadFromEnvironment()
        {
            Assembly assembly = typeof(SupabaseConfiguration).Assembly;
            string? buildUrl = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "SupabaseProjectUrl")?.Value;
            string? buildKey = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "SupabasePublishableKey")?.Value;

            return new SupabaseConfiguration(
                Environment.GetEnvironmentVariable("HLPASSWORD_SUPABASE_URL") ?? buildUrl ?? ReadSolutionEnvValue("HLPASSWORD_SUPABASE_URL"),
                Environment.GetEnvironmentVariable("HLPASSWORD_SUPABASE_PUBLISHABLE_KEY") ?? buildKey ?? ReadSolutionEnvValue("HLPASSWORD_SUPABASE_PUBLISHABLE_KEY"));
        }

        private static string? ReadSolutionEnvValue(string name)
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "HLPassword.slnx")))
                {
                    string envPath = Path.Combine(directory.FullName, ".env");
                    if (!File.Exists(envPath))
                    {
                        return null;
                    }

                    foreach (string line in File.ReadLines(envPath))
                    {
                        string entry = line.Trim();
                        if (entry.StartsWith("export ", StringComparison.Ordinal))
                        {
                            entry = entry.Substring(7).Trim();
                        }

                        int separator = entry.IndexOf('=');
                        if (separator <= 0 || !entry.Substring(0, separator).Trim().Equals(name, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        string value = entry.Substring(separator + 1).Trim();
                        if (value.Length >= 2 &&
                            ((value[0] == '"' && value[value.Length - 1] == '"') ||
                             (value[0] == '\'' && value[value.Length - 1] == '\'')))
                        {
                            value = value.Substring(1, value.Length - 2);
                        }
                        return value;
                    }
                    return null;
                }
                directory = directory.Parent;
            }
            return null;
        }

        public void EnsureConfigured()
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Host != "localhost"))
            {
                throw new InvalidOperationException("Set HLPASSWORD_SUPABASE_URL to a valid HTTPS Supabase project URL.");
            }

            if (string.IsNullOrWhiteSpace(PublishableKey))
            {
                throw new InvalidOperationException("Set HLPASSWORD_SUPABASE_PUBLISHABLE_KEY to the Supabase publishable key (or legacy anon key).");
            }

            if (PublishableKey.StartsWith("sb_secret_", StringComparison.OrdinalIgnoreCase) || IsServiceRoleJwt(PublishableKey))
            {
                throw new InvalidOperationException("A service-role/secret key must never be used by the desktop application.");
            }
        }

        private static bool IsServiceRoleJwt(string key)
        {
            string[] parts = key.Split('.');
            if (parts.Length != 3)
            {
                return false;
            }

            try
            {
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(payload));
                return document.RootElement.TryGetProperty("role", out JsonElement role) &&
                       role.ValueKind == JsonValueKind.String &&
                       role.GetString()?.Equals("service_role", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
