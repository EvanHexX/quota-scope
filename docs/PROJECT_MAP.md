# Project Map

이 문서는 사람이 부르는 module 이름과 실제 file path를 연결한다.

`app-winui/`가 배포되는 app이고, `app/`는 legacy Windows Forms app이면서 두 app이 함께 compile하는 shared source의 위치다.

## App (WinUI 3, released)

- Windows tray entrypoint -> `app-winui/Program.cs`
- XAML application object -> `app-winui/App.xaml`, `app-winui/App.xaml.cs`
- tray lifecycle/polling/menu/hotkey wiring -> `app-winui/TrayController.cs`
- popup UI -> `app-winui/Windows/UsagePopupWindow.cs`
- settings window -> `app-winui/Windows/SettingsWindow.cs`
- popup palette/glass -> `app-winui/Windows/PopupPalette.cs`, `app-winui/Windows/GlassStrength.cs`, `app-winui/Windows/AcrylicBackdropHost.cs`
- tray icon host (Win32 NotifyIcon + TrackPopupMenu) -> `app-winui/Tray/TrayIconHost.cs`
- global hotkey native window -> `app-winui/Hotkeys/HotkeyWindow.cs`
- hotkey capture control -> `app-winui/Controls/HotkeyCaptureBox.cs`
- row shape/order/visibility resolution -> `app-winui/RowShapes.cs`
- EN/KO localization helper -> `app-winui/Loc.cs`
- start-with-Windows registration -> `app-winui/Autostart.cs`
- Claude login terminal launcher -> `app-winui/ClaudeLoginLauncher.cs`
- crash logging -> `app-winui/CrashLog.cs`
- platform workarounds and popup chrome notes -> `docs/WINUI3_PARITY.md`

## Shared (두 project가 모두 compile, file은 `app/` 아래)

- tray icon rendering -> `app/TrayIconRenderer.cs`
- hotkey parsing -> `app/Hotkeys/HotkeyDefinition.cs`
- executable icon asset -> `app/Assets/QuotaScope.ico`

## Legacy app (Windows Forms, not released)

- entrypoint -> `app/Program.cs`
- tray lifecycle/menu/hotkey wiring -> `app/TrayApplicationContext.cs`
- popup UI -> `app/UsagePopupForm.cs`
- global hotkey native window -> `app/HotkeyWindow.cs`

## Providers (shared)

- provider interface -> `app/Providers/IUsageProvider.cs`
- shared usage models (ProviderUsage/UsageRow) -> `app/Providers/UsageModels.cs`
- credits gauge denominator/percent helpers -> `app/Providers/CreditsGauge.cs`

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

## Settings (shared)

- settings model/load/save -> `app/AppSettings.cs`
- user settings file -> app output `settings.json` (`AppContext.BaseDirectory`), git에서 제외
