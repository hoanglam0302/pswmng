# HLPassword Supabase setup

HLPassword is a Windows desktop vault using Supabase Google Auth and per-user Supabase storage. This setup is for a **new Supabase project**. It does not import or connect to old Google Drive vaults; existing users must create a new account and vault.

## Create a new Supabase project

1. Create a Supabase project and configure Google under Authentication → Providers. In Google Cloud Console → APIs & Services → Credentials, use an OAuth client of type **Web application** and add the exact Supabase callback `https://alsrsbzrymqopjnzwwij.supabase.co/auth/v1/callback` under Authorized redirect URIs. Enter that OAuth client's ID and secret in Supabase Authentication → Providers → Google. Google `redirect_uri_mismatch` means this exact callback is missing or does not match the OAuth client selected in Supabase.
2. In Supabase Authentication → URL Configuration → Redirect URLs, add `http://127.0.0.1:*/**` for the desktop PKCE callback.
3. Run `supabase/migrations/001_core_schema.sql` in the Supabase SQL Editor. It creates username profiles and user-owned vault document storage, with row-level security and case-insensitive unique usernames.
4. Copy `.env.example` to `.env` in the solution root and configure `HLPASSWORD_SUPABASE_URL` and `HLPASSWORD_SUPABASE_PUBLISHABLE_KEY` from Supabase Project Settings → API.
5. Run the application from Visual Studio, or publish a single-file Windows build with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-release.ps1
```

The publish script embeds the public URL and publishable key as assembly metadata. Never use a service-role/secret key in the desktop app or distribute `.env`.

## Account and vault behavior

Users register with a Gmail address and select a unique username. Google OAuth is the only login step; no username or vault password is requested at login. The vault data key is wrapped using the app's built-in recovery key, so access protection depends on Google authentication and Supabase RLS rather than a separate user-held vault secret. This key is embedded in the desktop app and is not a strong at-rest secret; anyone with both the app and account data may be able to recover vault encryption keys. Recover a Google account through Google if access is lost.

Vault documents are stored in `user_documents` under the authenticated Supabase user ID. RLS allows users to access only their own rows. The username is reserved in `user_profiles` with a case-insensitive unique database index. No legacy Drive access, account-claim flow, SMTP OTP, admin import tool, or service-role key is required.

Account moderation and broadcast notifications still use the existing admin service integration. Review and configure that service separately before production use.
