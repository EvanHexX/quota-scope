# Project Map

이 문서는 사람이 부르는 module 이름과 실제 file path를 연결한다.

## App

- Windows tray entrypoint -> `app/Program.cs`
- tray lifecycle/menu/hotkey wiring -> `app/TrayApplicationContext.cs`
- popup UI -> `app/UsagePopupForm.cs`
- tray icon rendering -> `app/TrayIconRenderer.cs`
- executable icon asset -> `app/Assets/QuotaScope.ico`
- global hotkey native window -> `app/HotkeyWindow.cs`

## Providers

- provider interface -> `app/Providers/IUsageProvider.cs`
- shared usage models (ProviderUsage/UsageRow) -> `app/Providers/UsageModels.cs`

## Codex Rate Limits

- Codex usage provider -> `app/Providers/Codex/CodexUsageProvider.cs`
- Codex app-server JSON-RPC client -> `app/Providers/Codex/CodexAppServerClient.cs`
- rate limit mapping -> `app/Providers/Codex/RateLimitMapper.cs`
- Codex command resolution -> `app/Providers/Codex/CodexCommandResolver.cs`
- module notes -> `docs/modules/codex_rate_limits.md`

## Claude Rate Limits

- Claude usage provider -> `app/Providers/Claude/ClaudeUsageProvider.cs`
- Claude credential reading -> `app/Providers/Claude/ClaudeCredentialReader.cs`
- Claude session auto-renew -> `app/Providers/Claude/ClaudeSessionRenewer.cs`
- Claude command resolution -> `app/Providers/Claude/ClaudeCommandResolver.cs`
- Claude usage mapping -> `app/Providers/Claude/ClaudeUsageMapper.cs`
- module notes -> `docs/modules/claude_rate_limits.md`

## Settings

- settings model/load/save -> `app/AppSettings.cs`
- user settings file -> app output `settings.json` ignored by git
