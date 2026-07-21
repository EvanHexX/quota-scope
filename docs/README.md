# QuotaScope Notes

QuotaScope is a small Windows tray utility that shows Codex app-server rate limit information in a local popup.

## Purpose

- Check remaining Codex rate limit percentage quickly.
- View the short-window and weekly-window reset status without keeping a Codex app, CLI, or dashboard view open.
- Keep the tool as a lightweight local utility rather than a general analytics or telemetry app.

## Run

```powershell
cd app
dotnet run
```

Or from the repository root:

```powershell
dotnet run --project app/QuotaScope.csproj
```

For the built-in mapper self-test:

```powershell
dotnet run --project app/QuotaScope.csproj -- --self-test
```

In PowerShell environments where the `codex.ps1` shim is blocked by execution policy, the app resolves the Codex command through `cmd.exe /c codex app-server`.

## Codex connection

The app does not directly use a separate OpenAI API key.

Current flow:

1. Start `codex app-server` as a child process.
2. Send `initialize` over stdio JSON-RPC.
3. Call `account/rateLimits/read`.
4. Listen for `account/rateLimits/updated` notifications and refresh the UI.

## UI behavior

- Tray icon click: open/close popup.
- `Ctrl+Alt+U`: open/close popup.
- Top-right pin icon: toggle pinned mode.
- Pinned mode keeps the popup topmost and prevents auto-close on focus loss or `Esc`.
- The header area is a drag handle for moving the borderless popup.
- Right-clicking the tray icon or opened popup shows the same menu:
  - `Refresh`
  - `Toggle`
  - `Settings > Codex Connection > Reconnect`
  - `Settings > Position`
  - `Settings > Time Display`
  - `Settings > Usage Rows > GPT-5.3 Spark`
  - `Settings > Shape Theme`
  - `Settings > Color Theme`
  - `Exit`
- The popup is a titlebarless dark/glass-style window.
- Color themes currently include `DarkBluePurple`, `MidnightBlack`, `Nebula`, and `Glassmorphism`.
- The outer canvas uses a transparency key.
- Default usage rows are `5h` and `1w`.
- `Spark 5h` and `Spark 1w` are optional rows.
- User-visible popup labels and connection status messages are written in English.
- Clicking the time text toggles between `Clock Time` and `Remaining Time` display.
- `Bento Circles` uses a taller circular gauge card layout. If Spark rows are enabled, it uses a 2x2 circle layout.
- The preferred font is Pretendard/Pretendard Variable, with Segoe UI fallback.
- The app sets `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` at startup to reduce DPI blur.

## Settings

The current app stores `settings.json` next to the running app output. If the file does not exist, defaults are used.

```json
{
  "hotkey": "Ctrl+Alt+U",
  "refreshSeconds": 60,
  "warningThresholdPercent": 20,
  "popupGraph": "half-circle",
  "codexCommand": "codex",
  "popupPosition": "BottomRight",
  "shapeTheme": "Bars",
  "colorTheme": "DarkBluePurple",
  "timeDisplayMode": "ClockTime",
  "isPinned": false,
  "showSparkUsage": false
}
```

`timeDisplayMode` can be `ClockTime` or `RemainingTime`.

The `hotkey` setting currently exists for future UI support. Actual registration is fixed to `Ctrl+Alt+U`.

## Related docs

- `docs/PROJECT_MAP.md`: source file map by module.
- `docs/MODERNIZATION_PLAN.md`: .NET/WinUI modernization plan.
- `docs/modules/codex_rate_limits.md`: Codex app-server rate limit schema and mapping notes.
