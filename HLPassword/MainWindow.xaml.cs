using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace HLPassword
{
    public partial class MainWindow : Window
    {
        // =================================================================================
        // HỆ THỐNG XỬ LÝ DỮ LIỆU ĐÁM MÂY & RAM (100% Không lưu file xuống ổ cứng)
        // =================================================================================
        private List<FolderNode> TreeStructure = new List<FolderNode>();
        private Dictionary<string, string> _inMemoryPasswords = new Dictionary<string, string>();
        private string CurrentFolderId = string.Empty;

        private byte[] _dataEncryptionKey = new byte[32];

        private string _currentUsername = string.Empty;
        private string _currentUserEmail = string.Empty;
        private SupabaseClient? _supabaseClient;
        private const string _internalRecoveryKey = "HLPassword_Cloud_Recovery_Key_2026";
        private static readonly HashSet<string> AdminEmails = new(StringComparer.OrdinalIgnoreCase)
        {
            "hoanglam0302.zip@gmail.com",
            "dtphat2k4@gmail.com"
        };
        private bool _isAdmin = false;
        private bool _hasPersonalVault = false;
        private List<LockedAccountRecord> _adminAllAccounts = new List<LockedAccountRecord>();
        private List<string> _adminBroadcasts = new List<string>();

        // Bộ đếm số lần vi phạm đặt tên Folder trong mỗi phiên đăng nhập (tối đa 3 lần nhắc, lần 4 khóa)
        private int _folderViolationCount = 0;
        private bool _isAccountLocked = false;
        private bool _canShowUnlockInput = false;
        private string _currentLockReason = "Vi phạm quy tắc tiêu chuẩn cộng đồng";
        private DispatcherTimer? _remoteLockTimer;

        // =================================================================================
        // KHU VỰC DÀNH CHO ADMIN CHỈNH SỬA THÔNG BÁO
        // =================================================================================
        private List<string> _notifications = new List<string>()
        {
            "Hệ thống đã nâng cấp bảo mật HLPassword Ver 2.2.19 beta!",
            "Cập nhật hệ thống răn đe pháp lý và kiểm duyệt tiêu chuẩn cộng đồng."
        };

        private string _notificationVersion = "Notif_v5";
        private string _lastSeenBroadcastSignature = string.Empty;
        // =================================================================================

        private bool _hasUnreadNotifications = true;
        private string _strConfirm = "XÁC NHẬN";
        private string _strYes = "CÓ";
        private string _strCancel = "HỦY BỎ";
        private string _strClose = "ĐÓNG";
        private string _currentLangCode = "VN";

        private DateTime _popupLastClosed = DateTime.MinValue;

        private FolderNode? CurrentViewGroup = null;
        private Stack<FolderNode> NavigationHistory = new Stack<FolderNode>();

        private bool IsPasswordVisible = false;
        private bool IsEmailVisible = false;

        private Point _dragStartPoint;
        private Window? _dragVisualWindow;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Win32Point pt);

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Point { public int X; public int Y; }

        private string CurrentPasswordValue
        {
            get => IsPasswordVisible ? PasswordBoxVisible.Text : PasswordBoxHidden.Password;
            set { PasswordBoxVisible.Text = value; PasswordBoxHidden.Password = value; }
        }

        private string CurrentEmailValue
        {
            get => IsEmailVisible ? EmailBoxVisible.Text : EmailBoxHidden.Password;
            set { EmailBoxVisible.Text = value; EmailBoxHidden.Password = value; }
        }

        private string GetEulaFilePath()
        {
            string folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HLPasswordToken");
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
            return Path.Combine(folderPath, "eula_accepted.dat");
        }

        public MainWindow()
        {
            InitializeComponent();

            HomeGrid.Visibility = Visibility.Visible;
            FolderGrid.Visibility = Visibility.Hidden;
            AuthGrid.Visibility = Visibility.Hidden;
            PasswordGrid.Visibility = Visibility.Hidden;
            AdminDashboardGrid.Visibility = Visibility.Hidden;

            this.PreviewKeyDown += MainWindow_PreviewKeyDown;

            SettingsPopup.Closed += (s, e) => _popupLastClosed = DateTime.Now;

            if (File.Exists(GetEulaFilePath()))
            {
                ChkTerms.IsChecked = true;
            }
        }

        #region HỆ THỐNG KIỂM DUYỆT NGÔN TỪ & KHÓA TÀI KHOẢN (CHỐNG LÁCH LUẬT)
        private static readonly string[] _vietnameseDiacriticBanned = new[]
        {
            "cặc", "lồn", "buồi", "đụ", "địt", "đĩ", "điếm", "đm", "đcm", "đkm", "đmm",
            "địtme", "đụmá", "đụmẹ", "conđĩ", "chóđẻ", "súcvật", "mấtdạy", "khốnnạn", "vôhọc"
        };

        private static readonly string[] _strictBannedWords = new[]
        {
            "fuck", "fuk", "fck", "fvck", "phuck", "shit", "sh1t", "bitch", "b1tch", "asshole",
            "dick", "d1ck", "pussy", "cunt", "slut", "whore", "bastard", "motherfucker", "mofo",
            "nigger", "nigga", "faggot", "retard", "porn", "hentai", "sex",
            "buoi", "duma", "dume", "ditme", "ditconme", "duconme", "condi", "concho", "thangcho",
            "chode", "sucvat", "khonnan", "matday", "vohoc", "cdmm", "clmm", "loz", "kac", "cak",
            "xamloz", "xamlon", "matlon", "daubuoi", "daulon", "ccmm", "dkmm"
        };

        private static readonly HashSet<string> _shortBannedWords = new(StringComparer.Ordinal)
        {
            "cac", "lon", "du", "di", "dit", "diem", "cave", "dm", "dcm", "dkm", "dmm", "vcl", "vl", "vkl", "clm", "cc", "ml", "sml"
        };

        private static readonly Regex _nonLetterRegex = new(@"[^\p{L}]", RegexOptions.Compiled);
        private static readonly Regex _repeatedCharRegex = new(@"(.)\1+", RegexOptions.Compiled);
        private static readonly Regex _nonAsciiLowerRegex = new(@"[^a-z]", RegexOptions.Compiled);
        private static readonly Regex _nonAsciiLowerSplitRegex = new(@"[^a-z]+", RegexOptions.Compiled);

        private string RemoveDiacritics(string text)
        {
            string normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC).Replace('đ', 'd').Replace('Đ', 'D');
        }

        private string NormalizeLeetspeak(string text)
        {
            return text
                .Replace('@', 'a')
                .Replace('4', 'a')
                .Replace('0', 'o')
                .Replace('1', 'i')
                .Replace('!', 'i')
                .Replace('3', 'e')
                .Replace('$', 's')
                .Replace('5', 's')
                .Replace('7', 't');
        }

        private bool ContainsProfanity(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;

            string lower = input.ToLowerInvariant();

            string onlyLettersWithDiacritics = _nonLetterRegex.Replace(lower, "");
            string collapsedWithDiacritics = _repeatedCharRegex.Replace(onlyLettersWithDiacritics, "$1");

            foreach (var bad in _vietnameseDiacriticBanned)
            {
                if (onlyLettersWithDiacritics.Contains(bad) || collapsedWithDiacritics.Contains(bad))
                    return true;
            }

            string leetNormalized = NormalizeLeetspeak(lower);
            string noDiacritics = RemoveDiacritics(leetNormalized);

            string stripped = _nonAsciiLowerRegex.Replace(noDiacritics, "");
            string collapsed = _repeatedCharRegex.Replace(stripped, "$1");

            foreach (var bad in _strictBannedWords)
            {
                if (stripped.Contains(bad) || collapsed.Contains(bad))
                    return true;
            }

            if (_shortBannedWords.Contains(stripped) || _shortBannedWords.Contains(collapsed))
                return true;

            string[] tokens = _nonAsciiLowerSplitRegex.Split(noDiacritics);
            foreach (var token in tokens)
            {
                if (string.IsNullOrEmpty(token)) continue;
                string collapsedToken = _repeatedCharRegex.Replace(token, "$1");
                if (_shortBannedWords.Contains(token) || _shortBannedWords.Contains(collapsedToken))
                    return true;
            }

            return false;
        }

        private void ApplyAccountLockUI(bool isLocked, bool showUnlockInput = false)
        {
            if (isLocked)
            {
                FolderListBox.Visibility = Visibility.Collapsed;
                NoFolderText.Visibility = Visibility.Collapsed;
                BreadcrumbPanel.Visibility = Visibility.Collapsed;
                LockedAccountOverlay.Visibility = Visibility.Visible;
                UnlockInputPanel.Visibility = showUnlockInput ? Visibility.Visible : Visibility.Collapsed;
                LockedReasonText.Text = $"Bạn đã bị khóa tài khoản bởi lí do: {_currentLockReason}. Liên hệ admin để biết thêm chi tiết.";
            }
            else
            {
                FolderListBox.Visibility = Visibility.Visible;
                BreadcrumbPanel.Visibility = Visibility.Visible;
                LockedAccountOverlay.Visibility = Visibility.Collapsed;
                UnlockInputPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void StartRemoteLockMonitor()
        {
            if (_remoteLockTimer == null)
            {
                _remoteLockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _remoteLockTimer.Tick += async (s, e) => await CheckRemoteLockTickAsync();
            }
            if (!_remoteLockTimer.IsEnabled) _remoteLockTimer.Start();
        }

        private void StopRemoteLockMonitor()
        {
            _remoteLockTimer?.Stop();
        }

        private void ApplyBroadcastList(List<string> broadcastList, bool markAsRead)
        {
            if (broadcastList.Count == 0) return;

            foreach (var note in broadcastList)
            {
                if (!string.IsNullOrWhiteSpace(note) && !_notifications.Contains(note))
                {
                    _notifications.Insert(0, note);
                }
            }

            string signature = string.Join("||", broadcastList);
            if (!markAsRead && signature != _lastSeenBroadcastSignature)
            {
                _hasUnreadNotifications = true;
                SettingsRedDot.Visibility = Visibility.Visible;
                MenuRedDot.Visibility = Visibility.Visible;
            }

            if (markAsRead)
            {
                _lastSeenBroadcastSignature = signature;
            }
        }

        // Đồng bộ thông báo mới do Admin gửi vào hộp thư của người dùng
        private async Task SyncBroadcastNotificationsAsync(bool markAsRead = false)
        {
            var broadcastList = await AdminLockManager.GetBroadcastNotificationsAsync();
            ApplyBroadcastList(broadcastList, markAsRead);
        }

        private async Task CheckRemoteLockTickAsync()
        {
            if (string.IsNullOrEmpty(_currentUsername)) return;

            // Gộp 1 request duy nhất để lấy cả trạng thái khóa và thông báo broadcast
            var (rec, broadcastList) = await AdminLockManager.GetStatusAndBroadcastsAsync(_currentUsername);
            ApplyBroadcastList(broadcastList, false);

            if (_isAccountLocked) return;

            if (rec != null && rec.IsCurrentlyLocked)
            {
                _isAccountLocked = true;
                _canShowUnlockInput = AdminLockManager.IsAdminApprovedForUnlock(rec);
                _currentLockReason = AdminLockManager.GetLockReason(rec);

                PasswordGrid.Visibility = Visibility.Hidden;
                FolderGrid.Visibility = Visibility.Visible;
                ApplyAccountLockUI(true, _canShowUnlockInput);

                string fullMsg = $"Bạn đã bị khóa tài khoản bởi lí do: {_currentLockReason}. Liên hệ admin để biết thêm chi tiết.";
                LockedReasonText.Text = fullMsg;

                string safeUsername = GetSafeKey(_currentUsername);
                string? cloudAccJson = await CloudGet($"Vaults/{safeUsername}/Account");
                if (!string.IsNullOrEmpty(cloudAccJson))
                {
                    var acc = JsonSerializer.Deserialize<AccountData>(cloudAccJson);
                    if (acc != null)
                    {
                        acc.IsLocked = true;
                        acc.LockCount = Math.Max(acc.LockCount, rec.LockCount);
                        await CloudPut($"Vaults/{safeUsername}/Account", JsonSerializer.Serialize(acc));
                    }
                }

                ShowCustomMessage("Tài khoản bị khóa", fullMsg);
            }
        }

        private async Task LockCurrentAccountAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentUsername)) return;
                string safeUsername = GetSafeKey(_currentUsername);

                var sheetRec = await AdminLockManager.GetAccountStatusAsync(_currentUsername);
                int sheetLockCount = sheetRec?.LockCount ?? 0;

                string? cloudAccJson = await CloudGet($"Vaults/{safeUsername}/Account");
                if (!string.IsNullOrEmpty(cloudAccJson))
                {
                    var acc = JsonSerializer.Deserialize<AccountData>(cloudAccJson);
                    if (acc != null)
                    {
                        int newLockCount = Math.Max(acc.LockCount, sheetLockCount) + 1;
                        acc.IsLocked = true;
                        acc.LockCount = newLockCount;
                        acc.UnlockOtp = string.Empty;

                        await CloudPut($"Vaults/{safeUsername}/Account", JsonSerializer.Serialize(acc));
                        await AdminLockManager.PushAccountStatusAsync(_currentUsername, acc.Email, newLockCount, true, string.Empty);
                    }
                }
            }
            catch (Exception ex) { LogError(ex); }
        }

        private async void BtnCheckUnlockStatus_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckUnlockStatus.IsEnabled = false;
            try
            {
                var rec = await AdminLockManager.GetAccountStatusAsync(_currentUsername);
                if (AdminLockManager.IsAdminApprovedForUnlock(rec))
                {
                    _canShowUnlockInput = true;
                    UnlockInputPanel.Visibility = Visibility.Visible;
                    ShowCustomMessage("Thông báo", "Admin đã duyệt yêu cầu mở khóa! Vui lòng nhập mã mở khóa do Admin cấp.");
                }
                else
                {
                    _canShowUnlockInput = false;
                    UnlockInputPanel.Visibility = Visibility.Collapsed;
                    ShowCustomMessage("Thông báo", "Tài khoản chưa được Admin duyệt mở khóa. Vui lòng liên hệ Admin!");
                }
            }
            finally
            {
                BtnCheckUnlockStatus.IsEnabled = true;
            }
        }

        private async void BtnUnlockAccount_Click(object sender, RoutedEventArgs e)
        {
            string inputOtp = UnlockOtpBox.Text.Trim();
            if (string.IsNullOrEmpty(inputOtp))
            {
                ShowCustomMessage("Thông báo", "Vui lòng nhập mã mở khóa do Admin cấp!");
                return;
            }

            BtnUnlockAccount.IsEnabled = false;
            try
            {
                bool unlocked = await AdminLockManager.VerifyUnlockFromAdminAsync(_currentUsername, inputOtp);
                if (unlocked)
                {
                    string safeUsername = GetSafeKey(_currentUsername);
                    string? cloudAccJson = await CloudGet($"Vaults/{safeUsername}/Account");
                    if (!string.IsNullOrEmpty(cloudAccJson))
                    {
                        var acc = JsonSerializer.Deserialize<AccountData>(cloudAccJson);
                        if (acc != null)
                        {
                            acc.IsLocked = false;
                            acc.UnlockOtp = string.Empty;
                            await CloudPut($"Vaults/{safeUsername}/Account", JsonSerializer.Serialize(acc));
                        }
                    }

                    _isAccountLocked = false;
                    _canShowUnlockInput = false;
                    _folderViolationCount = 0;
                    _currentLockReason = "Vi phạm quy tắc tiêu chuẩn cộng đồng";
                    UnlockOtpBox.Text = string.Empty;

                    await SyncCloudToMemory(safeUsername);
                    ApplyAccountLockUI(false, false);
                    LoadTreeStructure();

                    ShowCustomMessage("Thành công", "Tài khoản của bạn đã được mở khóa thành công!");
                }
                else
                {
                    ShowCustomMessage("Lỗi", "Mã mở khóa không chính xác!");
                }
            }
            finally
            {
                BtnUnlockAccount.IsEnabled = true;
            }
        }

        private async Task<bool> CheckFolderViolationAsync(string folderName)
        {
            if (!ContainsProfanity(folderName)) return false;

            _folderViolationCount++;

            if (_folderViolationCount >= 4)
            {
                _isAccountLocked = true;
                _canShowUnlockInput = false;
                _currentLockReason = "Vi phạm nhiều lần quy tắc tiêu chuẩn cộng đồng";
                ApplyAccountLockUI(true, false);
                await LockCurrentAccountAsync();
            }
            else
            {
                ShowCustomMessage($"Cảnh báo vi phạm ({_folderViolationCount}/3)", "Hệ thống ghi nhận bạn đang đặt tên Folder vi phạm chuẩn mực tiêu chuẩn cộng đồng, yêu cầu bạn hãy đặt tên khác, nếu bạn cố tình hoặc hệ thống ghi nhận nhiều lần bạn sẽ bị khóa tài khoản.");
            }

            return true;
        }
        #endregion

        #region CHỐNG COLD-BOOT ATTACK (RAM LOCKING & ENCRYPTION)
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualLock(IntPtr lpAddress, UIntPtr dwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualUnlock(IntPtr lpAddress, UIntPtr dwSize);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectMemory(IntPtr pData, uint cbData, uint dwFlags);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectMemory(IntPtr pData, uint cbData, uint dwFlags);

        private const uint CRYPTPROTECTMEMORY_SAME_PROCESS = 0x00;

        private GCHandle _dekHandle;
        private IntPtr _dekPointer = IntPtr.Zero;
        private bool _isDekLocked = false;

        private void SecureRAM_LockKey()
        {
            if (_dataEncryptionKey == null || _dataEncryptionKey.Length == 0) return;

            _dekHandle = GCHandle.Alloc(_dataEncryptionKey, GCHandleType.Pinned);
            _dekPointer = _dekHandle.AddrOfPinnedObject();

            VirtualLock(_dekPointer, (UIntPtr)_dataEncryptionKey.Length);
            CryptProtectMemory(_dekPointer, (uint)_dataEncryptionKey.Length, CRYPTPROTECTMEMORY_SAME_PROCESS);
            _isDekLocked = true;
        }

        private byte[] SecureRAM_GetUnprotectedKey()
        {
            if (_isDekLocked && _dekPointer != IntPtr.Zero)
            {
                CryptUnprotectMemory(_dekPointer, (uint)_dataEncryptionKey.Length, CRYPTPROTECTMEMORY_SAME_PROCESS);
            }

            byte[] tempKey = new byte[32];
            Array.Copy(_dataEncryptionKey, tempKey, 32);

            if (_isDekLocked && _dekPointer != IntPtr.Zero)
            {
                CryptProtectMemory(_dekPointer, (uint)_dataEncryptionKey.Length, CRYPTPROTECTMEMORY_SAME_PROCESS);
            }

            return tempKey;
        }

        private void SecureRAM_ClearKey()
        {
            if (_dekPointer != IntPtr.Zero)
            {
                if (_isDekLocked) CryptUnprotectMemory(_dekPointer, (uint)_dataEncryptionKey.Length, CRYPTPROTECTMEMORY_SAME_PROCESS);
                Array.Clear(_dataEncryptionKey, 0, _dataEncryptionKey.Length);
                VirtualUnlock(_dekPointer, (UIntPtr)_dataEncryptionKey.Length);
                if (_dekHandle.IsAllocated) _dekHandle.Free();
                _dekPointer = IntPtr.Zero;
                _isDekLocked = false;
            }
        }
        #endregion

        #region MENU CÀI ĐẶT & CHUYỂN ĐỔI NGÔN NGỮ
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if ((DateTime.Now - _popupLastClosed).TotalMilliseconds < 200) return;
            SettingsPopup.IsOpen = true;
        }

        private void MenuNotification_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
            ShowNotificationDialog();
        }

        private void MenuContact_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
            ShowContactAdminDialog();
        }

        private void MenuLang_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
            ShowLanguageDialog();
        }

        private void MenuLogout_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
            if (ShowCustomConfirm(_strClose == "CLOSE" ? "Logout" : "Đăng xuất", _strClose == "CLOSE" ? "Are you sure you want to logout?" : "Bạn có chắc chắn muốn đăng xuất?"))
            {
                Logout_Execute();
            }
        }

        private void ChangeLanguage(string langCode)
        {
            _currentLangCode = langCode;
            if (langCode == "EN")
            {
                this.Resources["T_LoginTitle"] = "LOGIN";
                this.Resources["T_NoAccount"] = "Don't have an account? Register";
                this.Resources["T_ForgotPass"] = "Forgot password?";
                this.Resources["T_RegTitle"] = "REGISTER ACCOUNT";
                this.Resources["T_RegBtn"] = "Sign up with Google";
                this.Resources["T_BackLogin"] = "⬅ Back to login";
                this.Resources["T_FindAccTitle"] = "FIND ACCOUNT";
                this.Resources["T_FindAccDesc"] = "Enter registered Gmail to find your account.";
                this.Resources["T_FindAccBtn"] = "FIND ACCOUNT";
                this.Resources["T_Cancel"] = "⬅ Cancel";
                this.Resources["T_SelAccTitle"] = "SELECT ACCOUNT";
                this.Resources["T_SelAccDesc"] = "This Gmail is linked to multiple Usernames. Please select an account.";
                this.Resources["T_Back"] = "⬅ Back";
                this.Resources["T_OtpTitle"] = "OTP VERIFICATION";
                this.Resources["T_OtpDesc"] = "A 6-digit code has been sent to your Email.";
                this.Resources["T_OtpBtn"] = "VERIFY CODE";
                this.Resources["T_RecoveryCode"] = "Recovery Code";
                this.Resources["T_NewPassTitle"] = "NEW PASSWORD";
                this.Resources["T_NewPassLbl"] = "New Master Password";
                this.Resources["T_ConfirmPassLbl"] = "Confirm Password";
                this.Resources["T_SavePassBtn"] = "SAVE PASSWORD";
                this.Resources["T_StorageTitle"] = "Professional Data Storage";
                this.Resources["T_StorageSub"] = "Welcome to your data management and storage space";
                this.Resources["T_SaveDataBtn"] = "Save Data";
                this.Resources["T_BackFolderBtn"] = "Back To Folders";
                this.Resources["T_MenuNotif"] = "🔔 Notifications";
                this.Resources["T_MenuContact"] = "✉️ Contact Admin";
                this.Resources["T_MenuLang"] = "🌐 Language";
                this.Resources["T_MenuLogout"] = "🚪 Logout";
                this.Resources["T_CtxNewFolder"] = "📄 New Folder";
                this.Resources["T_CtxNewGroup"] = "📂 New Group";
                this.Resources["T_CtxColor"] = "🎨 Color Theme";
                this.Resources["T_CtxRename"] = "✏️ Rename";
                this.Resources["T_CtxDelete"] = "🗑️ Delete";

                _strConfirm = "CONFIRM";
                _strYes = "YES";
                _strCancel = "CANCEL";
                _strClose = "CLOSE";
            }
            else if (langCode == "JP")
            {
                this.Resources["T_LoginTitle"] = "ログイン";
                this.Resources["T_NoAccount"] = "アカウントを持っていませんか？登録";
                this.Resources["T_ForgotPass"] = "パスワードを忘れた？";
                this.Resources["T_RegTitle"] = "アカウント登録";
                this.Resources["T_RegBtn"] = "Googleで登録";
                this.Resources["T_BackLogin"] = "⬅ ログインに戻る";
                this.Resources["T_FindAccTitle"] = "アカウント検索";
                this.Resources["T_FindAccDesc"] = "登録したGmailを入力してください。";
                this.Resources["T_FindAccBtn"] = "検索する";
                this.Resources["T_Cancel"] = "⬅ キャンセル";
                this.Resources["T_SelAccTitle"] = "アカウント選択";
                this.Resources["T_SelAccDesc"] = "このGmailは複数のユーザー名にリンクされています。アカウントを選択してください。";
                this.Resources["T_Back"] = "⬅ 戻る";
                this.Resources["T_OtpTitle"] = "OTP認証";
                this.Resources["T_OtpDesc"] = "6桁のコードがメールに送信されました。";
                this.Resources["T_OtpBtn"] = "コードを確認";
                this.Resources["T_RecoveryCode"] = "リカバリーコード (Recovery Code)";
                this.Resources["T_NewPassTitle"] = "新しいパスワード";
                this.Resources["T_NewPassLbl"] = "新しいマスターパスワード";
                this.Resources["T_ConfirmPassLbl"] = "パスワードの確認";
                this.Resources["T_SavePassBtn"] = "パスワードを保存";
                this.Resources["T_StorageTitle"] = "プロフェッショナルデータストレージ";
                this.Resources["T_StorageSub"] = "データ管理およびストレージスペースへようこそ";
                this.Resources["T_SaveDataBtn"] = "データを保存";
                this.Resources["T_BackFolderBtn"] = "フォルダーに戻る";
                this.Resources["T_MenuNotif"] = "🔔 通知";
                this.Resources["T_MenuContact"] = "✉️ 管理者に連絡";
                this.Resources["T_MenuLang"] = "🌐 言語";
                this.Resources["T_MenuLogout"] = "🚪 ログアウト";
                this.Resources["T_CtxNewFolder"] = "📄 新しいフォルダー";
                this.Resources["T_CtxNewGroup"] = "📂 新しいグループ";
                this.Resources["T_CtxColor"] = "🎨 色のテーマ";
                this.Resources["T_CtxRename"] = "✏️ 名前を変更";
                this.Resources["T_CtxDelete"] = "🗑️ 削除";

                _strConfirm = "確認";
                _strYes = "はい";
                _strCancel = "キャンセル";
                _strClose = "閉じる";
            }
            else if (langCode == "FR")
            {
                this.Resources["T_LoginTitle"] = "CONNEXION";
                this.Resources["T_NoAccount"] = "Pas de compte ? S'inscrire";
                this.Resources["T_ForgotPass"] = "Mot de passe oublié ?";
                this.Resources["T_RegTitle"] = "CRÉER UN COMPTE";
                this.Resources["T_RegBtn"] = "S'inscrire avec Google";
                this.Resources["T_BackLogin"] = "⬅ Retour à la connexion";
                this.Resources["T_FindAccTitle"] = "TROUVER LE COMPTE";
                this.Resources["T_FindAccDesc"] = "Entrez le Gmail enregistré pour trouver le compte.";
                this.Resources["T_FindAccBtn"] = "TROUVER LE COMPTE";
                this.Resources["T_Cancel"] = "⬅ Annuler";
                this.Resources["T_SelAccTitle"] = "SÉLECTIONNER LE COMPTE";
                this.Resources["T_SelAccDesc"] = "Ce Gmail est lié à plusieurs comptes. Veuillez en sélectionner un.";
                this.Resources["T_Back"] = "⬅ Retour";
                this.Resources["T_OtpTitle"] = "VÉRIFICATION OTP";
                this.Resources["T_OtpDesc"] = "Un code à 6 chiffres a été envoyé à votre e-mail.";
                this.Resources["T_OtpBtn"] = "VÉRIFIER LE CODE";
                this.Resources["T_RecoveryCode"] = "Code de récupération (Recovery Code)";
                this.Resources["T_NewPassTitle"] = "NOUVEAU MOT DE PASSE";
                this.Resources["T_NewPassLbl"] = "Nouveau Mot de Passe Maître";
                this.Resources["T_ConfirmPassLbl"] = "Confirmer le Mot de Passe";
                this.Resources["T_SavePassBtn"] = "ENREGISTRER";
                this.Resources["T_StorageTitle"] = "Stockage de données professionnel";
                this.Resources["T_StorageSub"] = "Bienvenue dans votre espace de gestion et de stockage";
                this.Resources["T_SaveDataBtn"] = "Sauvegarder";
                this.Resources["T_BackFolderBtn"] = "Retour aux Dossiers";
                this.Resources["T_MenuNotif"] = "🔔 Notifications";
                this.Resources["T_MenuContact"] = "✉️ Contacter l'Admin";
                this.Resources["T_MenuLang"] = "🌐 Langue";
                this.Resources["T_MenuLogout"] = "🚪 Se Déconnecter";
                this.Resources["T_CtxNewFolder"] = "📄 Nouveau Dossier";
                this.Resources["T_CtxNewGroup"] = "📂 Nouveau Groupe";
                this.Resources["T_CtxColor"] = "🎨 Thème de Couleur";
                this.Resources["T_CtxRename"] = "✏️ Renommer";
                this.Resources["T_CtxDelete"] = "🗑️ Supprimer";

                _strConfirm = "CONFIRMER";
                _strYes = "OUI";
                _strCancel = "ANNULER";
                _strClose = "FERMER";
            }
            else
            {
                this.Resources["T_LoginTitle"] = "ĐĂNG NHẬP";
                this.Resources["T_NoAccount"] = "Chưa có tài khoản? Đăng ký";
                this.Resources["T_ForgotPass"] = "Quên mật khẩu?";
                this.Resources["T_RegTitle"] = "ĐĂNG KÝ TÀI KHOẢN";
                this.Resources["T_RegBtn"] = "Đăng ký với Google";
                this.Resources["T_BackLogin"] = "⬅ Quay lại đăng nhập";
                this.Resources["T_FindAccTitle"] = "TÌM TÀI KHOẢN";
                this.Resources["T_FindAccDesc"] = "Nhập Gmail đã đăng ký để tìm tài khoản.";
                this.Resources["T_FindAccBtn"] = "TÌM TÀI KHOẢN";
                this.Resources["T_Cancel"] = "⬅ Hủy";
                this.Resources["T_SelAccTitle"] = "CHỌN TÀI KHOẢN";
                this.Resources["T_SelAccDesc"] = "Gmail này liên kết với nhiều Username. Vui lòng chọn tài khoản cần khôi phục.";
                this.Resources["T_Back"] = "⬅ Quay lại";
                this.Resources["T_OtpTitle"] = "XÁC THỰC OTP";
                this.Resources["T_OtpDesc"] = "Mã 6 số đã được gửi vào Email của bạn.";
                this.Resources["T_OtpBtn"] = "XÁC NHẬN MÃ";
                this.Resources["T_RecoveryCode"] = "Mã khôi phục (Recovery Code)";
                this.Resources["T_NewPassTitle"] = "MẬT KHẨU MỚI";
                this.Resources["T_NewPassLbl"] = "Mật khẩu chủ mới";
                this.Resources["T_ConfirmPassLbl"] = "Xác nhận mật khẩu";
                this.Resources["T_SavePassBtn"] = "LƯU MẬT KHẨU";
                this.Resources["T_StorageTitle"] = "Professional Data Storage";
                this.Resources["T_StorageSub"] = "Chào mừng bạn đến không gian quản lý và lưu trữ dữ liệu";
                this.Resources["T_SaveDataBtn"] = "Lưu dữ liệu";
                this.Resources["T_BackFolderBtn"] = "Quay lại thư mục";
                this.Resources["T_MenuNotif"] = "🔔 Thông báo";
                this.Resources["T_MenuContact"] = "✉️ Liên hệ Admin";
                this.Resources["T_MenuLang"] = "🌐 Ngôn ngữ";
                this.Resources["T_MenuLogout"] = "🚪 Đăng xuất";
                this.Resources["T_CtxNewFolder"] = "📄 Thư mục mới";
                this.Resources["T_CtxNewGroup"] = "📂 Nhóm mới";
                this.Resources["T_CtxColor"] = "🎨 Màu sắc";
                this.Resources["T_CtxRename"] = "✏️ Đổi tên";
                this.Resources["T_CtxDelete"] = "🗑️ Xóa";

                _strConfirm = "XÁC NHẬN";
                _strYes = "CÓ";
                _strCancel = "HỦY BỎ";
                _strClose = "ĐÓNG";
            }
        }

        private void ShowLanguageDialog()
        {
            var dialog = new Window { Title = "Language", Width = 380, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ShowInTaskbar = false };
            var border = new Border { CornerRadius = new CornerRadius(15), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0A1628")), BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#005BFF")), BorderThickness = new Thickness(2), Padding = new Thickness(25), Margin = new Thickness(15) };
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = (Color)ColorConverter.ConvertFromString("#00D4FF"), BlurRadius = 25, ShadowDepth = 0, Opacity = 0.5 };

            var stack = new StackPanel();
            string dialogTitle = _currentLangCode == "EN" ? "SELECT LANGUAGE" : (_currentLangCode == "JP" ? "言語を選択" : (_currentLangCode == "FR" ? "CHOISIR LA LANGUE" : "CHỌN NGÔN NGỮ"));
            stack.Children.Add(new TextBlock { Text = $"🌐 {dialogTitle}", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 20) });

            void AddLangBtn(string text, string code)
            {
                var btn = new Button { Content = text, Height = 45, Margin = new Thickness(0, 0, 0, 10), Foreground = Brushes.White, FontSize = 16, Cursor = Cursors.Hand, BorderThickness = new Thickness(0) };
                btn.Background = (_currentLangCode == code) ? new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#00D4FF"), (Color)ColorConverter.ConvertFromString("#005BFF"), 0) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A3A6B"));
                btn.Template = CreateRoundButtonTemplate();
                btn.Click += (s, e) => { ChangeLanguage(code); dialog.Close(); };
                stack.Children.Add(btn);
            }

            AddLangBtn("🇻🇳 Tiếng Việt", "VN");
            AddLangBtn("🇬🇧 English", "EN");
            AddLangBtn("🇯🇵 日本語", "JP");
            AddLangBtn("🇫🇷 Français", "FR");

            var closeBtn = new Button { Content = _strClose, Width = 120, Height = 40, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0) };
            closeBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A2A4A"));
            closeBtn.Template = CreateRoundButtonTemplate();
            closeBtn.Click += (s, ev) => dialog.Close();

            stack.Children.Add(closeBtn);
            border.Child = stack;
            dialog.Content = border;
            dialog.ShowDialog();
        }

        private async void ShowNotificationDialog()
        {
            await SyncBroadcastNotificationsAsync(true);

            var dialog = new Window { Title = "Thông báo", Width = 420, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ShowInTaskbar = false };
            var border = new Border { CornerRadius = new CornerRadius(15), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0A1628")), BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#005BFF")), BorderThickness = new Thickness(2), Padding = new Thickness(25), Margin = new Thickness(15) };
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = (Color)ColorConverter.ConvertFromString("#00D4FF"), BlurRadius = 25, ShadowDepth = 0, Opacity = 0.5 };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = this.Resources["T_MenuNotif"]?.ToString() ?? "Thông báo", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 15) });

            if (_notifications.Count == 0)
            {
                stack.Children.Add(new TextBlock { Text = _currentLangCode == "EN" ? "No notifications." : "Không có thông báo nào cả.", Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#88CCFF")), FontSize = 14, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 25) });
            }
            else
            {
                foreach (var note in _notifications)
                {
                    var noteBorder = new Border { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A2A4A")), BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334DC9F6")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 15) };
                    noteBorder.Child = new TextBlock { Text = note, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 22 };
                    stack.Children.Add(noteBorder);
                }
            }

            var closeBtn = new Button { Content = _strClose, Width = 120, Height = 40, Cursor = Cursors.Hand, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0) };
            closeBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A3A6B"));
            closeBtn.Template = CreateRoundButtonTemplate();
            closeBtn.Click += (s, ev) => dialog.Close();

            stack.Children.Add(closeBtn);
            border.Child = stack;
            dialog.Content = border;

            _hasUnreadNotifications = false;
            SettingsRedDot.Visibility = Visibility.Hidden;
            MenuRedDot.Visibility = Visibility.Hidden;

            string safeUsername = GetSafeKey(_currentUsername);
            _ = CloudPut($"Vaults/{safeUsername}/{_notificationVersion}", "true");

            dialog.ShowDialog();
        }

        private void ShowContactAdminDialog()
        {
            var dialog = new Window { Title = "Liên hệ Admin", Width = 500, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ShowInTaskbar = false };
            var border = new Border { CornerRadius = new CornerRadius(15), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0A1628")), BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#005BFF")), BorderThickness = new Thickness(2), Padding = new Thickness(25), Margin = new Thickness(15) };
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = (Color)ColorConverter.ConvertFromString("#00D4FF"), BlurRadius = 25, ShadowDepth = 0, Opacity = 0.5 };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = this.Resources["T_MenuContact"]?.ToString() ?? "Liên hệ Admin", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 15) });

            string desc = _currentLangCode == "EN" ? "Please follow these steps:\n1. Click the Copy button next to the info.\n2. Paste the Link into your browser or call/email us." : "Bạn muốn liên hệ Admin hãy làm các bước sau đây:\n1. Bấm vào nút Copy bên cạnh thông tin.\n2. Dán Link vào trình duyệt hoặc gọi điện/gửi Email.";
            stack.Children.Add(new TextBlock { Text = desc, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#88CCFF")), FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20), LineHeight = 22 });

            void AddContactRow(string imageSource, string valueTxt)
            {
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var iconImg = new Image
                {
                    Source = new BitmapImage(new Uri(imageSource, UriKind.Absolute)),
                    Width = 26,
                    Height = 26,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Grid.SetColumn(iconImg, 0);
                grid.Children.Add(iconImg);

                var tb = new TextBox { Text = valueTxt };
                tb.Style = (Style)FindResource("ContactTextBox");
                Grid.SetColumn(tb, 1);
                grid.Children.Add(tb);

                var btn = new Button { Content = "📋 Copy", Margin = new Thickness(10, 0, 0, 0), Width = 75, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#005BFF")), Foreground = Brushes.White, Cursor = Cursors.Hand, FontWeight = FontWeights.Bold };
                btn.Template = CreateRoundButtonTemplate();
                btn.Click += (s, e) => {
                    try { Clipboard.SetText(valueTxt); ShowCustomMessage(_strConfirm, _currentLangCode == "EN" ? "Copied to clipboard!" : $"Đã sao chép:\n{valueTxt}"); } catch { }
                };
                Grid.SetColumn(btn, 2);
                grid.Children.Add(btn);

                stack.Children.Add(grid);
            }

            AddContactRow("pack://application:,,,/FBLG.png", "https://www.facebook.com/hoanglam2712004?locale=vi_VN");
            AddContactRow("pack://application:,,,/Zalo.png", "0898798124");
            AddContactRow("pack://application:,,,/Email.png", "hlmanagement.support@gmail.com");

            var closeBtn = new Button { Content = _strClose, Width = 120, Height = 40, Margin = new Thickness(0, 15, 0, 0), Cursor = Cursors.Hand, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0) };
            closeBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A3A6B"));
            closeBtn.Template = CreateRoundButtonTemplate();
            closeBtn.Click += (s, ev) => dialog.Close();

            stack.Children.Add(closeBtn);
            border.Child = stack;
            dialog.Content = border;
            dialog.ShowDialog();
        }
        #endregion

        #region HỆ THỐNG CUSTOM DIALOG VÀ ĐIỀU KHOẢN (EULA)
        private void ShowCustomMessage(string title, string message)
        {
            var dialog = new Window { Title = title, Width = 420, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
            var border = new Border { CornerRadius = new CornerRadius(15), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0A1628")), BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#005BFF")), BorderThickness = new Thickness(2), Padding = new Thickness(25), Margin = new Thickness(15) };
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = (Color)ColorConverter.ConvertFromString("#00D4FF"), BlurRadius = 25, ShadowDepth = 0, Opacity = 0.5 };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 15) });
            stack.Children.Add(new TextBlock { Text = message, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#88CCFF")), FontSize = 14, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 25), LineHeight = 22 });

            var btn = new Button { Content = _strConfirm, Width = 140, Height = 40, Cursor = Cursors.Hand, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0) };
            btn.Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#00D4FF"), (Color)ColorConverter.ConvertFromString("#005BFF"), 0);
            btn.Template = CreateRoundButtonTemplate();
            btn.Click += (s, ev) => dialog.Close();

            stack.Children.Add(btn); border.Child = stack; dialog.Content = border; dialog.ShowDialog();
        }

        private bool ShowCustomConfirm(string title, string message)
        {
            bool result = false;
            var dialog = new Window { Title = title, Width = 400, Height = 220, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
            var border = new Border { CornerRadius = new CornerRadius(15), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0A1628")), BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF005B")), BorderThickness = new Thickness(2), Padding = new Thickness(25) };
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = (Color)ColorConverter.ConvertFromString("#FF005B"), BlurRadius = 25, ShadowDepth = 0, Opacity = 0.5 };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 15) });
            stack.Children.Add(new TextBlock { Text = message, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#88CCFF")), FontSize = 14, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 25) });

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var btnYes = new Button { Content = _strYes, Width = 110, Height = 40, Cursor = Cursors.Hand, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 15, 0) };
            btnYes.Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#FF005B"), (Color)ColorConverter.ConvertFromString("#AA003D"), 0);
            btnYes.Template = CreateRoundButtonTemplate();
            btnYes.Click += (s, ev) => { result = true; dialog.Close(); };

            var btnNo = new Button { Content = _strCancel, Width = 110, Height = 40, Cursor = Cursors.Hand, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0) };
            btnNo.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A3A6B"));
            btnNo.Template = CreateRoundButtonTemplate();
            btnNo.Click += (s, ev) => { result = false; dialog.Close(); };

            btnPanel.Children.Add(btnYes); btnPanel.Children.Add(btnNo); stack.Children.Add(btnPanel);
            border.Child = stack; dialog.Content = border; dialog.ShowDialog();
            return result;
        }

        private ControlTemplate CreateRoundButtonTemplate()
        {
            string xaml = @"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>
                                <Border CornerRadius='15' Background='{TemplateBinding Background}'>
                                    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                </Border>
                            </ControlTemplate>";
            return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        private void TermsHyperlink_Click(object sender, RoutedEventArgs e)
        {
            ShowTermsDialog();
        }

        private void ShowTermsDialog()
        {
            string termsText = @"Chào mừng bạn đến HL Password Cloud. Bạn sẽ cam kết tuân thủ và chịu trách nhiệm pháp lý của người dùng qua các điều khoản:

1. TRÁCH NHIỆM BẢO MẬT TUYỆT ĐỐI
• Phần mềm hoạt động theo tiêu chuẩn không lưu trữ dữ liệu người dùng. Hệ thống KHÔNG lưu giữ mật khẩu của bạn dưới bất kỳ hình thức nào.
• Nếu bạn làm mất thông tin, DỮ LIỆU CỦA BẠN SẼ BỊ KHÓA VĨNH VIỄN. Chúng tôi sẽ không chịu trách nhiệm khôi phục dữ liệu cho bạn.
2. TÍNH HỢP PHÁP CỦA DỮ LIỆU LƯU TRỮ
• Bạn là chủ sở hữu duy nhất của dữ liệu được mã hóa trên tài khoản. Bạn cam kết chỉ lưu trữ các thông tin, tài khoản và dữ liệu thuộc quyền sở hữu hợp pháp của bạn.
• Tuyệt đối NGHIÊM CẤM sử dụng phần mềm để làm công cụ che giấu các dữ liệu phi pháp, bao gồm nhưng không giới hạn: thông tin thẻ tín dụng bị đánh cắp, tài khoản tấn công/chiếm đoạt, dữ liệu tống tiền, rửa tiền, các hoạt động khủng bố hoặc bất kỳ nội dung nào vi phạm pháp luật hiện hành.

3. BẢN QUYỀN VÀ CÁC HÀNH VI BỊ NGHIÊM CẤM TRÊN PHẦN MỀM
• Nghiêm cấm mọi hành vi dịch ngược mã nguồn, bẻ khóa, hoặc tìm cách can thiệp vào bộ nhớ hệ thống nhằm vô hiệu hóa phần mềm.
• Phần mềm này được cấp phép sử dụng. Nghiêm cấm phân phối, bán lại, sao chép hoặc thương mại hóa phần mềm dưới mọi hình thức khi chưa có văn bản chấp thuận chính thức từ nhà phát triển.
• Cấm sử dụng phần mềm làm công cụ hỗ trợ cho các cuộc tấn công mạng hoặc tích hợp trái phép vào các phần mềm độc hại khác.

4. MIỄN TRỪ TRÁCH NHIỆM PHÁP LÝ
• Phần mềm được cung cấp ""Nguyên trạng"" và không đi kèm bất kỳ bảo hành nào về tính liên tục của máy chủ bên thứ ba.
• Nhà phát triển được MIỄN TRỪ TOÀN BỘ trách nhiệm pháp lý đối với bất kỳ tổn thất dữ liệu, thiệt hại tài chính, hoặc rắc rối pháp lý nào phát sinh do hành vi lưu trữ của bạn. Việc bạn sử dụng phần mềm để lưu trữ dữ liệu là rủi ro và trách nhiệm dân sự/hình sự của riêng bạn.

5. CHẤM DỨT DỊCH VỤ VÀ HỢP TÁC VỚI CƠ QUAN PHÁP LUẬT
• Việc cố tình vi phạm bất kỳ điều khoản nào sẽ lập tức hủy bỏ quyền sử dụng phần mềm của bạn.
• Hệ thống không can thiệp vào dữ liệu của bạn, chúng tôi bảo lưu quyền chặn truy cập ứng dụng và sẵn sàng hợp tác cung cấp các nhật ký hệ thống (nếu có) cho các cơ quan thực thi pháp luật và an ninh mạng khi có yêu cầu điều tra chính thức.";

            var dialog = new Window { Title = "Điều khoản sử dụng", Width = 600, Height = 480, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ShowInTaskbar = false };
            var border = new Border { CornerRadius = new CornerRadius(15), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0A1628")), BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00D4FF")), BorderThickness = new Thickness(2), Padding = new Thickness(20), Margin = new Thickness(15) };
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = (Color)ColorConverter.ConvertFromString("#00A2FF"), BlurRadius = 25, ShadowDepth = 0, Opacity = 0.5 };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            mainGrid.Children.Add(new TextBlock { Text = "📜 ĐIỀU KHOẢN & PHÁP LÝ", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 15) });

            var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 15) };
            Grid.SetRow(scrollViewer, 1);
            var textContent = new TextBlock { Text = termsText, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#88CCFF")), FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 22, Margin = new Thickness(0, 0, 10, 0) };
            scrollViewer.Content = textContent;
            mainGrid.Children.Add(scrollViewer);

            var closeBtn = new Button { Content = "TÔI ĐÃ ĐỌC VÀ CHẤP NHẬN ĐIỀU KHOẢN", Width = 280, Height = 45, Cursor = Cursors.Hand, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0), Visibility = Visibility.Hidden };
            closeBtn.Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#00D4FF"), (Color)ColorConverter.ConvertFromString("#005BFF"), 0);
            closeBtn.Template = CreateRoundButtonTemplate();
            closeBtn.Click += (s, ev) => dialog.Close();
            Grid.SetRow(closeBtn, 2);
            mainGrid.Children.Add(closeBtn);

            scrollViewer.ScrollChanged += (s, ev) =>
            {
                if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 2)
                {
                    closeBtn.Visibility = Visibility.Visible;
                }
            };

            border.Child = mainGrid;
            dialog.Content = border;

            dialog.Loaded += (s, ev) =>
            {
                if (scrollViewer.ScrollableHeight == 0) closeBtn.Visibility = Visibility.Visible;
            };

            dialog.ShowDialog();
        }
        #endregion

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (AuthGrid.Visibility == Visibility.Visible)
                {
                    if (PnlLogin.Visibility == Visibility.Visible) AuthLogin_Click(null, null);
                    else if (PnlRegister.Visibility == Visibility.Visible) AuthRegister_Click(null, null);

                    e.Handled = true;
                }
            }
        }

        private void CreateProject_Click(object sender, RoutedEventArgs e)
        {
            if (ChkTerms.IsChecked == true)
            {
                try { File.WriteAllText(GetEulaFilePath(), "ACCEPTED"); } catch { }
            }

            HomeGrid.Visibility = Visibility.Hidden;
            AuthGrid.Visibility = Visibility.Visible;
            SwitchToPanel(PnlLogin);
        }

        #region CLOUD SYNC ENGINE
        private string GetSafeKey(string input)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(input.Trim().ToLower())).Replace("=", "");
        }

        private async Task<string?> CloudGet(string path)
        {
            string documentKey = path.Split('/').Last();
            return await (_supabaseClient ?? throw new InvalidOperationException("Sign in with Google before accessing vault data."))
                .GetDocumentAsync(documentKey);
        }

        private async Task CloudPut(string path, string jsonData)
        {
            string documentKey = path.Split('/').Last();
            await (_supabaseClient ?? throw new InvalidOperationException("Sign in with Google before accessing vault data."))
                .PutDocumentAsync(documentKey, jsonData);
        }

        private async Task CloudDelete(string path)
        {
            string documentKey = path.Split('/').Last();
            await (_supabaseClient ?? throw new InvalidOperationException("Sign in with Google before accessing vault data."))
                .DeleteDocumentAsync(documentKey);
        }
        #endregion

        #region LUỒNG ĐĂNG NHẬP / ĐĂNG KÝ / QUÊN MẬT KHẨU

        private void SwitchToPanel(StackPanel pnl)
        {
            PnlLogin.Visibility = Visibility.Hidden;
            PnlRegister.Visibility = Visibility.Hidden;
            pnl.Visibility = Visibility.Visible;

            if (BottomProgressContainer != null) BottomProgressContainer.Visibility = Visibility.Hidden;
            EnableAuthForm(true);
        }

        private void EnableAuthForm(bool isEnabled)
        {
            PnlLogin.IsEnabled = isEnabled;
            PnlRegister.IsEnabled = isEnabled;
        }

        private void SwitchToLogin_Click(object sender, RoutedEventArgs e) => SwitchToPanel(PnlLogin);
        private void SwitchToRegister_Click(object sender, RoutedEventArgs e) => SwitchToPanel(PnlRegister);

        private Task UpdateLoginProgress(int targetValue)
        {
            BottomProgressBar.Value = targetValue;
            BottomProgressText.Text = $"{targetValue}%";
            return Task.CompletedTask;
        }

        private async void AuthRegister_Click(object? sender, RoutedEventArgs? e)
        {
            string username = RegUsername.Text.Trim();

            if (ContainsProfanity(username))
            {
                ShowCustomMessage("Vi phạm điều khoản", "Hệ thống ghi nhận bạn đã đặt tên tài khoản vi phạm điều khoản");
                return;
            }
            if (!Regex.IsMatch(username, @"^(?=.*[A-Z])(?=.*\d)[a-zA-Z0-9_\-]{8,64}$"))
            {
                ShowCustomMessage("Error", "Username phải dài tối thiểu 8 ký tự, bao gồm ít nhất 1 chữ IN HOA và 1 chữ số (chỉ chứa chữ, số, _, -)!");
                return;
            }
            EnableAuthForm(false);
            BottomProgressContainer.Visibility = Visibility.Visible;
            BottomProgressBar.Value = 0;
            BottomProgressText.Text = "0%";
            try
            {
                await UpdateLoginProgress(20);
                _supabaseClient ??= new SupabaseClient(App.Supabase);
                SupabaseSession session = await _supabaseClient.SignInWithGoogleAsync();
                if (string.IsNullOrWhiteSpace(session.Email))
                {
                    throw new InvalidOperationException("Tài khoản Google không cung cấp địa chỉ Email.");
                }

                await UpdateLoginProgress(60);
                SupabaseProfile? profile = await _supabaseClient.GetProfileAsync();
                if (profile == null)
                {
                    await _supabaseClient.CreateProfileAsync(username, session.Email);
                }
                else if (!string.Equals(profile.Username, username, StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(profile.Email, session.Email, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Tài khoản Google này đã được đăng ký với username khác.");
                }

                string? existingAccount = await CloudGet("Account");
                if (existingAccount != null)
                {
                    throw new InvalidOperationException("Tài khoản vault đã tồn tại. Hãy đăng nhập thay vì đăng ký lại.");
                }

                _currentUsername = username;
                _currentUserEmail = session.Email;
                AccountData account = CreateAccountData(username, session.Email);
                await CloudPut("Account", JsonSerializer.Serialize(account));
                await AdminLockManager.PushAccountStatusAsync(username, session.Email, 0, false, string.Empty);
                await SyncCloudToMemory(GetSafeKey(username));
                await SyncBroadcastNotificationsAsync(false);
                await UpdateLoginProgress(100);
                LoginSuccess();
                RegUsername.Clear();
            }
            catch (Exception ex)
            {
                SecureRAM_ClearKey();
                ShowCustomMessage("Đăng ký", ex.Message);
            }
            finally
            {
                EnableAuthForm(true);
                BottomProgressContainer.Visibility = Visibility.Hidden;
            }
        }

        private AccountData CreateAccountData(string username, string email)
        {
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(_dataEncryptionKey); }
            SecureRAM_LockKey();

            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] activeKey = SecureRAM_GetUnprotectedKey();
            byte[] keyFromSystem = DeriveKey(_internalRecoveryKey, salt);
            string encryptedDek = EncryptAES(activeKey, keyFromSystem);
            Array.Clear(activeKey, 0, activeKey.Length);
            Array.Clear(keyFromSystem, 0, keyFromSystem.Length);

            return new AccountData
            {
                Username = username,
                Email = email,
                SaltBase64 = Convert.ToBase64String(salt),
                EncryptedDEK_User = encryptedDek,
                EncryptedDEK_System = encryptedDek
            };
        }

        private async void AuthLogin_Click(object? sender, RoutedEventArgs? e)
        {
            await AuthSupabaseLoginAsync();
        }

        private async Task AuthSupabaseLoginAsync()
        {
            EnableAuthForm(false);
            BottomProgressContainer.Visibility = Visibility.Visible;
            BottomProgressBar.Value = 0;
            BottomProgressText.Text = "0%";
            try
            {
                await UpdateLoginProgress(20);
                _supabaseClient ??= new SupabaseClient(App.Supabase);
                SupabaseSession session = await _supabaseClient.SignInWithGoogleAsync();
                _isAdmin = AdminEmails.Contains(session.Email);

                SupabaseProfile? profile = await _supabaseClient.GetProfileAsync();
                if (profile == null)
                {
                    if (_isAdmin)
                    {
                        _hasPersonalVault = false;
                        _currentUsername = "ADMIN";
                        _currentUserEmail = session.Email;
                        await UpdateLoginProgress(100);
                        AdminLoginSuccess();
                        return;
                    }
                    throw new InvalidOperationException("Chưa có hồ sơ HLPassword cho tài khoản Google này. Hãy đăng ký trước.");
                }
                await UpdateLoginProgress(50);
                string? accountJson = await CloudGet("Account");
                if (accountJson == null)
                {
                    if (_isAdmin)
                    {
                        _hasPersonalVault = false;
                        _currentUsername = profile.Username;
                        _currentUserEmail = profile.Email;
                        await UpdateLoginProgress(100);
                        AdminLoginSuccess();
                        return;
                    }
                    throw new InvalidOperationException("Không tìm thấy dữ liệu vault của tài khoản.");
                }
                AccountData account = JsonSerializer.Deserialize<AccountData>(accountJson)
                        ?? throw new InvalidOperationException("Dữ liệu tài khoản không hợp lệ.");

                byte[] salt = Convert.FromBase64String(account.SaltBase64);
                byte[] keyFromSystem = DeriveKey(_internalRecoveryKey, salt);
                byte[]? decryptedDek = DecryptAES(account.EncryptedDEK_System, keyFromSystem);
                Array.Clear(keyFromSystem, 0, keyFromSystem.Length);
                if (decryptedDek == null || decryptedDek.Length != 32)
                {
                    throw new InvalidOperationException("Không thể mở khóa vault. Tài khoản có thể đang dùng định dạng khóa cũ không được hỗ trợ.");
                }

                _dataEncryptionKey = decryptedDek;
                SecureRAM_LockKey();
                _currentUsername = profile.Username;
                _currentUserEmail = profile.Email;
                _hasPersonalVault = true;
                var adminRecord = await AdminLockManager.GetAccountStatusAsync(_currentUsername);
                _isAccountLocked = adminRecord?.IsCurrentlyLocked ?? account.IsLocked;
                _canShowUnlockInput = AdminLockManager.IsAdminApprovedForUnlock(adminRecord);
                _currentLockReason = AdminLockManager.GetLockReason(adminRecord);
                if (adminRecord != null && (account.IsLocked != _isAccountLocked || account.LockCount != adminRecord.LockCount))
                {
                    account.IsLocked = _isAccountLocked;
                    account.LockCount = Math.Max(account.LockCount, adminRecord.LockCount);
                    await CloudPut("Account", JsonSerializer.Serialize(account));
                }

                await UpdateLoginProgress(80);
                if (!_isAccountLocked)
                {
                    await SyncCloudToMemory(GetSafeKey(_currentUsername));
                }
                await SyncBroadcastNotificationsAsync(false);
                await UpdateLoginProgress(100);
                if (_isAdmin)
                {
                    AdminLoginSuccess();
                }
                else
                {
                    LoginSuccess();
                }
            }
            catch (Exception ex)
            {
                _isAdmin = false;
                _hasPersonalVault = false;
                _currentUsername = string.Empty;
                _currentUserEmail = string.Empty;
                SecureRAM_ClearKey();
                ShowCustomMessage("Đăng nhập Supabase", ex.Message);
            }
            finally
            {
                EnableAuthForm(true);
                BottomProgressContainer.Visibility = Visibility.Hidden;
            }
        }

        private async Task SyncCloudToMemory(string safeUsername)
        {
            var docs = await (_supabaseClient ?? throw new InvalidOperationException("Sign in with Google before accessing vault data."))
                .GetDocumentsAsync(new[] { _notificationVersion, "Tree", "Passwords" });

            docs.TryGetValue(_notificationVersion, out string? notifState);
            _hasUnreadNotifications = (notifState != "true");

            docs.TryGetValue("Tree", out string? cloudTree);
            string rawStr = string.Empty;
            if (cloudTree != null)
            {
                try { rawStr = JsonSerializer.Deserialize<string>(cloudTree) ?? string.Empty; } catch { }
            }

            if (!string.IsNullOrEmpty(rawStr))
            {
                string decryptedTree = DecryptDataString(rawStr);
                if (!string.IsNullOrEmpty(decryptedTree))
                {
                    try
                    {
                        TreeStructure = JsonSerializer.Deserialize<List<FolderNode>>(decryptedTree) ?? new List<FolderNode>();
                    }
                    catch
                    {
                        TreeStructure = new List<FolderNode>();
                    }
                }
                else
                {
                    TreeStructure = new List<FolderNode>();
                }
            }
            else
            {
                TreeStructure = new List<FolderNode>();
            }

            docs.TryGetValue("Passwords", out string? cloudPasswords);
            if (cloudPasswords != null)
            {
                try
                {
                    _inMemoryPasswords = JsonSerializer.Deserialize<Dictionary<string, string>>(cloudPasswords) ?? new Dictionary<string, string>();
                }
                catch
                {
                    _inMemoryPasswords = new Dictionary<string, string>();
                }
            }
            else
            {
                _inMemoryPasswords = new Dictionary<string, string>();
            }
        }

        private void LoginSuccess()
        {
            _folderViolationCount = 0;

            AuthGrid.Visibility = Visibility.Hidden;
            FolderGrid.Visibility = Visibility.Visible;
            UserEmailText.Text = _currentUsername;
            BtnSettingsContainer.Visibility = Visibility.Visible;

            SettingsRedDot.Visibility = _hasUnreadNotifications ? Visibility.Visible : Visibility.Hidden;
            MenuRedDot.Visibility = _hasUnreadNotifications ? Visibility.Visible : Visibility.Hidden;

            if (_isAccountLocked)
            {
                ApplyAccountLockUI(true, _canShowUnlockInput);
            }
            else
            {
                ApplyAccountLockUI(false, false);
                LoadTreeStructure();
            }

            BgAnimationCanvas.Opacity = 0.3;
            try
            {
                var floatingAnimation = (System.Windows.Media.Animation.Storyboard)this.BgAnimationCanvas.FindResource("FloatingAnimation");
                floatingAnimation.Begin(BgAnimationCanvas, true);
            }
            catch { }

            StartRemoteLockMonitor();
        }

        private async void Logout_Execute()
        {
            StopRemoteLockMonitor();
            if (_supabaseClient != null)
            {
                try
                {
                    await _supabaseClient.SignOutAsync();
                }
                catch (Exception ex)
                {
                    LogError(ex);
                    ShowCustomMessage("Đăng xuất", "Đã xóa phiên đăng nhập trên thiết bị nhưng không thể xác nhận đăng xuất khỏi Supabase.");
                }
                finally
                {
                    _supabaseClient = null;
                }
            }
            SecureRAM_ClearKey();

            _folderViolationCount = 0;
            _isAccountLocked = false;
            _canShowUnlockInput = false;
            _isAdmin = false;
            _hasPersonalVault = false;
            _adminAllAccounts.Clear();
            _adminBroadcasts.Clear();
            _currentLockReason = "Vi phạm quy tắc tiêu chuẩn cộng đồng";
            ApplyAccountLockUI(false, false);

            _currentUsername = ""; UserEmailText.Text = ""; BtnSettingsContainer.Visibility = Visibility.Hidden;
            BtnMenuAdminDashboard.Visibility = Visibility.Collapsed;
            SepMenuAdminDashboard.Visibility = Visibility.Collapsed;
            BtnAdminToVault.Visibility = Visibility.Collapsed;
            TreeStructure.Clear();
            _inMemoryPasswords.Clear();

            BgAnimationCanvas.Opacity = 0;
            try
            {
                var floatingAnimation = (System.Windows.Media.Animation.Storyboard)this.BgAnimationCanvas.FindResource("FloatingAnimation");
                floatingAnimation.Stop(BgAnimationCanvas);
            }
            catch { }

            FolderGrid.Visibility = Visibility.Hidden; PasswordGrid.Visibility = Visibility.Hidden;
            AdminDashboardGrid.Visibility = Visibility.Hidden;
            HomeGrid.Visibility = Visibility.Visible;
            FolderListBox.Items.Clear();
            AdminAccountsListBox.Items.Clear();
            AdminBroadcastsListBox.Items.Clear();
        }

        #endregion

        #region ADMIN DASHBOARD
        private async void AdminLoginSuccess()
        {
            _folderViolationCount = 0;
            AuthGrid.Visibility = Visibility.Hidden;
            FolderGrid.Visibility = Visibility.Hidden;
            PasswordGrid.Visibility = Visibility.Hidden;
            AdminDashboardGrid.Visibility = Visibility.Visible;

            UserEmailText.Text = $"ADMIN • {_currentUserEmail}";
            AdminSubtitleText.Text = $"Đăng nhập với quyền Quản trị viên ({_currentUserEmail})";
            BtnSettingsContainer.Visibility = Visibility.Visible;
            BtnMenuAdminDashboard.Visibility = Visibility.Visible;
            SepMenuAdminDashboard.Visibility = Visibility.Visible;
            BtnAdminToVault.Visibility = _hasPersonalVault ? Visibility.Visible : Visibility.Collapsed;

            SettingsRedDot.Visibility = Visibility.Hidden;
            MenuRedDot.Visibility = Visibility.Hidden;

            BgAnimationCanvas.Opacity = 0.25;
            try
            {
                var floatingAnimation = (System.Windows.Media.Animation.Storyboard)this.BgAnimationCanvas.FindResource("FloatingAnimation");
                floatingAnimation.Begin(BgAnimationCanvas, true);
            }
            catch { }

            await LoadAdminDashboardAsync();
        }

        private async Task LoadAdminDashboardAsync()
        {
            BtnAdminRefresh.IsEnabled = false;
            try
            {
                var (accounts, broadcasts) = await AdminLockManager.GetAllAdminDataAsync();
                _adminAllAccounts = accounts;
                _adminBroadcasts = broadcasts;

                int total = accounts.Count;
                int locked = accounts.Count(a => a.IsCurrentlyLocked);
                AdminStatTotal.Text = total.ToString();
                AdminStatActive.Text = (total - locked).ToString();
                AdminStatLocked.Text = locked.ToString();
                AdminStatBroadcasts.Text = broadcasts.Count.ToString();

                RenderAdminAccountsList();
                RenderAdminBroadcastsList();
            }
            catch (Exception ex)
            {
                LogError(ex);
                ShowCustomMessage("Admin Dashboard", $"Không thể tải dữ liệu quản trị: {ex.Message}");
            }
            finally
            {
                BtnAdminRefresh.IsEnabled = true;
            }
        }

        private void RenderAdminAccountsList()
        {
            AdminAccountsListBox.Items.Clear();
            string query = AdminSearchBox.Text.Trim();

            var filtered = string.IsNullOrEmpty(query)
                ? _adminAllAccounts
                : _adminAllAccounts.Where(a =>
                    a.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.Email.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var acc in filtered)
            {
                var card = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 9, 12, 9),
                    Background = _folderCardBgBrush,
                    BorderBrush = _folderCardBorderBrush,
                    BorderThickness = new Thickness(1)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var infoStack = new StackPanel();
                infoStack.Children.Add(new TextBlock
                {
                    Text = $"{acc.Username}  ({acc.Email})",
                    Foreground = Brushes.White,
                    FontSize = 13.5,
                    FontWeight = FontWeights.SemiBold
                });

                string detail = $"Số lần khóa: {acc.LockCount}";
                if (acc.IsCurrentlyLocked)
                {
                    if (AdminLockManager.IsAdminApprovedForUnlock(acc))
                    {
                        string otp = acc.UnlockOtp.Substring("APPROVED|".Length).Trim();
                        detail += $"  •  OTP mở khóa: {otp}";
                    }
                    else
                    {
                        detail += $"  •  Lý do: {AdminLockManager.GetLockReason(acc)}";
                    }
                }
                infoStack.Children.Add(new TextBlock
                {
                    Text = detail,
                    Foreground = _breadcrumbLinkBrush,
                    FontSize = 11.5,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(infoStack, 0);
                grid.Children.Add(infoStack);

                string statusLabel = !acc.IsCurrentlyLocked
                    ? "HOẠT ĐỘNG"
                    : (AdminLockManager.IsAdminApprovedForUnlock(acc) ? "CHỜ OTP" : "ĐÃ KHÓA");
                string badgeHex = !acc.IsCurrentlyLocked
                    ? "#00E676"
                    : (AdminLockManager.IsAdminApprovedForUnlock(acc) ? "#FFD54F" : "#FF4D6D");

                var badge = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 3, 8, 3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = CreateFrozenBrush("#22000000"),
                    BorderBrush = CreateFrozenBrush(badgeHex),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(10, 0, 0, 0)
                };
                badge.Child = new TextBlock
                {
                    Text = statusLabel,
                    Foreground = CreateFrozenBrush(badgeHex),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold
                };
                Grid.SetColumn(badge, 1);
                grid.Children.Add(badge);

                card.Child = grid;
                AdminAccountsListBox.Items.Add(new ListBoxItem { Tag = acc, Content = card });
            }
        }

        private void RenderAdminBroadcastsList()
        {
            AdminBroadcastsListBox.Items.Clear();
            foreach (var note in _adminBroadcasts)
            {
                var card = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 8, 10, 8),
                    Background = _folderCardBgBrush,
                    BorderBrush = _folderCardBorderBrush,
                    BorderThickness = new Thickness(1)
                };
                card.Child = new TextBlock
                {
                    Text = note,
                    Foreground = Brushes.White,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap
                };
                AdminBroadcastsListBox.Items.Add(new ListBoxItem { Tag = note, Content = card });
            }
        }

        private void AdminSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RenderAdminAccountsList();
        }

        private async void BtnAdminRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadAdminDashboardAsync();
        }

        private void BtnAdminToVault_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasPersonalVault) return;
            AdminDashboardGrid.Visibility = Visibility.Hidden;
            FolderGrid.Visibility = Visibility.Visible;
            if (_isAccountLocked)
            {
                ApplyAccountLockUI(true, _canShowUnlockInput);
            }
            else
            {
                ApplyAccountLockUI(false, false);
                LoadTreeStructure();
            }
        }

        private async void MenuAdminDashboard_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
            if (!_isAdmin) return;
            FolderGrid.Visibility = Visibility.Hidden;
            PasswordGrid.Visibility = Visibility.Hidden;
            AdminDashboardGrid.Visibility = Visibility.Visible;
            await LoadAdminDashboardAsync();
        }

        private LockedAccountRecord? GetSelectedAdminAccount()
        {
            if (AdminAccountsListBox.SelectedItem is ListBoxItem item && item.Tag is LockedAccountRecord rec)
            {
                return rec;
            }
            ShowCustomMessage("Admin Dashboard", "Vui lòng chọn một tài khoản trong danh sách trước.");
            return null;
        }

        private async void BtnAdminLock_Click(object sender, RoutedEventArgs e)
        {
            var rec = GetSelectedAdminAccount();
            if (rec == null) return;

            string reason = ShowInputDialog("Khóa tài khoản", $"Nhập lý do khóa tài khoản '{rec.Username}':", "Vi phạm quy tắc tiêu chuẩn cộng đồng");
            if (string.IsNullOrWhiteSpace(reason)) return;

            int nextLockCount = rec.IsCurrentlyLocked ? rec.LockCount : rec.LockCount + 1;
            bool ok = await AdminLockManager.PushAccountStatusAsync(rec.Username, rec.Email, nextLockCount, true, "REASON|" + reason.Trim());
            ShowCustomMessage("Admin Dashboard", ok ? $"Đã khóa tài khoản '{rec.Username}'." : "Cập nhật trạng thái thất bại.");
            await LoadAdminDashboardAsync();
        }

        private async void BtnAdminIssueOtp_Click(object sender, RoutedEventArgs e)
        {
            var rec = GetSelectedAdminAccount();
            if (rec == null) return;

            string defaultOtp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            string otp = ShowInputDialog("Cấp mã mở khóa (OTP)", $"Mã mở khóa cho '{rec.Username}':", defaultOtp);
            if (string.IsNullOrWhiteSpace(otp)) return;

            bool ok = await AdminLockManager.PushAccountStatusAsync(rec.Username, rec.Email, rec.LockCount, true, "APPROVED|" + otp.Trim());
            if (ok)
            {
                try { Clipboard.SetText(otp.Trim()); } catch { }
                ShowCustomMessage("Admin Dashboard", $"Đã cấp mã mở khóa '{otp.Trim()}' (đã copy vào Clipboard) cho '{rec.Username}'.");
            }
            else
            {
                ShowCustomMessage("Admin Dashboard", "Không thể cấp mã mở khóa.");
            }
            await LoadAdminDashboardAsync();
        }

        private async void BtnAdminUnlockDirect_Click(object sender, RoutedEventArgs e)
        {
            var rec = GetSelectedAdminAccount();
            if (rec == null) return;

            if (!ShowCustomConfirm("Mở khóa tài khoản", $"Bạn có chắc muốn mở khóa trực tiếp cho '{rec.Username}'?")) return;

            bool ok = await AdminLockManager.PushAccountStatusAsync(rec.Username, rec.Email, rec.LockCount, false, string.Empty);
            ShowCustomMessage("Admin Dashboard", ok ? $"Đã mở khóa tài khoản '{rec.Username}'." : "Không thể mở khóa tài khoản.");
            await LoadAdminDashboardAsync();
        }

        private async void BtnAdminSendBroadcast_Click(object sender, RoutedEventArgs e)
        {
            string message = AdminBroadcastInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(message)) return;

            var updated = new List<string>(_adminBroadcasts);
            updated.Insert(0, message);
            bool ok = await AdminLockManager.PushBroadcastNotificationsAsync(updated, _currentUserEmail);
            if (ok)
            {
                AdminBroadcastInput.Clear();
                await LoadAdminDashboardAsync();
            }
            else
            {
                ShowCustomMessage("Admin Dashboard", "Không thể gửi thông báo hệ thống.");
            }
        }

        private async void BtnAdminDeleteBroadcast_Click(object sender, RoutedEventArgs e)
        {
            if (!(AdminBroadcastsListBox.SelectedItem is ListBoxItem item) || !(item.Tag is string selectedNote))
            {
                ShowCustomMessage("Admin Dashboard", "Vui lòng chọn một thông báo để xóa.");
                return;
            }

            if (!ShowCustomConfirm("Xóa thông báo", "Bạn có chắc muốn xóa thông báo này?")) return;

            var updated = _adminBroadcasts.Where(n => n != selectedNote).ToList();
            bool ok = await AdminLockManager.PushBroadcastNotificationsAsync(updated, _currentUserEmail);
            if (ok)
            {
                await LoadAdminDashboardAsync();
            }
            else
            {
                ShowCustomMessage("Admin Dashboard", "Không thể xóa thông báo.");
            }
        }
        #endregion

        #region MÃ HÓA CẤP THẤP (AES + DEK)
        private byte[] DeriveKey(string password, byte[] salt)
        {
            return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100000, HashAlgorithmName.SHA256, 32);
        }

        private string EncryptAES(byte[]? rawData, byte[] key)
        {
            if (rawData == null || rawData.Length == 0) return string.Empty;
            byte[] iv = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(iv); }

            using (var aes = Aes.Create())
            {
                aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var ms = new MemoryStream())
                {
                    ms.Write(iv, 0, iv.Length);
                    using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write)) { cs.Write(rawData, 0, rawData.Length); }
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        private byte[]? DecryptAES(string base64Cipher, byte[] key)
        {
            try
            {
                if (string.IsNullOrEmpty(base64Cipher)) return null;
                byte[] fullCipher = Convert.FromBase64String(base64Cipher);
                if (fullCipher.Length < 16) return null;

                byte[] iv = new byte[16];
                byte[] cipher = new byte[fullCipher.Length - 16];
                Array.Copy(fullCipher, 0, iv, 0, 16);
                Array.Copy(fullCipher, 16, cipher, 0, cipher.Length);

                using (var aes = Aes.Create())
                {
                    aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    using (var ms = new MemoryStream(cipher))
                    using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read))
                    using (var dest = new MemoryStream()) { cs.CopyTo(dest); return dest.ToArray(); }
                }
            }
            catch { return null; }
        }

        private string EncryptDataString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;

            byte[] activeKey = SecureRAM_GetUnprotectedKey();
            string result = EncryptAES(Encoding.UTF8.GetBytes(plainText), activeKey);
            Array.Clear(activeKey, 0, activeKey.Length);

            return result;
        }

        private string DecryptDataString(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;

            byte[] activeKey = SecureRAM_GetUnprotectedKey();
            byte[]? dec = DecryptAES(cipherText, activeKey);
            Array.Clear(activeKey, 0, activeKey.Length);

            if (dec == null) return string.Empty;

            string result = Encoding.UTF8.GetString(dec);
            Array.Clear(dec, 0, dec.Length);
            return result;
        }
        #endregion

        #region Toggle Visibility & COPY
        private void TogglePassword_Click(object sender, RoutedEventArgs e)
        {
            IsPasswordVisible = !IsPasswordVisible;
            if (IsPasswordVisible) { PasswordBoxVisible.Text = PasswordBoxHidden.Password; PasswordBoxVisible.Visibility = Visibility.Visible; PasswordBoxHidden.Visibility = Visibility.Hidden; TogglePasswordButton.Content = "👁️‍🗨️"; }
            else { PasswordBoxHidden.Password = PasswordBoxVisible.Text; PasswordBoxVisible.Visibility = Visibility.Hidden; PasswordBoxHidden.Visibility = Visibility.Visible; TogglePasswordButton.Content = "👁️"; }
        }

        private void ToggleEmail_Click(object sender, RoutedEventArgs e)
        {
            IsEmailVisible = !IsEmailVisible;
            if (IsEmailVisible) { EmailBoxVisible.Text = EmailBoxHidden.Password; EmailBoxVisible.Visibility = Visibility.Visible; EmailBoxHidden.Visibility = Visibility.Hidden; ToggleEmailButton.Content = "👁️‍🗨️"; }
            else { EmailBoxHidden.Password = EmailBoxVisible.Text; EmailBoxVisible.Visibility = Visibility.Hidden; EmailBoxHidden.Visibility = Visibility.Visible; ToggleEmailButton.Content = "👁️"; }
        }

        private void ResetVisibilityState()
        {
            IsPasswordVisible = false; PasswordBoxVisible.Visibility = Visibility.Hidden; PasswordBoxHidden.Visibility = Visibility.Visible; TogglePasswordButton.Content = "👁️";
            IsEmailVisible = false; EmailBoxVisible.Visibility = Visibility.Hidden; EmailBoxHidden.Visibility = Visibility.Visible; ToggleEmailButton.Content = "👁️";
        }

        private void CopyUsername_Click(object sender, RoutedEventArgs e) => CopyToClipboard((Button)sender, UsernameBox.Text);
        private void CopyPassword_Click(object sender, RoutedEventArgs e) => CopyToClipboard((Button)sender, CurrentPasswordValue);
        private void CopyPhone_Click(object sender, RoutedEventArgs e) => CopyToClipboard((Button)sender, PhoneBox.Text);
        private void CopyEmail_Click(object sender, RoutedEventArgs e) => CopyToClipboard((Button)sender, CurrentEmailValue);
        private void CopyAccountType_Click(object sender, RoutedEventArgs e) => CopyToClipboard((Button)sender, AccountTypeBox.Text);

        private void CopyToClipboard(Button btn, string textToCopy)
        {
            if (string.IsNullOrEmpty(textToCopy)) return;
            try { Clipboard.SetText(textToCopy); } catch { return; }
        }
        #endregion

        #region DATA SAVING METHODS (RAM ONLY)
        private PasswordData LoadPasswordData(string id)
        {
            try
            {
                if (_inMemoryPasswords.TryGetValue(id, out string? encData) && !string.IsNullOrEmpty(encData))
                {
                    string decryptedJson = DecryptDataString(encData);
                    if (!string.IsNullOrEmpty(decryptedJson))
                    {
                        return JsonSerializer.Deserialize<PasswordData>(decryptedJson) ?? new PasswordData();
                    }
                }
            }
            catch (Exception ex) { LogError(ex); }
            return new PasswordData();
        }

        private async Task SavePasswordDataAsync(string id, PasswordData data)
        {
            string json = JsonSerializer.Serialize(data);
            string encData = EncryptDataString(json);

            _inMemoryPasswords[id] = encData;

            string safeUsername = GetSafeKey(_currentUsername);
            string allPasswordsJson = JsonSerializer.Serialize(_inMemoryPasswords);
            await CloudPut($"Vaults/{safeUsername}/Passwords", allPasswordsJson);
        }

        private async void DeleteDataFile(string id)
        {
            try
            {
                if (_inMemoryPasswords.ContainsKey(id))
                {
                    _inMemoryPasswords.Remove(id);
                    string safeUsername = GetSafeKey(_currentUsername);
                    string allPasswordsJson = JsonSerializer.Serialize(_inMemoryPasswords);
                    await CloudPut($"Vaults/{safeUsername}/Passwords", allPasswordsJson);
                }
            }
            catch { }
        }

        private async void SaveTreeStructure()
        {
            try
            {
                string json = JsonSerializer.Serialize(TreeStructure);
                string encTree = EncryptDataString(json);

                string safeUsername = GetSafeKey(_currentUsername);
                string encTreeJsonString = JsonSerializer.Serialize(encTree);
                await CloudPut($"Vaults/{safeUsername}/Tree", encTreeJsonString);
            }
            catch (Exception ex) { LogError(ex); }
        }
        #endregion

        #region Drill-down ListBox UI Building
        private void LoadTreeStructure()
        {
            try
            {
                CurrentViewGroup = null;
                NavigationHistory.Clear();
                RebuildFolderList();
            }
            catch (Exception ex)
            {
                LogError(ex);
                TreeStructure = new List<FolderNode>();
                CurrentViewGroup = null;
                NavigationHistory.Clear();
                RebuildFolderList();
            }
        }

        private void RebuildFolderList()
        {
            FolderListBox.Items.Clear();
            var listToDisplay = CurrentViewGroup == null ? TreeStructure : CurrentViewGroup.Children;

            if (listToDisplay.Count == 0 && CurrentViewGroup != null)
                NoFolderText.Visibility = Visibility.Visible;
            else
            {
                NoFolderText.Visibility = Visibility.Collapsed;
                foreach (var node in listToDisplay) FolderListBox.Items.Add(CreateListBoxItem(node));
            }

            UpdateBreadcrumbs();
        }

        private static readonly Brush _breadcrumbActiveBrush = CreateFrozenBrush("#FFFFFF");
        private static readonly Brush _breadcrumbLinkBrush = CreateFrozenBrush("#4DC9F6");
        private static readonly Brush _breadcrumbArrowBrush = CreateFrozenBrush("#005BFF");
        private static readonly Brush _folderCardBgBrush = CreateFrozenBrush("#CC132645");
        private static readonly Brush _folderGroupTextBrush = CreateFrozenBrush("#66D9FF");
        private static readonly Brush _folderDataTextBrush = CreateFrozenBrush("#FFFFFF");
        private static readonly Brush _folderCardBorderBrush = CreateFrozenFolderBorderBrush();
        private static readonly System.Windows.Media.Effects.DropShadowEffect _breadcrumbGlowEffect = CreateFrozenGlow("#00A2FF", 12, 0.8);
        private static readonly System.Windows.Media.Effects.DropShadowEffect _arrowGlowEffect = CreateFrozenGlow("#005BFF", 8, 1.0);

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private static LinearGradientBrush CreateFrozenFolderBorderBrush()
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#664DC9F6"), 0.0));
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#4400A2FF"), 0.5));
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#66005BFF"), 1.0));
            b.Freeze();
            return b;
        }

        private static System.Windows.Media.Effects.DropShadowEffect CreateFrozenGlow(string hex, double blur, double opacity)
        {
            var e = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = blur,
                ShadowDepth = 0,
                Color = (Color)ColorConverter.ConvertFromString(hex),
                Opacity = opacity
            };
            e.Freeze();
            return e;
        }

        private void UpdateBreadcrumbs()
        {
            BreadcrumbPanel.Children.Clear();
            List<FolderNode?> path = new List<FolderNode?>();
            path.Add(null);
            var historyArray = NavigationHistory.ToArray();
            Array.Reverse(historyArray);
            path.AddRange(historyArray);
            if (CurrentViewGroup != null) path.Add(CurrentViewGroup);

            for (int i = 0; i < path.Count; i++)
            {
                var node = path[i];
                bool isLast = (i == path.Count - 1);
                var tb = new TextBlock { Text = node == null ? "Home" : node.Name, Foreground = isLast ? _breadcrumbActiveBrush : _breadcrumbLinkBrush, FontSize = 18, FontWeight = isLast ? FontWeights.Bold : FontWeights.SemiBold, Cursor = isLast ? Cursors.Arrow : Cursors.Hand, Tag = node, AllowDrop = !isLast, Padding = new Thickness(5) };
                tb.Effect = _breadcrumbGlowEffect;

                if (!isLast)
                {
                    tb.MouseEnter += (s, e) => tb.Foreground = Brushes.White;
                    tb.MouseLeave += (s, e) => tb.Foreground = _breadcrumbLinkBrush;
                    tb.MouseLeftButtonDown += (s, e) => NavigateToBreadcrumb(node);
                    tb.DragOver += Breadcrumb_DragOver;
                    tb.Drop += Breadcrumb_Drop;
                }

                BreadcrumbPanel.Children.Add(tb);

                if (!isLast)
                {
                    var arrow = new TextBlock { Text = " > ", Foreground = _breadcrumbArrowBrush, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0) };
                    arrow.Effect = _arrowGlowEffect;
                    BreadcrumbPanel.Children.Add(arrow);
                }
            }
        }

        private void NavigateToBreadcrumb(FolderNode? targetNode)
        {
            if (targetNode == null) { NavigationHistory.Clear(); CurrentViewGroup = null; }
            else
            {
                while (CurrentViewGroup != null && CurrentViewGroup != targetNode)
                {
                    if (NavigationHistory.Count > 0) CurrentViewGroup = NavigationHistory.Pop();
                    else break;
                }
            }
            RebuildFolderList();
        }

        private void Breadcrumb_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(FolderNode))) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            e.Effects = DragDropEffects.Move; e.Handled = true;
        }

        private void Breadcrumb_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(FolderNode)))
            {
                var draggedNode = e.Data.GetData(typeof(FolderNode)) as FolderNode;
                if (draggedNode != null && sender is TextBlock tb)
                {
                    FolderNode? targetNode = tb.Tag as FolderNode;
                    var currentList = CurrentViewGroup == null ? TreeStructure : CurrentViewGroup.Children;
                    currentList.Remove(draggedNode);
                    var targetList = targetNode == null ? TreeStructure : targetNode.Children;
                    targetList.Add(draggedNode);
                    SaveTreeStructure();
                    RebuildFolderList();
                }
            }
        }

        private ListBoxItem CreateListBoxItem(FolderNode node)
        {
            var item = new ListBoxItem();
            item.Tag = node;
            var border = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(15, 11, 15, 11), Margin = new Thickness(0), Background = _folderCardBgBrush, BorderThickness = new Thickness(1.2), SnapsToDevicePixels = true, UseLayoutRounding = true, HorizontalAlignment = HorizontalAlignment.Stretch, BorderBrush = _folderCardBorderBrush };

            var textBlock = new TextBlock { Text = (node.IsGroup ? "📂 " : "📄 ") + node.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            if (string.IsNullOrEmpty(node.ColorHex))
            {
                textBlock.Foreground = node.IsGroup ? _folderGroupTextBrush : _folderDataTextBrush;
            }
            else
            {
                textBlock.Foreground = CreateFrozenBrush(node.ColorHex);
            }
            border.Child = textBlock;
            item.Content = border;

            if (node.IsGroup) item.ContextMenu = (ContextMenu)this.FindResource("GroupMenu");
            else item.ContextMenu = (ContextMenu)this.FindResource("DataMenu");
            return item;
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            if (child == null) return null;
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindVisualParent<T>(parentObject);
        }
        #endregion

        #region Drag and Drop Logic 
        private void FolderListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            var hit = VisualTreeHelper.HitTest(FolderListBox, e.GetPosition(FolderListBox));
            if (hit != null)
            {
                if (FindVisualParent<System.Windows.Controls.Primitives.ScrollBar>(hit.VisualHit) != null) return;
                var lbi = FindVisualParent<ListBoxItem>(hit.VisualHit);
                if (lbi == null) { if (FolderListBox.SelectedItem is ListBoxItem item) item.IsSelected = false; }
            }
        }

        private void FolderListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point mousePos = e.GetPosition(null);
                Vector diff = _dragStartPoint - mousePos;
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var lbi = FindVisualParent<ListBoxItem>((DependencyObject)e.OriginalSource);
                    if (lbi != null && lbi.Tag is FolderNode node)
                    {
                        _dragVisualWindow = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, SizeToContent = SizeToContent.WidthAndHeight, IsHitTestVisible = false, Topmost = true, ShowInTaskbar = false };
                        var border = new Border { CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(200, 26, 42, 74)), BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4DC9F6")), BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 8, 4) };
                        border.Child = new TextBlock { Text = (node.IsGroup ? "📂 " : "📄 ") + node.Name, Foreground = Brushes.White, FontWeight = FontWeights.Normal, FontSize = 12 };
                        _dragVisualWindow.Content = border;
                        Win32Point startPos = new Win32Point();
                        GetCursorPos(out startPos);
                        UpdateDragVisualPosition(startPos);
                        _dragVisualWindow.Show();
                        try { DragDrop.DoDragDrop(lbi, node, DragDropEffects.Move); }
                        finally { if (_dragVisualWindow != null) { _dragVisualWindow.Close(); _dragVisualWindow = null; } }
                    }
                }
            }
        }

        private void FolderListBox_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (_dragVisualWindow != null)
            {
                Win32Point w32Mouse = new Win32Point();
                GetCursorPos(out w32Mouse);
                UpdateDragVisualPosition(w32Mouse);
            }
        }

        private void UpdateDragVisualPosition(Win32Point w32Mouse)
        {
            PresentationSource source = PresentationSource.FromVisual(this);
            double dpiX = 1.0, dpiY = 1.0;
            if (source != null && source.CompositionTarget != null) { dpiX = source.CompositionTarget.TransformFromDevice.M11; dpiY = source.CompositionTarget.TransformFromDevice.M22; }
            if (_dragVisualWindow != null)
            {
                _dragVisualWindow.Left = w32Mouse.X * dpiX + 10;
                _dragVisualWindow.Top = w32Mouse.Y * dpiY + 10;
            }
        }

        private void FolderListBox_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(FolderNode))) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            var draggedNode = e.Data.GetData(typeof(FolderNode)) as FolderNode;
            var lbi = FindVisualParent<ListBoxItem>((DependencyObject)e.OriginalSource);
            if (lbi != null && lbi.Tag is FolderNode targetNode && draggedNode != null)
            {
                if (targetNode.IsGroup && targetNode.Id != draggedNode.Id) e.Effects = DragDropEffects.Move;
                else e.Effects = DragDropEffects.None;
            }
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void FolderListBox_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(FolderNode)))
            {
                var draggedNode = e.Data.GetData(typeof(FolderNode)) as FolderNode;
                var lbi = FindVisualParent<ListBoxItem>((DependencyObject)e.OriginalSource);
                if (lbi != null && lbi.Tag is FolderNode targetNode && draggedNode != null)
                {
                    if (targetNode.IsGroup && targetNode.Id != draggedNode.Id)
                    {
                        var currentList = CurrentViewGroup == null ? TreeStructure : CurrentViewGroup.Children;
                        currentList.Remove(draggedNode);
                        targetNode.Children.Add(draggedNode);
                        SaveTreeStructure(); RebuildFolderList();
                    }
                }
            }
        }
        #endregion

        #region Mouse Actions (Right Click & Double Click)
        private void FolderListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var hit = VisualTreeHelper.HitTest(FolderListBox, e.GetPosition(FolderListBox));
            if (hit != null)
            {
                var lbi = FindVisualParent<ListBoxItem>(hit.VisualHit);
                if (lbi != null) { lbi.IsSelected = true; lbi.Focus(); }
                else { if (FolderListBox.SelectedItem is ListBoxItem item) item.IsSelected = false; }
            }
        }

        private void FolderListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var lbi = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (lbi == null || !lbi.IsSelected) return;

            if (lbi.Tag is FolderNode node)
            {
                if (node.IsGroup)
                {
                    if (CurrentViewGroup != null) NavigationHistory.Push(CurrentViewGroup);
                    CurrentViewGroup = node; RebuildFolderList(); return;
                }

                CurrentFolderId = node.Id;
                FolderTitle.Text = node.Name;

                var data = LoadPasswordData(CurrentFolderId);
                ResetVisibilityState();

                UsernameBox.Text = data.Username;
                CurrentPasswordValue = data.Password;
                PhoneBox.Text = data.Phone;
                CurrentEmailValue = data.Email;
                AccountTypeBox.Text = data.AccountType;

                FolderGrid.Visibility = Visibility.Hidden;
                PasswordGrid.Visibility = Visibility.Visible;
            }
        }
        #endregion

        #region Context Menu Actions
        private async void MenuCreateDataFolderRoot_Click(object sender, RoutedEventArgs e)
        {
            if (_isAccountLocked) return;
            string name = ShowInputDialog(this.Resources["T_CtxNewFolder"]?.ToString() ?? "New Folder", "Enter new Folder name:");
            if (string.IsNullOrEmpty(name)) return;
            if (await CheckFolderViolationAsync(name)) return;

            var node = new FolderNode { Name = name, IsGroup = false };
            if (CurrentViewGroup == null) TreeStructure.Add(node); else CurrentViewGroup.Children.Add(node);
            await SavePasswordDataAsync(node.Id, new PasswordData()); SaveTreeStructure(); RebuildFolderList();
        }

        private async void MenuCreateGroupFolderRoot_Click(object sender, RoutedEventArgs e)
        {
            if (_isAccountLocked) return;
            string name = ShowInputDialog(this.Resources["T_CtxNewGroup"]?.ToString() ?? "New Group", "Enter new Group name:");
            if (string.IsNullOrEmpty(name)) return;
            if (await CheckFolderViolationAsync(name)) return;

            var node = new FolderNode { Name = name, IsGroup = true };
            if (CurrentViewGroup == null) TreeStructure.Add(node); else CurrentViewGroup.Children.Add(node);
            SaveTreeStructure(); RebuildFolderList();
        }

        private async void MenuCreateDataFolderChild_Click(object sender, RoutedEventArgs e)
        {
            if (_isAccountLocked) return;
            if (!(FolderListBox.SelectedItem is ListBoxItem selectedUI) || !(selectedUI.Tag is FolderNode parentNode)) return;
            string name = ShowInputDialog(this.Resources["T_CtxNewFolder"]?.ToString() ?? "New Folder", "Enter new Folder name:");
            if (string.IsNullOrEmpty(name)) return;
            if (await CheckFolderViolationAsync(name)) return;

            var childNode = new FolderNode { Name = name, IsGroup = false };
            parentNode.Children.Add(childNode);
            await SavePasswordDataAsync(childNode.Id, new PasswordData()); SaveTreeStructure(); RebuildFolderList();
        }

        private async void MenuRename_Click(object sender, RoutedEventArgs e)
        {
            if (_isAccountLocked) return;
            if (!(FolderListBox.SelectedItem is ListBoxItem selectedUI) || !(selectedUI.Tag is FolderNode node)) return;
            string name = ShowInputDialog(this.Resources["T_CtxRename"]?.ToString() ?? "Rename", "Enter new name:", node.Name);
            if (string.IsNullOrEmpty(name) || name == node.Name) return;
            if (await CheckFolderViolationAsync(name)) return;

            node.Name = name; SaveTreeStructure(); RebuildFolderList();
        }

        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_isAccountLocked) return;
            if (!(FolderListBox.SelectedItem is ListBoxItem selectedUI) || !(selectedUI.Tag is FolderNode node)) return;
            var msg = node.IsGroup ? (_currentLangCode == "EN" ? $"Delete group '{node.Name}'?" : $"Bạn có chắc muốn xóa nhóm '{node.Name}'?") : (_currentLangCode == "EN" ? $"Delete '{node.Name}'?" : $"Bạn có chắc muốn xóa '{node.Name}' không?");

            if (!ShowCustomConfirm(this.Resources["T_CtxDelete"]?.ToString() ?? "Delete", msg)) return;

            void DeleteRecursive(FolderNode n)
            {
                if (!n.IsGroup) DeleteDataFile(n.Id);
                foreach (var child in n.Children) DeleteRecursive(child);
            }
            DeleteRecursive(node);

            bool RemoveFromList(List<FolderNode> list)
            {
                if (list.Remove(node)) return true;
                foreach (var child in list) if (child.IsGroup && RemoveFromList(child.Children)) return true;
                return false;
            }
            RemoveFromList(TreeStructure);
            SaveTreeStructure(); RebuildFolderList();
        }

        private void MenuChangeColor_Click(object sender, RoutedEventArgs e)
        {
            if (_isAccountLocked) return;
            if (!(FolderListBox.SelectedItem is ListBoxItem selectedUI) || !(selectedUI.Tag is FolderNode node)) return;
            if (sender is MenuItem menuItem && menuItem.Tag is string hexCode)
            {
                node.ColorHex = hexCode; SaveTreeStructure(); RebuildFolderList();
            }
        }
        #endregion

        #region General Buttons & Dialogs
        private void Back_Click(object sender, RoutedEventArgs e)
        {
            PasswordGrid.Visibility = Visibility.Hidden;
            FolderGrid.Visibility = Visibility.Visible;
            if (FolderListBox.SelectedItem is ListBoxItem item) item.IsSelected = false;
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(CurrentFolderId)) return;

            if (!ShowCustomConfirm(this.Resources["T_SaveDataBtn"]?.ToString() ?? "Save", _currentLangCode == "EN" ? "Save changes?" : "Bạn có chắc chắn muốn lưu các thay đổi này?")) return;

            var data = new PasswordData
            {
                Username = SanitizeInput(UsernameBox.Text),
                Password = SanitizeInput(CurrentPasswordValue),
                Phone = SanitizeInput(PhoneBox.Text),
                Email = SanitizeInput(CurrentEmailValue),
                AccountType = SanitizeInput(AccountTypeBox.Text)
            };

            try
            {
                await SavePasswordDataAsync(CurrentFolderId, data);
                ShowCustomMessage("Success", _currentLangCode == "EN" ? "Data saved successfully!" : "Dữ liệu của bạn đã được lưu, vui lòng bấm xác nhận!");
            }
            catch (Exception ex)
            {
                LogError(ex);
                ShowCustomMessage("Error", _currentLangCode == "EN" ? $"Failed to save data: {ex.Message}" : $"Không thể lưu dữ liệu: {ex.Message}");
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;
        private void Close_Click(object sender, RoutedEventArgs e) => this.Close();

        private string SanitizeInput(string input) => string.IsNullOrEmpty(input) ? string.Empty : Regex.Replace(input, @"[^\x20-\x7E]", string.Empty);

        private string ShowInputDialog(string title, string instruction, string defaultText = "")
        {
            string result = string.Empty;
            var dialog = new Window { Title = title, Width = 380, Height = 200, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None, AllowsTransparency = true };
            var border = new Border { CornerRadius = new CornerRadius(15), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0A1628")), BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#005BFF")), BorderThickness = new Thickness(2), Padding = new Thickness(20) };
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = (Color)ColorConverter.ConvertFromString("#005BFF"), BlurRadius = 20, ShadowDepth = 0, Opacity = 0.5 };

            var stackPanel = new StackPanel();
            stackPanel.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Center });
            stackPanel.Children.Add(new TextBlock { Text = instruction, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#88CCFF")), FontSize = 13, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Center });

            var textBox = new TextBox { Text = defaultText, Margin = new Thickness(0, 0, 0, 20), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A2A4A")), Foreground = Brushes.White, BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4DC9F6")), BorderThickness = new Thickness(1), FontSize = 14, Padding = new Thickness(10, 8, 10, 8) };
            stackPanel.Children.Add(textBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var okBtn = new Button { Content = _strConfirm, Width = 110, Height = 38, Margin = new Thickness(0, 0, 15, 0), Foreground = Brushes.White, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
            okBtn.Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#00D4FF"), (Color)ColorConverter.ConvertFromString("#005BFF"), 0);
            okBtn.Template = CreateRoundButtonTemplate();

            var cancelBtn = new Button { Content = _strCancel, Width = 110, Height = 38, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A3A6B")), Foreground = Brushes.White, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
            cancelBtn.Template = CreateRoundButtonTemplate();

            okBtn.Click += (s, args) => { result = textBox.Text.Trim(); dialog.Close(); };
            cancelBtn.Click += (s, args) => { result = string.Empty; dialog.Close(); };
            textBox.KeyDown += (s, ev) => { if (ev.Key == Key.Enter) { ev.Handled = true; okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };

            buttonPanel.Children.Add(okBtn); buttonPanel.Children.Add(cancelBtn);
            stackPanel.Children.Add(buttonPanel); border.Child = stackPanel; dialog.Content = border; dialog.Owner = this; textBox.Focus(); textBox.SelectAll(); dialog.ShowDialog();

            return result;
        }

        private void LogError(Exception ex) { try { Debug.WriteLine($"[{DateTime.Now}] LỖI: {ex.Message}\n{ex.StackTrace}"); } catch { } }
        #endregion
    }

    public class AccountData
    {
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string SaltBase64 { get; set; } = string.Empty;
        public string EncryptedDEK_User { get; set; } = string.Empty;
        public string EncryptedDEK_System { get; set; } = string.Empty;
        public bool IsLocked { get; set; } = false;
        public int LockCount { get; set; } = 0;
        public string UnlockOtp { get; set; } = string.Empty;
    }

    public class FolderNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public bool IsGroup { get; set; } = false;
        public string ColorHex { get; set; } = string.Empty;
        public List<FolderNode> Children { get; set; } = new List<FolderNode>();
    }

    public class PasswordData
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string AccountType { get; set; } = string.Empty;
    }
}