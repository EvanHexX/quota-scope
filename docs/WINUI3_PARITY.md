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

- **H.NotifyIcon.WinUI — REMOVED (2026-07-22)**. The package's internal
  `SUBCLASSPROC` delegate was garbage-collected while still registered,
  killing the entire app via `Environment.FailFast` ("callback on a garbage
  collected delegate", confirmed in the Windows Application event log; GC
  timing made it look like random silent exits, often with the settings
  window open). The `ITrayIcon` seam was swapped to the planned raw
  fallback: `TrayIconHost` now implements Shell_NotifyIcon directly with a
  rooted WndProc, TaskbarCreated re-registration, NIF_INFO notifications,
  and a native TrackPopupMenu context menu (the popup window keeps its XAML
  MenuFlyout). No third-party tray dependency remains.
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

## Lifecycle

- `Application.DispatcherShutdownMode` must be `OnExplicitShutdown`. The
  default (`OnLastWindowClose`) silently exits the whole app — tray icon
  included — as soon as the last open window closes, e.g. closing the
  settings window while the popup is hidden. This was the cause of the
  "silent exit with settings open" reports; only the tray Exit menu calls
  `Application.Exit()`.

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

## Popup layout modes

- `ShapeTheme` is `Bars`, `BentoCircles`, or `MixMatch`. Mix & match stores a
  per-row override in `RowShapes` (`"<providerId>|<row label>"` ->
  `Circle` | `Bars`, gauge by default) and the Appearance page lists every row
  the enabled providers report.
- Layout rule shared by all three modes: bar rows always span the full content
  width; gauges pair two per line only when some provider section has at least
  two gauges, otherwise everything stacks in a single column. The popup is wide
  (452 dip) for the two-column case, compact (240 dip) for a single lone gauge,
  and one-column width (408 dip) otherwise — so bars are one column wide in a
  single-column layout and two columns wide in a two-column layout.
- `LayoutColumns` (`Auto` | `OneColumn` | `TwoColumns`, surfaced in mix & match)
  overrides that heuristic when the user wants a forced column count.

## Backdrops

- Non-glass mode uses `MicaBackdrop` through `Window.SystemBackdrop`.
- Glassmorphism drives `DesktopAcrylicController` directly
  (`app-winui/Windows/AcrylicBackdropHost.cs`) instead of the XAML
  `DesktopAcrylicBackdrop` element: the element exposes no tint or luminosity
  control, so the mode only shifted colors slightly. The controller sets
  `Kind = Base`, a palette-derived tint, and low tint/luminosity opacity so the
  desktop behind the popup actually shows. `IsInputActive` stays true so the
  effect survives losing focus (pinned popups).
- In glass mode the surfaces get out of the way: the island root and outer card
  are nearly clear and the row cards themselves are rendered as glass panes —
  a soft top-down sheen gradient plus a bright 1px rim — instead of a flat film
  over the backdrop.
- `GlassStrength` (`Subtle` | `Medium` | `Strong`, picker shown while
  glassmorphism is on) drives both layers together: surface alphas and rim
  brightness for the cards, tint/luminosity opacity for the acrylic controller.
- Backdrops render as a flat fallback color when Windows "Transparency effects"
  is off or acrylic is unsupported; the Appearance page detects both
  (`UISettings.AdvancedEffectsEnabled`, `DesktopAcrylicController.IsSupported`)
  and explains it inline instead of looking broken.

## Localization

- `Loc` provides `T(en, ko)` plus `RowLabel` (duration tokens: `5h` -> `5시간`,
  `7d` -> `7일`, `1w` -> `1주`; model names stay as-is) and `Option` for
  settings values that are persisted as stable English keys.
- Language changes apply live: the tray menu is rebuilt, and the settings
  window retitles itself, relabels its navigation items, and rebuilds the
  visible page instead of requiring a reopen.

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
