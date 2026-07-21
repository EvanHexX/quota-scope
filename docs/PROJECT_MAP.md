# Project Map

이 문서는 사람이 부르는 module 이름과 실제 file path를 연결한다.

## App

- Windows tray entrypoint -> `app/Program.cs`
- tray lifecycle/menu/hotkey wiring -> `app/TrayApplicationContext.cs`
- popup UI -> `app/UsagePopupForm.cs`
- tray icon rendering -> `app/TrayIconRenderer.cs`
- executable icon asset -> `app/Assets/QuotaScope.ico`
- global hotkey native window -> `app/HotkeyWindow.cs`

## Codex Rate Limits

- Codex app-server JSON-RPC client -> `app/CodexAppServerClient.cs`
- rate limit DTOs -> `app/Models.cs`
- rate limit mapping -> `app/RateLimitMapper.cs`
- Codex command resolution -> `app/CodexCommandResolver.cs`
- module notes -> `docs/modules/codex_rate_limits.md`

## Settings

- settings model/load/save -> `app/AppSettings.cs`
- user settings file -> app output `settings.json` ignored by git
