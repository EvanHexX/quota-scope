# QuotaScope Notes

QuotaScope is a small Windows tray utility that shows Codex and Claude usage/rate limit information in a local popup.

These are internal implementation notes. `README.md` at the repository root is the user-facing document.

## Purpose

- Check remaining rate limit percentages quickly across providers.
- View window reset status without keeping each provider's app, CLI, or dashboard view open.
- Keep the tool as a lightweight local utility rather than a general analytics or telemetry app.

## Run

The released app is the WinUI 3 project under `app-winui/`. `app/` holds the legacy Windows Forms app plus the sources both projects compile; see `docs/PROJECT_MAP.md`.

```powershell
dotnet run --project app-winui/QuotaScope.WinUI.csproj
```

For the built-in self-tests (Codex and Claude usage mappers, the Claude session renewer, the hotkey parser, row shape resolution, and localization):

```powershell
dotnet run --project app-winui/QuotaScope.WinUI.csproj -- --self-test
```

`--self-test` is handled in `app-winui/Program.cs` before any XAML initialization, so it stays headless.

In PowerShell environments where the `codex.ps1` shim is blocked by execution policy, the app resolves the Codex command through `cmd.exe /c codex app-server`.

## Codex connection

The app does not directly use a separate OpenAI API key.

Current flow:

1. Start `codex app-server` as a child process.
2. Send `initialize` over stdio JSON-RPC.
3. Call `account/rateLimits/read`.
4. Listen for `account/rateLimits/updated` notifications and refresh the UI.

## Claude connection

The app reads the local Claude Code sign-in (`%USERPROFILE%\.claude\.credentials.json`) per poll and calls Anthropic's undocumented OAuth usage endpoint. The token is never stored, copied, or logged. See `docs/modules/claude_rate_limits.md` for the schema, required headers, and failure handling (stale cache, 429 backoff, 401 pause).

## UI behavior

- Tray icon left-click: open/close popup.
- Tray icon right-click: `Refresh`, `Toggle popup`, `Reconnect`, `Settings...`, `Exit`. Right-clicking the popup shows the same menu.
- Global hotkeys: toggle popup (default `Ctrl+Alt+U`), refresh all, and toggle pin. The latter two are unbound by default; all three are configurable in the settings window, and a binding that fails to register is reported inline rather than saved.
- The popup header holds the title, a refresh button, and a pin button. Refresh runs the same poll as the `Refresh` menu item, not `Reconnect`; its glyph spins while the poll runs.
- Pinned mode keeps the popup topmost and prevents auto-close on focus loss or `Esc`.
- The header area, including the card's top padding, is a drag handle for the borderless popup. The header buttons are excluded from it so they stay clickable.
- Clicking the time text toggles between clock time and remaining time.
- Usage rows are payload-driven: one row per rate-limit window the provider reports, labeled from the window duration with unified hour/day units (300 mins -> `5h`, 10080 mins -> `7d`).
- The popup shows one labeled section per provider (provider name + status) so Codex and Claude are visually separated.
- Rows can be hidden, reordered, and given individual shapes. The last visible row of a provider locks so a section can never render empty.
- `Spark <window>` rows (GPT-5.3-Codex-Spark), Claude per-model windows, and the `Credits` balance row are optional.
- Color themes are `Dark`, `Light`, and `Midnight`, optionally following the system theme, with an optional glassmorphism backdrop at four strengths.
- The preferred font is Pretendard/Pretendard Variable, with Segoe UI Variable and Segoe UI fallbacks.
- UI scale lays the popup out at a fixed base size inside a `Viewbox` that stretches to the scaled window, so scaling magnifies rather than reflows and cannot clip content. `docs/WINUI3_PARITY.md` records the popup chrome, DPI, and backdrop workarounds behind this.

## Settings

`settings.json` is written next to the running executable (`AppContext.BaseDirectory`). If the file does not exist, defaults are used. Every option is editable in the settings window (tray icon -> `Settings...`) and applied immediately.

Defaults:

```json
{
  "Hotkey": "Ctrl+Alt+U",
  "HotkeyRefreshAll": "",
  "HotkeyTogglePin": "",
  "WarningThresholdPercent": 20,
  "NotifyOnThreshold": true,
  "FollowSystemTheme": true,
  "ThemeOverride": "Dark",
  "Glassmorphism": false,
  "GlassStrength": "Medium",
  "ShapeTheme": "Bars",
  "LayoutColumns": "Auto",
  "RowShapes": {},
  "RowOrder": {},
  "RowVisibility": {},
  "TrayIconStyle": "UsageArc",
  "GaugeMetric": "Used",
  "UiScale": 1.0,
  "Language": "System",
  "Autostart": false,
  "PopupPosition": "BottomRight",
  "TimeDisplayMode": "ClockTime",
  "IsPinned": false,
  "Providers": {
    "codex": {
      "Enabled": true,
      "RefreshSeconds": 60,
      "ShowSecondaryRows": false,
      "ShowCredits": false,
      "Command": "codex",
      "AutoRenewSession": true
    }
  }
}
```

A `claude` provider entry is created with the same shape the first time it is read; its `Command` defaults to `codex` because `ProviderSettings` is shared, and the Claude command is resolved separately by `app/Providers/Claude/ClaudeCommandResolver.cs`.

Notes on individual keys:

- `Language` is `System`, `English`, or `한국어`.
- `PopupPosition` is one of `BottomRight`, `TopRight`, `TopLeft`, `BottomLeft`, `Center`, `NearCursor`, `LastPosition`. `LastPosition` reads `LastPopupX`/`LastPopupY`, which the popup writes when it is hidden or dragged.
- `ShapeTheme` is `Bars`, `BentoCircles`, or `MixMatch`. `LayoutColumns` (`Auto`, `OneColumn`, `TwoColumns`) is offered only for mix & match; `Auto` uses two columns only when a provider has two gauges.
- `RowShapes`, `RowOrder`, and `RowVisibility` are keyed per row as `<providerId>|<row label>`.
- `ThemeOverride` is `Dark`, `Light`, or `Midnight`, and is ignored while `FollowSystemTheme` is on.
- `GlassStrength` is `Subtle`, `Medium`, `Strong`, or `VeryStrong`, and applies only while `Glassmorphism` is on.
- `TrayIconStyle` is `UsageArc` or `Glyph`. `GaugeMetric` (`Used` or `Remaining`) drives the popup gauges and the tray arc fill; the tray state colors always key off usage.
- `UiScale` is offered as 80%-150% in the settings window and clamped to 0.7-1.6 when read.
- `RefreshSeconds` has a 10-second floor for Codex and a 60-second floor for Claude; the poll timer runs at the smallest enabled interval.
- `AutoRenewSession` is Claude-only. With it off, the app never runs `claude` in the background, and usage stops updating once the 8-hour token expires until a sign-in or reconnect.
- `ColorTheme` and `PopupGraph` are legacy keys. `ColorTheme` is read only by the Windows Forms app in `app/`; `PopupGraph` is unused. Neither affects the released app.

## Related docs

- `README.md` / `README.ko.md`: user-facing documentation.
- `docs/PROJECT_MAP.md`: source file map by module.
- `docs/WINUI3_PARITY.md`: WinUI 3 platform workarounds and popup behavior notes.
- `docs/modules/codex_rate_limits.md`: Codex app-server rate limit schema and mapping notes.
- `docs/modules/claude_rate_limits.md`: Claude usage endpoint schema and mapping notes.
- `docs/MODERNIZATION_PLAN.md`, `docs/PUBLIC_RELEASE_STATUS.md`: historical records of decisions already carried out.
