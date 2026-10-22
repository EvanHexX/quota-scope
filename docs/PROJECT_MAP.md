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
- popup row layout (visible rows in order, 2열 판단, card pairing) -> `app-winui/RowLayout.cs`
- usage alert levels, points, and per-row notification tracking -> `app-winui/UsageAlerts.cs`
- EN/KO localization helper -> `app-winui/Loc.cs`
- start-with-Windows registration -> `app-winui/Autostart.cs`
- Claude login terminal launcher -> `app-winui/ClaudeLoginLauncher.cs`
- crash logging -> `app-winui/CrashLog.cs`
- graphics adapter change detection (self-restart, microsoft-ui-xaml#10844) -> `app-winui/GraphicsAdapters.cs`
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
- self-test fixture loader/comparer, input readers -> `app/Providers/UsageFixtures.cs`
- shared mapper fixtures (입력/기대 결과 쌍, 두 project에 EmbeddedResource로 포함, macOS 포트와 공유) -> `fixtures/codex/`, `fixtures/claude/`
- shared renewer/hotkey/settings fixtures (두 project에 포함) -> `fixtures/renewer/`, `fixtures/hotkeys/`, `fixtures/settings/`
- shared row visibility/shape, usage alert, row layout, reset-time text fixtures (WinUI project에만 포함) -> `fixtures/rows/`, `fixtures/alerts/`, `fixtures/layout/`, `fixtures/resettext/`

## macOS (Swift, 소스에서 빌드)

macOS 메뉴 막대 app. Windows와 같은 기능을 갖추고 2026-10-17 수동 점검을 통과했다 (`docs/MACOS_BUILD.md`). 배포 파일은 없고 Mac에서 직접 빌드한다.

- design, build, shared fixture format, Mac smoke test -> `docs/MACOS_BUILD.md`
- Swift package (QuotaCore, QuotaProviders, test runners는 Windows에서도 빌드; QuotaScope app은 macOS 전용) -> `mac/Package.swift`
- build, self-tests, sign, install script (`mac/build.sh [--release] [--install]`) -> `mac/build.sh`
- menu bar app: status item and icon, popover and pinned panel, settings window and settings file, global hotkeys, notifications, menu bar text -> `mac/Sources/QuotaScope/` (`main.swift`, `AppController.swift`, `StatusIcon.swift`, `PopoverView.swift`, `SettingsWindow.swift`, `SettingsStore.swift`, `GlobalHotkeys.swift`, `Notifier.swift`, `DisplayText.swift`)
- shared logic port: models, Codex/Claude mappers, row shapes and layout, usage alerts, reset-time text, settings model, hotkey grammar -> `mac/Sources/QuotaCore/`
- fixture runner (`swift run quotascope-selftest`) -> `mac/Sources/quotascope-selftest/main.swift`, `mac/Sources/QuotaCore/Fixtures.swift`
- providers: Codex app-server client, Claude usage poller, session renewer, command/credential lookup (Keychain는 macOS 전용) -> `mac/Sources/QuotaProviders/`
- provider checks against fakes (`swift run quotascope-providertest`) -> `mac/Sources/quotascope-providertest/main.swift`, `mac/Sources/fake-codex-app-server/main.swift`

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

- settings model/load/save, fixture form (`Describe`) and self-test (`RunSelfTest`, `fixtures/settings/`) -> `app/AppSettings.cs`
- user settings file -> app output `settings.json` (`AppContext.BaseDirectory`), git에서 제외

## CI

- Windows: 두 C# project build와 `--self-test`; macOS: `swift build`, `quotascope-selftest`, `quotascope-providertest` -> `.github/workflows/ci.yml`
