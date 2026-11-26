using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace HLPassword
{
    public class LockedAccountRecord
    {
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int LockCount { get; set; } = 0;
        public bool IsCurrentlyLocked { get; set; } = false;
        public string UnlockOtp { get; set; } = string.Empty;
        public string LastLockedTime { get; set; } = DateTime.Now.ToString("O");
    }

    public static class AdminLockManager
    {
        private const string GoogleScriptUrl = "https://script.google.com/macros/s/AKfycbzvUSXt9ql7wwA1-bVjcT-eoHWGU8Y_Bh8rx2mGJeWLfoh3kZDwUfmellx0GOBvYbn8Eg/exec";
        private const string ApprovedPrefix = "APPROVED|";
        private const string ReasonPrefix = "REASON|";
        private const string BroadcastUserKey = "__BROADCAST_NOTIFICATIONS__";

        private static readonly HttpClient _http = new HttpClient();

        public static bool IsAdminApprovedForUnlock(LockedAccountRecord? rec)
        {
            if (rec == null || !rec.IsCurrentlyLocked) return false;
            return !string.IsNullOrWhiteSpace(rec.UnlockOtp) &&
                   rec.UnlockOtp.StartsWith(ApprovedPrefix) &&
                   rec.UnlockOtp.Length > ApprovedPrefix.Length;
        }

        public static string GetLockReason(LockedAccountRecord? rec)
        {
            if (rec != null && !string.IsNullOrWhiteSpace(rec.UnlockOtp) && rec.UnlockOtp.StartsWith(ReasonPrefix))
            {
                string r = rec.UnlockOtp.Substring(ReasonPrefix.Length).Trim();
                if (!string.IsNullOrEmpty(r)) return r;
            }
            return "Vi phạm quy tắc tiêu chuẩn cộng đồng";
        }

        // Lấy danh sách thông báo do Admin đẩy từ hệ thống
        public static async Task<List<string>> GetBroadcastNotificationsAsync()
        {
            try
            {
                string json = await _http.GetStringAsync(GoogleScriptUrl);
                var records = JsonSerializer.Deserialize<List<LockedAccountRecord>>(json) ?? new List<LockedAccountRecord>();
                var broadcastRec = records.FirstOrDefault(r => r.Username.Equals(BroadcastUserKey, StringComparison.OrdinalIgnoreCase));

                if (broadcastRec != null && !string.IsNullOrWhiteSpace(broadcastRec.UnlockOtp))
                {
                    return JsonSerializer.Deserialize<List<string>>(broadcastRec.UnlockOtp) ?? new List<string>();
                }
            }
            catch { }
            return new List<string>();
        }

        public static async Task PushAccountStatusAsync(string username, string email, int lockCount, bool isLocked, string unlockOtp = "")
        {
            try
            {
                var record = new LockedAccountRecord
                {
                    Username = username,
                    Email = email,
                    LockCount = lockCount,
                    IsCurrentlyLocked = isLocked,
                    UnlockOtp = unlockOtp,
                    LastLockedTime = DateTime.Now.ToString("O")
                };
                string json = JsonSerializer.Serialize(record);
                await _http.PostAsync(GoogleScriptUrl, new StringContent(json, Encoding.UTF8, "application/json"));
            }
            catch { }
        }

        public static async Task<LockedAccountRecord?> GetAccountStatusAsync(string username)
        {
            try
            {
                string json = await _http.GetStringAsync(GoogleScriptUrl);
                var records = JsonSerializer.Deserialize<List<LockedAccountRecord>>(json) ?? new List<LockedAccountRecord>();
                return records.FirstOrDefault(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        public static async Task<bool> VerifyUnlockFromAdminAsync(string username, string inputOtp)
        {
            if (string.IsNullOrWhiteSpace(inputOtp)) return false;

            try
            {
                var rec = await GetAccountStatusAsync(username);
                if (!IsAdminApprovedForUnlock(rec)) return false;

                string expectedOtp = rec!.UnlockOtp.Substring(ApprovedPrefix.Length).Trim();
                if (inputOtp.Trim() == expectedOtp)
                {
                    rec.IsCurrentlyLocked = false;
                    rec.UnlockOtp = string.Empty;
                    string updateJson = JsonSerializer.Serialize(rec);
                    await _http.PostAsync(GoogleScriptUrl, new StringContent(updateJson, Encoding.UTF8, "application/json"));
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}