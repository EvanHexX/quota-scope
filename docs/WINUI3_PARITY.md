# WinUI 3 Port — Parity Checklist and Notes

Branch: `feat/winui3-shell`. The WinForms app (`app/QuotaScope.csproj`) stays
buildable and untouched until every parity item below passes. Retiring the
WinForms app is a separate follow-up task after sign-off.

## Parity checklist (all must pass before the WinForms app is replaced)

- [ ] Tray icon appears and reflects usage percent
- [ ] Tray left-click toggles the popup
- [ ] Custom hotkeys register/work and conflicts show an inline error
- [ ] Refresh / Reconnect work
- [ ] Pinned popup behavior works
- [ ] Popup positioning works in all modes (BottomRight/TopRight/TopLeft/BottomLeft/Center/NearCursor) across monitors/DPIs
- [ ] Settings persist after restart
- [ ] Codex + Claude shown together; labels/sections match the WinForms baseline
- [ ] One provider failing leaves the other working
- [ ] Startup time / memory compared against the WinForms version and recorded here
- [ ] Packaging decision recorded (framework-dependent / self-contained / MSIX)

## Dependency justifications (AGENTS.md rule: new production dependencies need documented reasons)

- **H.NotifyIcon.WinUI** — WinUI 3 has no `NotifyIcon` equivalent. A raw
  Shell_NotifyIcon implementation needs a hidden top-level window,
  `TaskbarCreated` re-registration after explorer restarts, and second-window
  hosting to show a XAML `MenuFlyout` from the tray (~250 lines of
  defect-prone interop). The package handles all of that and accepts
  `System.Drawing.Icon`. It is wrapped behind `ITrayIcon`
  (`app-winui/Tray/TrayIconHost.cs`) so a raw fallback can be swapped in
  without touching `TrayController`. Approved by the maintainer on 2026-07-22.
- **System.Drawing.Common** — reuses `app/TrayIconRenderer.cs` unmodified for
  tray icon rendering. First-party Microsoft package, Windows-only supported.
- **Microsoft.WindowsAppSDK / Microsoft.Windows.SDK.BuildTools** — the WinUI 3
  platform itself. Currently building against WindowsAppSDK 1.8.x.

## Build / deployment notes

- `dotnet build app-winui/QuotaScope.WinUI.csproj` (CLI-only, no Visual Studio
  required). Platform is pinned to x64 because WindowsAppSDK rejects AnyCPU.
- Unpackaged deployment: `WindowsPackageType=None` +
  `WindowsAppSDKSelfContained=true`. The WinAppSDK runtime is copied next to
  the exe, so no runtime installer is needed and the headless
  `QuotaScopeWinUI.exe --self-test` path never initializes XAML/WinRT.
- Shared sources (`app/AppSettings.cs`, `app/TrayIconRenderer.cs`,
  `app/Providers/**`) are compiled in via `<Compile Include>` file links.
  Those files are never edited from this project; `internal` visibility works
  because linked files compile into this assembly.
- During coexistence each exe keeps its own `settings.json` next to its output
  (both use `AppContext.BaseDirectory`). Pre-release policy: no migration.
- Windows 10 fallback: the borderless popup uses
  `DWMWA_WINDOW_ROUNDED_CORNER_PREFERENCE`, which is Windows 11-only; on
  Windows 10 corners are square. Accepted.

## Popup window chrome

- The popup keeps a logical DWM border with
  `OverlappedPresenter.SetBorderAndTitleBar(true, false)` while remaining
  non-resizable. `Window.ExtendsContentIntoTitleBar = true` removes the residual
  WinUI title-bar strip.
- `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND` supplies the Windows 11
  rounded geometry and shadow. The tested Windows 11 build reports a one-pixel
  `DWMWA_VISIBLE_FRAME_BORDER_THICKNESS` and still composites top-edge pixels
  when `DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE` is requested. The border color
  is therefore synchronized to the active card surface color so those pixels
  blend into the client surface. The outer XAML card has no separate border;
  internal dividers and row-card borders remain unchanged.
- The outer XAML card applies neither `CornerRadius` nor `BorderThickness`;
  DWM is the single owner of the window silhouette. A second XAML radius exposes
  the opaque island background between the curves, while a square one-pixel
  stroke is clipped into dark lines and disconnected corner pixels.
- A narrowly scoped `WM_NCCALCSIZE` subclass runs the default calculation and
  restores only `rgrc[0].top`, reclaiming the residual one-pixel non-client top
  strip. Never return zero for the full window rectangle here: that produced a
  dead black band between the XAML island and outer window on the tested
  fractional-DPI setup. No exact matching upstream Windows App SDK issue was
  found, so the black-band behavior remains a project observation.
- The manual drag region covers the outer card's top padding plus its header,
  excluding the pin button. This keeps every visible top-row client pixel
  draggable after the non-client strip is reclaimed.
- Once the user manually moves a visible popup, asynchronous provider refreshes
  rebuild and resize it at the current position instead of reapplying the
  configured `PopupPosition`. A refresh received during pointer capture defers
  its resize until the drag ends. A new show or an explicit settings change
  intentionally re-anchors the popup to the configured position.
- The corner and border attributes are re-applied after backdrop changes and on
  show/activation because window state transitions can restore DWM defaults.

## Tray icon design decisions (maintainer, 2026-07-22)

- Remove the numeric text from the tray icon. The signal is limited to two
  channels: arc fill ratio (quantized to 5% steps) and a 3-level state color
  (normal / warning / critical).
- Stop rendering a fixed 32x32 icon and letting the shell downscale it.
  Render at the DPI-native size (16/20/24/32 px) with per-size stroke widths.
- Unify every layer on `usedPercent` (remove `remainingPercent` from the
  models). Gauges and the tray arc fill with usage; state colors key off the
  warning threshold in used terms.
- Exact numbers live in the tooltip and popup. The tooltip is a per-provider
  5h/7d summary.
- Toast on threshold: implemented. A tray notification fires once per
  escalation (Normal -> Warning -> Critical; recovery resets silently),
  controlled by Settings > General > Threshold notification
  (`NotifyOnThreshold`, default on).
- Fallback: if the small tray icon still reads poorly, drop the arc and use a
  fixed glyph with state color only.
- Update (post-verification): visibility was judged good. Both channels are
  now user options in Settings > Appearance: `TrayIconStyle`
  (UsageArc | Glyph) and `GaugeMetric` (Used | Remaining, applied to popup
  gauges/percentages and the tray arc fill; state colors always key off
  usage). Hotkey load warnings are additionally surfaced as a tray
  notification at startup, not only on the settings page.

## Measurements (fill in during final verification)

| Metric | WinForms | WinUI 3 |
|---|---|---|
| Cold start to tray icon | | |
| Private working set after 5 min | | |
| Output folder size | | |
