# macOS Build Plan

> Status as of 2026-10-17: **a menu bar MVP runs on the Mac** (phase 4, merged from `claude/project-thread-rz0afu` at 7ef321a). It shows Codex and Claude usage, and has a settings window and `mac/build.sh`. The Mac session built and ran it on macOS 15.6.1 with Swift 6.1.2; the maintainer's smoke test (10) is still to do. On 2026-10-17 `QuotaCore` built and passed all 53 fixture cases on macOS 15.6.1 arm64 with the Command Line Tools alone, and the Codex half of phase 0 was answered (9). Phase 0 is done as far as it can be without the app (9). Phase 1 (shared fixtures, Windows side: 1a mappers, 1b renewer, hotkeys, rows, alerts) is done. Phase 2 is done too: `QuotaCore` (models, mappers, rows, alerts, settings model, hotkey grammar) is ported to Swift and passes all six fixture folders (57 cases), built and run on Windows (7.3). Phase 3 has started: `QuotaProviders` (the Codex and Claude providers, the renewer, command and credential lookup) is written and passes 42 checks against fakes on Windows and on macOS (4b3e03c; macOS 15.6.1 arm64, Command Line Tools only, Swift 6.1.2: no warnings, 57/57 fixtures, 42/42 provider checks, no fake process left behind). Two of the four checks that touch real credentials are answered from the app (the Keychain payload and its access prompt; see 7.3); `mdat` on refresh and a real `claude mcp list` renewal are still open. Items marked **(verify)** are assumptions to check on the target Mac in phase 0.

## 1. Goal and constraints

Build a macOS version of QuotaScope that:

- is **built from source on the Mac that runs it**. No Apple Developer Program membership, no Developer ID certificate, no notarization, no App Store.
- **looks and behaves like a native Mac menu bar utility**, not like a port of the Windows tray popup.
- keeps the provider whitelist (Codex, Claude), the local-only privacy model, and the "token is read per poll and never stored or logged" rule.
- leaves the released Windows app (`app-winui/`) and the legacy app (`app/`) building and behaving exactly as today. The only Windows-side change is moving self-test samples into shared fixture files (section 5).

What "no developer account" means in practice:

| Need | Without an account | Notes |
|---|---|---|
| Compiler / SDK | Xcode Command Line Tools (`xcode-select --install`), free | No .NET SDK needed on the Mac. Full Xcode is optional (debugger, SwiftUI previews). No Apple ID sign-in needed. |
| Code signature | Ad-hoc (`codesign --sign -`) or a **self-signed** local code-signing certificate | Apple Silicon refuses to run unsigned arm64 code, so the build script always signs, at least ad-hoc. |
| Gatekeeper | Not triggered | Files built locally carry no `com.apple.quarantine` attribute, so the app opens without the "unidentified developer" dialog. |
| Sharing a built `.app` with another Mac | Possible but unsupported | The copy gets quarantined; the other user must clear it (`xattr -dr com.apple.quarantine`). The README should say "build it yourself" instead. |

## 2. Architecture decision: all Swift

### Options considered

| Option | Native fit | Provider code | Toolchain on the Mac | Verdict |
|---|---|---|---|---|
| **A. SwiftUI/AppKit app, providers ported to Swift** | Best | Ported (about 1,900 lines of logic, section 4), kept in step with C# by shared fixtures | CLT only | **Recommended** |
| B. Avalonia (.NET) cross-platform UI | Weak: custom-drawn controls, no real `NSStatusItem` popover, no system materials | Reused | .NET SDK | Rejected: fails the "native UI" goal |
| C. .NET for macOS (`net10.0-macos`, AppKit bindings) | Good (real AppKit), but no SwiftUI, verbose UI code | Reused in-process | .NET SDK + `macos` workload + full Xcode | Rejected: heaviest toolchain |
| D. SwiftUI shell + shared C# core as a helper process | Best | Reused through a JSON-lines IPC | .NET SDK + CLT | Rejected (see below) |

### Why A over D

D's only advantage is that the provider code would exist once. The repository history does not show that this code changes often enough to pay for a permanent second runtime:

- `RateLimitMapper.cs` and `ClaudeUsageMapper.cs` were each touched by 8 commits. Half of them landed on 2026-07-22 during initial development. After that, changes came in three batches (2026-09-05, 2026-10-08, 2026-10-10).
- Only a few of those commits reacted to an external payload or provider behavior: the Claude `limits` array (`8b1f2f2`), generic Claude per-model windows (`d58019e`), and merging Codex sparse rolling updates (`e565761`). Of these, only `e565761` came after initial development. The rest were product features (credits gauge, `>100%`, short row names, credits alert amounts).
- The mapping logic itself is small: about 420 lines for Codex and 230 for Claude before the self-tests start. The rest of those files is samples and tests.

D's costs, on the other hand, are permanent: an IPC protocol to version, two processes to start, supervise and shut down, two binaries to sign, a .NET SDK on every building Mac, and a roughly 70 MB helper. The Keychain, process, and HTTP code that differs on macOS would still have to be written new, just in C# behind `OperatingSystem.IsMacOS()` branches inside shared Windows files.

**A "narrow D" for a quick MVP is not planned either.** The IPC layer would be thrown away when moving to A, and building it costs about as much as porting the mappers against fixtures.

The risk A creates is **drift**: the two implementations answering the same payload differently. Section 5 handles it with shared input/expected-output fixtures, and section 4.2 handles the behavior that fixtures cannot capture.

### App structure

```
QuotaScope.app
└─ Contents/
   ├─ MacOS/QuotaScope         one Swift binary: UI + providers + settings
   ├─ Resources/               AppIcon.icns, Localizable.xcstrings (en, ko), fixtures/ (for --self-test)
   └─ Info.plist               LSUIElement = YES (no Dock icon)
```

Swift package targets:

| Target | Content | Depends on |
|---|---|---|
| `QuotaCore` | Models (`UsageRow`, `ProviderUsage`, …), mappers, credits gauge, row resolution, alerts, settings model, hotkey parsing | Foundation only, no UI |
| `QuotaProviders` | Codex app-server client, Claude HTTP client, Keychain credential source, session renewer, command resolution | `QuotaCore`, Security.framework |
| `QuotaScope` (app) | Menu bar item, popover, settings window, notifications, login item, global hotkey | both, SwiftUI, AppKit |

`QuotaCore` mirrors the C# files one-to-one where it can (same type names, same case names, same comments explaining why), so a fix on one side can be found and repeated on the other.

## 3. Native UI design

Principle: use system components and system styling, and drop Windows features whose only purpose was to imitate them.

### 3.1 Menu bar item

- `NSStatusItem` with a **template image** (monochrome, adapts to light/dark menu bar and to the active/inactive state automatically).
- Usage arc: draw the arc as a template image at 18 pt using the same 5 % steps as `TrayIconRenderer`. The Windows 3-level state color does not fit a template icon; show warning/critical with a small colored badge dot instead. **Decision M3.**
- **Menu bar text** next to the icon (for example `41%`), with the value picked in Settings → General (decided 2026-10-17). New macOS-only settings key `MenuBarText`:
  - `Off`: icon only.
  - `HighestWeekly` (**default**): the highest weekly main window across enabled providers (Codex `7d`, Claude `7d` and active scoped weekly rows). Non-Pro Codex users and Claude users mostly watch the weekly limit, and on Codex Pro the weekly window is the only main one anyway.
  - `HighestOverall`: the highest main window of any length, the same value the arc draws.
  - A specific row key (`codex|7d`, `claude|7d Fable`, …): any row the user can show, listed with its display name.
  - The number follows the gauge metric setting (used or remaining), like the popup. If the chosen row is missing (provider offline, row gone), show `--` rather than silently switching rows.
- Left click toggles the popover. Right click (or Control-click) opens an `NSMenu` with Refresh, Reconnect, Settings…, Quit, mirroring the Windows tray menu.
- Hover tooltip lists remaining quota per window, as on Windows.

### 3.2 Popover

- SwiftUI view hosted in an `NSPopover` anchored to the status item (arrow, system material background, light/dark automatic). Plain `MenuBarExtra(.window)` is simpler but gives no control over right-click and pinning, so use `NSStatusItem` + `NSPopover` from AppKit with SwiftUI content.
- Provider sections with headers, rows using native controls: capsule bars (**default shape, decided 2026-10-17**, same `ShapeTheme` default `Bars` as Windows), `Gauge` (`.accessoryCircularCapacity`) for gauges, and the fill card as a rounded rectangle with a partial fill. Shapes, order, visibility, and columns come from the ported row resolution.
- Header: last update, refresh button (SF Symbol `arrow.clockwise`), pin button (`pin` / `pin.fill`).
- **Pinned** = detach the popover into a small floating panel (`NSPanel`, `.nonactivatingPanel`, `.floating` level), the native equivalent of the Windows pinned popup. Popover behavior `.transient` when unpinned.
- Every row with a reset time shows it under the bar. Clicking the time text toggles clock time (`오후 4:00 초기화` / `resets at 4:00 PM`) and remaining time (`2시간 13분 뒤 초기화` / `resets in 2h 13m`), as on Windows. The choice is also in Settings → General and is stored in the existing `TimeDisplayMode` key (`ClockTime` / `RemainingTime`). The strings follow `Loc.cs` word for word. Remaining time ticks every minute while the popup is open.
- Width: about 300 pt, one column by default. The label column must fit the longest label in both languages; today that is `전체 모델 · 주간` / `All models · Weekly`. Longer labels (a future model scope) truncate in the middle of the scope, never the window name, and show the full name in a tooltip.
- Typography: the macOS system font (SF Pro, with Apple SD Gothic Neo for Korean through the system fallback), no bundled fonts. `monospacedDigit()` for percentages and times.

### 3.3 Settings window

- SwiftUI `Settings` scene (opens with `⌘,`), toolbar-style tabs: General, Providers, Appearance, Alerts, Hotkeys, About. Same sections as the Windows settings window, laid out with `Form` + `.formStyle(.grouped)`.
- Hotkey recorder: a field that captures a key combination and shows it with Mac symbols (`⌃⌥U`).
- **Started (2026-10-17):** `mac/Sources/QuotaScope/SettingsWindow.swift`, opened from the right-click menu (Settings…, `⌘,`) and the popover's gear button. Tabs General (language, start at login, time display, menu bar text, settings file), Providers, Appearance (gauge metric, System/Light/Dark, rows to show and their order), Alerts (warning and critical levels, notifications, alert points, alerts per row), About. Changes save at once and write only the keys that changed. Shape theme and columns are under Appearance (below).
- **Shapes and columns (phase 5, 2026-10-17):** the popover draws `RowLayout.lines` as the Windows popup does: bar rows span the width, gauges (a ring with the percent inside) and fill cards (the card background fills to the percent, with a 2 pt edge) pair up per line. Widths scale the Windows ones to the popover: 300 pt for one column, 340 pt for two, 190 pt for a forced single column or a lone gauge. Appearance gains Shape theme (Bars, Gauges, Fill, Mix & match), Columns (Auto, One, Two), and a per-row shape picker in mix & match. Reset times now come from the shared `Loc.resetText` (fixtures/resettext).
- **Hotkeys (phase 5, 2026-10-17):** `GlobalHotkeys.swift` registers the three bindings (toggle popup, default `⌃⌥U`; refresh all; toggle pin) with `RegisterEventHotKey`, mapping virtual-key codes to Carbon key codes and `Win` to ⌘ (6.3). Launch follows Windows: an invalid stored string falls back to the default or unbound, and load problems become a notification and a note in the Hotkeys tab. The Hotkeys tab records a binding on click (Esc cancels, at least one of ⌃⌥⇧⌘ required) and lets the registered hotkeys go while recording; a refused binding is not saved and the previous one is restored. Keys without a Mac equivalent (F21-F24) are refused.
- **Notifications (phase 5, 2026-10-17):** `Notifier.swift` posts `UsageAlerts.buildNotification` through `UNUserNotificationCenter`, in the app language. As on Windows, `UsageAlertTracker` runs on every usage update (also with notifications off), once per poll after its last provider. Permission is asked at launch when notifications are on and never asked before, or when they are switched on; a denied permission shows a note with a System Settings button in the Alerts tab. Banners also show while the popover is open, and clicking one opens the popover. Outside an app bundle (`swift run`) the center is not used.

### 3.4 Windows feature mapping

| Windows feature | macOS equivalent | Note |
|---|---|---|
| Tray icon + popup | `NSStatusItem` + `NSPopover` | |
| Pinned popup | Detached floating `NSPanel` | |
| Themes (Dark, Light, Midnight) and follow-system | Follow system appearance; keep an override (System / Light / Dark) | Midnight has no Mac counterpart. **Decision M4.** |
| Glassmorphism + 4 strengths | System materials (`.regularMaterial` etc.) are already the default look | Drop the setting on macOS |
| UI scale 80–150 % | Drop; macOS users scale with display settings | Possibly a small/regular row density toggle |
| Global hotkeys (`Ctrl+Alt+U` default) | Carbon `RegisterEventHotKey` (no Accessibility permission needed) | Default `⌃⌥U` (same physical keys). Settings strings keep the `Ctrl+Alt+U` grammar, see 6.3 |
| Tray notifications | `UNUserNotificationCenter` | Asks for permission on first alert **(verify that an ad-hoc signed app can request it)** |
| Start with Windows (HKCU Run) | `SMAppService.mainApp.register()` (macOS 13+) | Shows up in System Settings → Login Items **(verify with ad-hoc signing)** |
| EN/KO UI (`Loc.cs`) | `Loc` in `QuotaCore`, as on Windows (**decision M14**, 2026-10-17: no String Catalog) | Language setting: System / English / Korean, applied live. Every app string goes through `Loc.t` with the `Loc.cs` wording. A String Catalog would follow only the system language, change only after a relaunch, and needs Xcode's `xcstringstool` to compile, which the Command Line Tools lack. `Info.plist` declares `en` and `ko` so system-drawn text (text field menus, the permission prompts) follows the system language |
| Reconnect opens `cmd /k claude /login` | Open Terminal.app running `claude /login` through a temporary `.command` file opened with `NSWorkspace` | Avoids the Automation permission that `osascript` would need |
| Crash log next to exe | `~/Library/Logs/QuotaScope/` | |
| Graphics adapter self-restart | Not needed | WinUI-specific (microsoft-ui-xaml#10844) |

Minimum OS: **macOS 13 Ventura** (for `SMAppService`, SwiftUI `Gauge`, `Settings` scene). Architecture: the Mac's own (arm64 or x86_64) by default.

## 4. Porting scope

### 4.1 What gets ported

Line counts are the C# logic **before** each file's self-test section.

| C# source | Logic lines | Swift target | Checked by |
|---|---:|---|---|
| `app/Providers/Codex/RateLimitMapper.cs` | ~420 | `QuotaCore` | Fixtures (5) |
| `app/Providers/Claude/ClaudeUsageMapper.cs` | ~230 | `QuotaCore` | Fixtures (5) |
| `app/Providers/CreditsGauge.cs`, `UsageModels.cs` | ~100 | `QuotaCore` | Fixtures (5) |
| `app-winui/RowShapes.cs` | ~140 | `QuotaCore` | Fixtures (5) |
| `app-winui/UsageAlerts.cs` | ~135 | `QuotaCore` | Fixtures (5) |
| `app/AppSettings.cs` (model only) | ~90 | `QuotaCore` (`Codable`) | Fixtures (5): same JSON in, same values out |
| `app/Hotkeys/HotkeyDefinition.cs` (grammar only) | ~100 | `QuotaCore` | Fixtures (5) |
| `app/Providers/Codex/CodexAppServerClient.cs` | ~335 | `QuotaProviders` | Behavior checklist (4.2) |
| `app/Providers/Claude/ClaudeUsageProvider.cs` | ~310 | `QuotaProviders` | Behavior checklist (4.2) |
| `app/Providers/Claude/ClaudeSessionRenewer.cs` | ~250 | `QuotaProviders` | Behavior checklist (4.2) + its existing self-test cases as fixtures |
| `CodexCommandResolver`, `ClaudeCommandResolver`, `ClaudeCredentialReader` | — | `QuotaProviders` | Rewritten for macOS (6.1, 6.2), not ported |

Total ported logic: **about 1,900 lines**, of which the mappers are about 650. The fixture-checked part is the deterministic one; the provider and renewer part is where real risk sits.

### 4.2 Behavior that fixtures cannot capture

The provider and renewer code encodes findings that were measured, not designed. They are documented in `docs/modules/claude_rate_limits.md` and `docs/modules/codex_rate_limits.md`, and **those documents are the specification for the port**. Each item below becomes a checklist entry in the phase 3 review, with a Swift unit test wherever the logic can run without a network or a CLI (using an injected clock and fake process runner).

Claude:

- Only `claude mcp list` makes Claude Code refresh its token; `claude auth status` does not (measured 2026-08-13, claude 2.x). **(verify on macOS)**
- Proactive renew starts at 15 minutes before `expiresAt`. A proactive nudge that changes nothing is **not** a failure: retry after 2 minutes, never give up.
- A recovery nudge (after expiry or a 401) that changes nothing **is** a failure: cooldown 2 m → 10 m → 30 m, stop after 5 in a row until the credentials change.
- After a 401, pause polling until the credentials change, then resume immediately.
- Poll at most once per 60 seconds regardless of the configured interval.
- Required `User-Agent: claude-code/…` header and `anthropic-beta: oauth-2025-04-20`.
- On failure keep the last good rows, marked stale.
- The renew child runs windowless, is killed after 30 seconds, is killed with its process tree when the app quits, and its output (the user's MCP server list) is read and discarded, never logged.
- Status text: `Renewing Claude session`, `Session expires in 12m` (15 min lead with auto-renew, 60 without), otherwise `Claude rate limit`.

Codex:

- JSON-RPC over stdio: `initialize`, `account/rateLimits/read`, listen for `account/rateLimits/updated`.
- Sparse `updated` notifications are merged into the last full read, so windows and buckets an update leaves out stay visible (`e565761`).
- Reconnect restarts the child; the child dies with the app.
- **Never call `account/rateLimitResetCredit/consume`.** It spends a rate-limit reset credit for real. QuotaScope only reads: `initialize`, `account/rateLimits/read`, and the `account/rateLimits/updated` notification. The `rateLimitResetCredits` field of a read may be shown, but nothing acts on it. The C# client calls none of the reset-credit methods today, and the Swift client must not either.

Both:

- One provider failing never affects the other.

## 5. Shared fixtures

### 5.1 Layout and format

Each case is an **input plus the expected result**, not just a sample payload. Row `Label` values are the keys under which settings are stored (`RowShapes`, `RowOrder`, `RowVisibility`, `RowAlerts`, `RowAlertPoints`), so a one-character difference between platforms would silently detach a user's settings. The expected files pin those strings.

```
fixtures/
  codex/                                done: 19 cases (phase 1a)
    weekly-only-pro.input.json          { "description", "options"?, "read": <account/rateLimits/read result> }
    weekly-only-pro.expected.json
    rolling-main-window.input.json      { "description", "read": {…}, "updates": [<account/rateLimits/updated params>, …] }
    rolling-main-window.expected.json
    …
  claude/                               done: 10 cases (phase 1a)
    limits-array.input.json             { "description", "options"?, "response": <GET /api/oauth/usage body> }
    limits-array.expected.json
    …
  renewer/                              done: 4 cases (phase 1b)
    expiry.input.json                   { "description", "now"?, "expiresAt"?: [...], "failures"?: [...], "attempts"?: [...] }
    expiry.expected.json                { "expiring"?: [bool, …], "cooldownMinutes"?: [...], "delayMinutes"?: [...] }
  hotkeys/                              done: 3 cases (phase 1b)
    invalid.input.json                  { "description", "texts": ["Ctrl+Alt+U", null, …] }
    invalid.expected.json               { "results": [{ "ok", "modifiers"?, "virtualKey"?, "formatted"?, "reparses"? }, …] }
  rows/                                 done: 9 cases (phase 1b)
    shape-mix-match.input.json          { "description", "settings"?: {…}, "rows": [{ "providerId", "label", "isPrimary" }, …] }
    shape-mix-match.expected.json       { "rows": [{ "key", "visible", "shape" }, …] }
  alerts/                               done: 14 cases (phase 1b; 2 Korean cases added in phase 5)
    credits-points.input.json           { "description", "language"?: "Korean", "settings"?, "silentFirstSight"?, "parse"?: [...], "steps"?: [...] }
    credits-points.expected.json        { "parsed"?: [...], "steps"?: [{ "alerts": [...], "notification" }, …] }
  layout/                               done: 6 cases (phase 5)
    mix-match-pairing.input.json        { "description", "settings"?, "providers": [{ "providerId", "rows": [{ "label", "isPrimary" }] }] }
    mix-match-pairing.expected.json     { "twoColumn", "sections": [{ "providerId", "lines": [{ "shape", "rows": [label, label?] }] }] }
  resettext/                            done: 1 case, 8 reset times (phase 5)
    seoul-afternoon.input.json          { "description", "now": "<ISO with the local offset>", "resetsAt": [ISO | null, …] }
    seoul-afternoon.expected.json       { "results": [{ "clock": { "en", "ko" }, "remaining": { "en", "ko" } }, …] }
  settings/                             not yet: no C# self-test to move; added with the settings model port
    *.json + *.expected.json            settings.json files, including old ones, and the values they load to
```

Every folder uses the same `<case>.input.json` / `<case>.expected.json` pairs and the same comparison (below). Each input has a `description`. Settings, wherever an input carries them, are a `settings.json` fragment with the Windows key names (`"AlertPoints"`, `"RowShapes"`, `"Providers": { "codex": { "ShowCredits": … } }`); C# loads it through `AppSettings.FromJson`, the same path `settings.json` takes, so absent keys keep their defaults.

Mapper inputs:

- `description` says what the case pins down; it replaces the comment the C# test had.
- `options.creditsFullAmount` is the configured credits full amount (default 2500 when absent). Other settings that affect mapping go in `options` too if they are ever added.
- Codex: the `updates` are merged into `read` in order with `RateLimitMapper.MergeRollingUpdate`, then the result is mapped, the same path `CodexUsageProvider` takes. Without `updates` the read is mapped as is.

Mapper expected file (the output of `UsageFixtures.Describe` in C#):

```json
{
  "providerId": "codex", "displayName": "Codex", "state": "Ok", "statusText": "Codex rate limit",
  "overallUsedPercent": 30,
  "rows": [
    { "label": "5h", "scope": null, "isPrimary": true, "usedPercent": 25,
      "resetsAt": "2030-01-01T12:00:00Z", "windowDurationMins": 300,
      "detailText": null, "beyondFull": false, "creditsLeft": null }
  ]
}
```

- Every field of `UsageRow` is listed. `usedPercent` is `null` when the row has no window (a text-only credits row). `UpdatedAt` is wall-clock time and left out.
- `resetsAt` is ISO 8601 UTC with trailing zero fractions dropped (`2026-04-11T07:00:00.528743Z`).
- Comparison: numbers within 1e-9, strings and booleans exactly, and both sides must have the same keys, so a field added on one side fails the test.

The other folders answer lists in their input with lists in the same order. A key present in the input has its answer key in the expected file, and an absent one has none, so one case can cover one function or several.

- `renewer/` (`ClaudeSessionRenewer`): `expiresAt` timestamps (null = unreadable) are checked against `now` and answered by `expiring`; `failures` (failed attempts in a row) by `cooldownMinutes`; `attempts` (`{ "renewed", "recovery", "failures" }`) by `delayMinutes`, the wait before the next attempt. A delay of `null` means the renewer has given up until the credentials file changes.
- `hotkeys/` (`HotkeyDefinition`): each text (`null` allowed) gives `{ "ok": false }`, or the binding: `modifiers` as the Win32 `MOD_*` flags (Alt 1, Ctrl 2, Shift 4, Win 8), `virtualKey` as the Windows virtual-key code, the canonical `formatted` text, and `reparses`, whether that text parses back to the same binding. macOS keeps these numbers as the stored meaning and maps them to its own key codes.
- `rows/` (`RowShapes`): each row under the case's settings gives its settings `key` (`<providerId>|<label>`), whether it is `visible`, and the `shape` it is drawn with (`Circle`, `Bars`, `Fill`). Display order (`RowOrder`) has no case yet.
- `alerts/` (`UsageAlerts`, `UsageAlertTracker`): `parse` entries (`{ "text", "credits" }`) give the parsed points and how the box writes them back, or `{ "ok": false }`. `steps` run in order through one tracker (`silentFirstSight` for one created after a self-restart); each step's `usages` are `ProviderUsage` objects in the mapper expected form (only `label` is required per row; no `usedPercent` means no window). A step's own `settings` replace the case's from that step on. Each step gives its `alerts` (`providerId`, `label`, `point`, `remaining`, `critical`) and the `notification` they make, in English (`null` when there are none). The tray and row warning colors have no case yet.

### 5.2 Rules

- **C# is the reference.** A fixture is added or changed only together with the C# behavior, in the same commit, and the expected file is reviewed by hand like code.
- A new payload shape seen in the wild becomes a new fixture first, then a fix on both sides.
- Fixtures contain **no real account data**: synthetic or redacted payloads only, same as the current self-test samples.
- Time-dependent output (remaining time, "expires in") is computed from a `"now"` value in the input, never from the wall clock.

### 5.3 Windows-side change (phase 1)

Moving the samples out of the C# self-tests is the only change to the Windows apps in this plan.

**Phase 1a, done (2026-10-17): mappers.**

- The samples in `RateLimitMapper.RunSelfTest` and `ClaudeUsageMapper.RunSelfTest` moved to `fixtures/codex/` (19 cases) and `fixtures/claude/` (10 cases). Each assertion of the old tests is now a field in an expected file, and the expected files pin every row field, which the old tests did not.
- `app/Providers/UsageFixtures.cs` (shared, compiled by both projects) loads the cases, maps each input, and compares. An empty folder or a missing expected file fails the test, so a resource that stops being embedded cannot pass silently. The first mismatch is written to stderr with its path, e.g. `fixtures/claude/limits-array: $.rows[3].label: expected "7d sonnet", got "7d Sonnet"`.
- `--self-test` also runs from a released exe, where no repository is present, so the fixtures are compiled in as `EmbeddedResource` items with logical names `fixtures/<folder>/<file>`. No files ship next to the exe. Both `app/QuotaScope.csproj` and `app-winui/QuotaScope.WinUI.csproj` embed `codex/` and `claude/`, because the legacy app runs the mapper self-tests too.
- One check stays in C#: `RunMainBucketMirrorSelfTest`. It asserts that an update moves both copies of the main bucket (`rateLimits` and its mirror in `rateLimitsByLimitId`). The mirror never becomes a row, so no fixture can see it.
- Verified: both projects build with 0 warnings and `--self-test` exits 0. Corrupting one expected value in each folder, or removing an expected file, makes it exit 1 with the message above.

**Phase 1b, done (2026-10-17): the remaining self-tests.**

- The samples in `ClaudeSessionRenewer.RunSelfTest`, `HotkeyDefinition.RunSelfTest`, `RowShapes.RunSelfTest`, and `UsageAlerts.RunSelfTest` moved to `fixtures/renewer/` (4 cases), `fixtures/hotkeys/` (3), `fixtures/rows/` (9), and `fixtures/alerts/` (12), in the formats of 5.1. Every assertion of the old tests is a value in an expected file; none stayed in C#, because each one compared data. The checks that only probed a property (a hotkey's Format→TryParse round trip, a notification body containing "450", two body lines) became full values (`reparses` on every parsed hotkey, the whole notification text).
- Sequences that mutated one settings object between checks became either separate cases (one settings each) or steps with their own `settings`, so each expected value sits next to the settings it was computed under. Alert sequences that used fresh trackers on unrelated rows became separate cases.
- The renewer's expiry checks take `now` from the input instead of `DateTimeOffset.Now`. The alerts self-test sets English before comparing notifications.
- `UsageFixtures.Run` now takes any `JsonElement → JsonNode` evaluation; the mapper form is a wrapper over it. Its input readers (`ReadSettings`, `ReadUsage`, `ReadTimestamp`, `Optional`) are shared by the four tests. `AppSettings.FromJson` is `AppSettings.Load` without the file, so fixture settings load as `settings.json` does.
- The legacy project embeds only the folders its own self-tests read: `codex/`, `claude/`, `renewer/`, `hotkeys/`. `rows/` and `alerts/` are embedded in the WinUI project only.
- Verified: both projects build with 0 warnings and `--self-test` exits 0 on both exes. Corrupting one expected value in each new folder makes it exit 1 (both exes for `renewer/` and `hotkeys/`, WinUI for `rows/` and `alerts/`), e.g. `fixtures/hotkeys/canonical-format: $.results[3].formatted: expected "Ctrl+PgDn", got "Ctrl+PageDown"`.

**Adding a case.** Write the `.input.json`, then write the `.expected.json` by hand from what the behavior should be, and run `--self-test`. A mismatch prints the first differing path. Do not copy the current output into the expected file without reading it; a fixture written that way only proves that the code does what it already does.

## 6. macOS platform code

### 6.1 Claude credentials live in the Keychain

On macOS, Claude Code stores its OAuth credentials in the login Keychain, not in `~/.claude/.credentials.json`. **Verified 2026-10-17 on macOS 15.6.1:**

- The item is a generic password in `login.keychain-db`, with service (`svce`) `Claude Code-credentials` and an **empty** account (`acct = ""`). Query it by service only: `kSecClassGenericPassword` + `kSecAttrService`, with no `kSecAttrAccount`, because an account of `""` or the user name does not match.
- `~/.claude/.credentials.json` does not exist on that Mac.
- The item has the usual creation and modification dates (`cdat`, `mdat`).

Still **(verify)**, in phase 3 with the real app:
- The payload is believed to be the same JSON as the Windows file (`claudeAiOauth.accessToken`, `expiresAt`).
- `mdat` should move when Claude Code refreshes the token.
- An attributes-only read from the app should not prompt.

- `KeychainCredentialSource` reads the item with `SecItemCopyMatching`, parses it in memory, uses the token for the one request, and drops it. Never cached, copied, or logged.
- **Change detection** (replaces the Windows `FileSystemWatcher`): poll the item's modification date every few seconds via an attributes-only query (`kSecReturnAttributes`, no `kSecReturnData`). **(verify that this does not trigger a Keychain prompt)**
- Fall back to `~/.claude/.credentials.json` with a file watcher if that file exists (Claude Code uses it when the Keychain is unavailable).

**Keychain prompts.** The item's ACL belongs to Claude Code, so the first read shows a "QuotaScope wants to use your confidential information…" prompt.

| Approach | Prompt behavior | Security | Verdict |
|---|---|---|---|
| Run `/usr/bin/security find-generic-password -s "Claude Code-credentials" -w` | "Always Allow" adds `/usr/bin/security` to the item's ACL, which survives rebuilds | Weakens the item: **any** process can then read the token silently through `security` | Not used |
| `SecItemCopyMatching` from the app | "Always Allow" trusts QuotaScope's code signature | Only QuotaScope gets access | **Used** |

With ad-hoc signing every rebuild is a new signature, so the prompt returns after each rebuild. The fix without a developer account: create a **self-signed code-signing certificate** once (Keychain Access → Certificate Assistant → Create a Certificate…, type "Code Signing", name `QuotaScope Local`, identity type "Self Signed Root"), then open it in the login keychain and set **Trust → Code Signing → Always Trust**; without that step `security find-identity -v` lists it as untrusted and the build falls back to ad-hoc. The build script signs with a `QuotaScope Local` identity by itself (or whatever `QUOTASCOPE_SIGN_IDENTITY` names), which keeps the designated requirement stable across rebuilds. **Decision M2.**

**Checked on the Mac (2026-10-17, macOS 15.6.1): the certificate does not stop the prompt after a rebuild.** The second build signed with `QuotaScope Local` (`Authority=QuotaScope Local`, `TeamIdentifier=not set`) prompted again. The likely reason is the item's partition list: an app without a Team ID is recorded by its cdhash, which changes with every build, and only a Developer ID team (`teamid:`) is stable. So each new build asks once, the answer then holds for that build across relaunches and restarts, and a user who installs a release sees it once per update. Changing the partition list of Claude Code's item (`security set-generic-password-partition-list`) would avoid it but edits another app's item and needs the keychain password, so it is not done or suggested. The self-signed identity stays as the default: it costs nothing and gives login items and notifications a stable signer.

### 6.2 Finding `codex` and `claude` from a GUI app

Apps launched from Finder or at login do **not** inherit the shell `PATH`, so `/opt/homebrew/bin`, `~/.local/bin`, npm global bins, nvm shims etc. are missing.

**Codex ships inside the OpenAI desktop app on macOS** (reported 2026-10-17 from the Agent Summit Mac work, on macOS 15.6 arm64; public sources below):

- Since the 2026-07-09 merge, updating Codex.app turns it into the new `/Applications/ChatGPT.app`, which contains Codex. It is also downloadable on its own. The older desktop app becomes `/Applications/ChatGPT Classic.app` and has no Codex. On that Mac there was no `Codex.app`, no `~/Library/Application Support/OpenAI/Codex/bin`, and no `codex` on `PATH`. The only `codex` binary was the one inside ChatGPT.app.
- The CLI sits at `<app>/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex` (codex-cli 0.162.0-alpha.17.2 there).
- Bundle IDs: the new ChatGPT.app keeps Codex's `com.openai.codex`; Classic is `com.openai.chat`. A downloaded ChatGPT.app and one updated from Codex.app have the same executable hash.
- Codex state stays in `~/.codex` (or `$CODEX_HOME`) whatever the app is called. QuotaScope never reads it; `codex app-server` does.
- Sources: OpenAI, "Moving to the new ChatGPT desktop app" (help.openai.com/en/articles/20001276); openai/codex#31944.

So the app is found by **bundle ID, not by name**.

Resolution order for `codex`:

1. The configured command if it is an absolute path.
2. Apps whose bundle ID is `com.openai.codex`: `NSWorkspace.urlsForApplications(withBundleIdentifier:)`, plus a direct check of `/Applications/ChatGPT.app`, `~/Applications/ChatGPT.app`, and a leftover `Codex.app` in either place. Read each candidate's `CFBundleIdentifier` and skip anything else (`com.openai.chat` is Classic). Use the first one whose bundled CLI path above exists and is executable, preferring the highest `CFBundleShortVersionString`.
3. The `PATH` steps below (a Homebrew or npm install of the CLI).

For `claude`, after the steps below come up empty, the copy the Claude desktop app downloads is used: `~/Library/Application Support/Claude/claude-code/<version>/<hash>/claude.app/Contents/MacOS/claude`, newest version first. It uses the same Keychain item, so signing in with it (`/login`) signs QuotaScope in too. `~/.claude/local` is searched as well. The path is undocumented and changes when the desktop app updates, so it is looked up again on every resolve (seen 2026-10-17 with 2.1.299).

Resolution order for `claude`, and steps 3 onward for `codex`:

1. The configured command if it is an absolute path.
2. Known locations: `~/.local/bin`, `/opt/homebrew/bin`, `/usr/local/bin`, `~/.npm-global/bin`, `~/.bun/bin`, the newest `~/.nvm/versions/node/*/bin`.
3. Ask the user's login shell once and cache the **path** (not the environment): `$SHELL -lc 'command -v codex'`.
4. Otherwise report "not found" with the paths tried, and let the user set the command in settings.

Node-based shims also need `node` on `PATH`; start children with the login shell's `PATH` (resolved once, kept in memory).

The Settings → Providers screen shows which `codex` was found and where it came from: "ChatGPT.app (bundled)", "PATH", or "custom". That way a missing or unexpected CLI is visible without reading logs.

Phase 0 results (2026-10-17, macOS 15.6.1 arm64, reported from the Agent Summit Mac work):

- **Bundle IDs, verified:**
  - `/Applications/ChatGPT.app` is `com.openai.codex` (version 26.1007.71326).
  - ChatGPT Classic is `com.openai.chat`.
  - The bundled `CodexCLI.app` is `com.openai.codex.cli` (TeamIdentifier 2DC432GLL2).
- **CLI version:** read it from `<app>/Contents/Resources/codex-cli/codex-package.json` (`version`, e.g. `0.162.0-alpha.17.2`), not from an Info.plist. Settings → Providers shows that version next to where the CLI came from.
- **App-server while ChatGPT.app is running, verified:** the bundled CLI's `codex app-server` over stdio went through `initialize` → `initialized` → `account/rateLimits/read` normally. The read carried:
  - primary (300 min) and secondary (10080 min) windows, with `usedPercent` and `resetsAt` in Unix seconds;
  - `credits`, `planType`, `rateLimitReachedType`;
  - `rateLimitsByLimitId.codex` and `rateLimitResetCredits`.

  Updates arrived as `account/rateLimits/updated`. This is the shape `fixtures/codex` already covers. The app-server opens the SQLite state under `~/.codex` read-write, and it worked next to ChatGPT.app's own process.
- **Updates, verified:** Sparkle 2.9.1 replaces the whole bundle, but the CLI path stays the same, and an app-server that is already running keeps answering. QuotaScope restarts its app-server when the `version` in `codex-package.json`, or the CLI file's inode, changes. It checks this on each poll, so the next reads come from the new CLI.
- **App-server while ChatGPT.app is not running, verified:** QuotaScope starts the bundled CLI as a child (`codex app-server`, stdio). `initialize` took 0.46 s, and `initialize` → `initialized` → `account/rateLimits/read` completed in 0.97 s in total.
  - On the way it got notifications the client does not use: `remoteControl/status/changed` and `account/updated` (`authMode`, `planType`). The client ignores any notification it does not know.
  - No `account/rateLimits/updated` arrived within 4 s. Polling stays the baseline.
  - Closing stdin ended it at once with exit 0, with no stderr warnings (lock or SQLite).
  - ChatGPT.app was not launched, no new lock was left in `~/.codex`, and the WAL/-shm files ChatGPT.app had left were checkpointed away when the app-server exited.
  - The read also carries fields the mapper ignores today (`ordinaryUsageAllowed`, `individualLimit`, `normalModelSlug`, `accountId`, `rateLimitUpsell`). `credits.balance` is a string (`"0"`). `rateLimitResetCredits.credits[]` lists reset credits; per the rule in 4.2 they are shown at most and never consumed.
- **Testing on the Mac needs explicit approval:** the Mac's Claude Code safety check classed starting `codex app-server` as credential exploration and blocked it. Live runs there (phase 3 onward) need the maintainer's explicit go-ahead each time.

### 6.3 Settings and data paths

| Data | Windows (unchanged) | macOS |
|---|---|---|
| `settings.json` | next to the exe (`AppContext.BaseDirectory`) | `~/Library/Application Support/QuotaScope/settings.json` (writing inside a signed `.app` would break the signature) |
| Crash log | next to the exe | `~/Library/Logs/QuotaScope/` |
| `claude_usage_last.json` diagnostic dump | next to the exe | `~/Library/Application Support/QuotaScope/` |

- **Same JSON schema and key names** as `AppSettings.cs`, enforced by the `settings/` fixtures. No Windows key is renamed or dropped. macOS ignores Windows-only keys (`UiScale`, `Glassmorphism`, `GlassStrength`, `LastPopupX/Y`); keys added for macOS (`MenuBarText`) are ignored by Windows. Unknown keys are preserved on save where practical.
- Hotkey strings keep the `Ctrl+Alt+U` grammar of `HotkeyDefinition` unchanged; the Mac UI shows `Ctrl→⌃`, `Alt→⌥`, `Shift→⇧`, and **`Win→⌘`** (decided 2026-10-17, replacing an earlier idea of a new `Cmd` token). The Windows key and ⌘ sit in the same place on their keyboards, the grammar already has `Win` (`MOD_WIN`), and no new token means no parser change on either side. The recorder writes `Win+…` for a ⌘ binding, and the same string means the Windows key on Windows. The keys themselves are mapped from Windows virtual-key codes to Carbon key codes in the Mac app.

## 7. Building on the Mac without an account

### 7.1 Prerequisites

1. Xcode Command Line Tools: `xcode-select --install`
2. Optional: a self-signed code-signing certificate (6.1)

### 7.2 One script: `mac/build.sh`

```
mac/build.sh [--release] [--install]
```

1. `swift build -c release` in `mac/` (Swift Package Manager, no `.xcodeproj`).
2. Run the built binary with `--self-test` (fixtures read from `../fixtures`); stop on failure.
3. Assemble `QuotaScope.app`: binary, `Info.plist` (bundle id `io.github.evanhexx.quotascope`, `LSUIElement`, version from the latest git tag), icon, string catalog, a copy of `fixtures/`.
4. Sign with `$QUOTASCOPE_SIGN_IDENTITY` or `-` (ad-hoc). Hardened runtime is not needed locally.
5. `codesign --verify --strict` and print which identity was used.
6. With `--install`, quit a running copy and copy the app to `~/Applications/`.

`--self-test` mirrors the Windows convention and works with the Command Line Tools alone. `swift test` can run the same fixture checks during development. **Use Swift Testing (`import Testing`), not XCTest.** Verified 2026-10-17 on the Command Line Tools (Swift 6.1.2, SDK 15.5): XCTest is not available there (`no such module`), and Swift Testing works with the package's macOS 13 platform.

### 7.3 Developing `QuotaCore` on Windows

`QuotaCore` uses Foundation only, so it also builds with the Swift toolchain for Windows. That way the shared logic can be ported and checked against the fixtures before a Mac is involved.

Setup used on 2026-10-17: Swift 6.4.1 from swift.org (`winget install --id Swift.Toolchain -e`). It needs the Visual Studio C++ build tools and a Windows SDK, which are already present wherever the WinUI app builds. `swift build` must find MSVC `link`, so run it from an x64 Developer Command Prompt (or after `vcvars64.bat`). The installer sets `SDKROOT` as a user variable, so a shell opened before the install does not have it yet.

```powershell
cd mac
swift build
swift run quotascope-selftest          # reads ../fixtures
```

The self-test prints a count per folder and exits 1 on the first mismatch, with the same path-style message as the C# side. `swift build` also prints warnings about the toolchain's own `WinSDK` module. Those come from the toolchain; `mac/Sources` itself builds without warnings.

What exists (`mac/Sources/QuotaCore`):

| Swift file | Ports | Checked by |
|---|---|---|
| `UsageModels.swift`, `CreditsGauge.swift` | `UsageModels.cs`, `CreditsGauge.cs` | `codex/`, `claude/` |
| `RateLimitMapper.swift` | `RateLimitMapper.cs`, including the rolling-update merge | `codex/` (19 cases) |
| `ClaudeUsageMapper.swift` | `ClaudeUsageMapper.cs` | `claude/` (10 cases) |
| `AppSettings.swift` | the settings model and `FromJson` in `AppSettings.cs` (loading only; saving comes with the app) | `rows/`, `alerts/` |
| `RowShapes.swift` | `RowShapes.cs` | `rows/` (9 cases) |
| `UsageAlerts.swift` | `UsageAlerts.cs`, including the tracker | `alerts/` (12 cases) |
| `HotkeyDefinition.swift` | `Hotkeys/HotkeyDefinition.cs`: parse, canonical format, the named-key table. The model stays Windows `MOD_*` flags and virtual-key codes; Carbon key codes come with the app | `hotkeys/` (3 cases) |
| `Loc.swift` | the row naming (`RowLabel`) and the reset-time text (`ResetText`, `ResetClock`, `ResetIn`) in `Loc.cs`, formatted by hand rather than with `DateFormatter`, so the text does not depend on the system's locale data | `alerts/` notification bodies (English and Korean), `resettext/` |
| `RowLayout.swift` | `app-winui/RowLayout.cs`: visible rows in the user's order, the two-column decision, and which cards share a line | `layout/` (6 cases) |
| `JSON.swift`, `Timestamp.swift`, `NumberText.swift` | the System.Text.Json, DateTimeOffset, and number-format behavior the C# code relies on | all of the above |
| `Fixtures.swift` | `UsageFixtures.cs` and the per-folder evaluators | — |

Matching .NET took deliberate code in four places:

- **JSON:** an ordered JSON value that keeps numbers as their source text. Merging Codex rolling updates depends on member order, and `TryGetInt64` tells `300` apart from `300.0`. `JSONSerialization` does neither reliably on every platform.
- **Timestamps:** 100 ns ticks. Claude reset times carry microseconds, and `ISO8601DateFormatter` keeps only milliseconds.
- **Number text:** `"0.##"` formatting takes 15 significant digits and rounds half away from zero, as .NET does. `Math.Round` is ties-to-even. Point parsing accepts what `NumberStyles.Float` accepts, and refuses Swift-only forms such as hex or `inf`.
- **Settings:** dictionary keys compare exactly. The C# defaults are declared case-insensitive, but System.Text.Json replaces them with plain dictionaries on load.

The renewer's timing rules are now `ClaudeRenewalPolicy.swift` in QuotaCore, checked by `fixtures/renewer` (4 cases). That brings the self-test to 57 cases in 6 folders.

Phase 5 shared work (2026-10-17):

- The column decision and the card pairing moved out of the WinUI popup into `app-winui/RowLayout.cs`. The popup now draws what `RowLayout.Lines` returns, and its behavior does not change.
- The reset-time text takes "now" as an argument: `Loc.ResetText(resetsAt, now, remaining)`. The popup passes `DateTimeOffset.Now`.
- Two Korean notification cases joined `fixtures/alerts`. An input may set `"language": "Korean"`, and English is restored after each case.
- Both new folders (`layout`, `resettext`) are embedded in the WinUI project only, since the legacy app has neither the layout nor the reset text.
- The Swift port passes all 8 folders (66 cases).

Checked: a corrupted expected value in each folder, or a missing expected file, makes the self-test exit 1.

#### QuotaProviders (phase 3, started 2026-10-17)

`mac/Sources/QuotaProviders` runs the providers. It builds on Windows too, except for the macOS-only parts behind `#if canImport`:

| Swift file | Ports | Builds on Windows |
|---|---|---|
| `UsageProvider.swift` | `IUsageProvider.cs`; each provider is an actor | yes |
| `CommandResolution.swift` | `CodexCommandResolver.cs` and `ClaudeCommandResolver.cs`, rewritten for 6.2: bundle ID lookup, `codex-package.json` version, CLI stamp, login-shell `PATH` | yes (the `NSWorkspace` lookup is macOS-only) |
| `CodexAppServerClient.swift`, `CodexUsageProvider.swift` | `CodexAppServerClient.cs`, `CodexUsageProvider.cs` | yes |
| `ClaudeCredentials.swift` | `ClaudeCredentialReader.cs`, as sources: Keychain (macOS), file, and Keychain-then-file | file only |
| `ClaudeSessionRenewer.swift` | the process part of `ClaudeSessionRenewer.cs` | yes |
| `ClaudeUsageProvider.swift` | `ClaudeUsageProvider.cs` | yes |

Where it differs from the C# code on purpose:

- **Codex:**
  - The client restarts its app-server when the bundled CLI's stamp changes (6.2).
  - When the process closes stdout, requests that are still waiting fail at once, instead of after the 15 s timeout.
  - Closing stdin ends the app-server, with `terminate()` as the fallback. The C# version kills the process tree.
- **Claude:**
  - The Keychain has no change notification, so the credentials' modification date is polled every 5 s, debounced 2 s. This replaces `FileSystemWatcher`, for the file source too.
  - The secret is read once per fetch, for both `expiresAt` and the token, instead of twice. It is read only for a request that is actually sent, after the auth-pause and cache checks (the C# code reads `expiresAt` first). On macOS every data read is a Keychain access, and until the user picks "Always Allow" each one prompts again (measured in phase 4). As a side effect, a proactive renewal now goes out only on a poll that sends a request, which is at least once per poll interval.
  - Credentials that exist but cannot be read (the Keychain prompt denied or not answered yet) report `Unavailable` with "Claude credentials could not be read. Allow QuotaScope in the Keychain prompt". The next poll retries. This is not treated as a sign-out.
  - An item that holds only Claude Code's MCP connector tokens (`mcpOAuth`, no `claudeAiOauth`) reads as not signed in. That is how it looks before the terminal `claude` has ever signed in (seen 2026-10-17). It shows "Run Claude Code and sign in" without "expired", and no renewal is nudged, neither on that read nor on the paused polls after it. The flag clears once a real sign-in is read.
  - Every Keychain call (the data read, the attribute lookup for the change stamp, and the existence check) runs on one serial queue, `QuotaScope.ClaudeCredentials`, off the provider actor, and its result comes back through a continuation. Measured on the Mac in phase 4: `SecItemCopyMatching` holds its thread until the user answers the access prompt. One build left the prompt open for 19 minutes, saw no calls in that time, and got all the queued calls at once when it was answered. While the prompt is up, the login keychain also holds a lock that attribute lookups wait on. So the 5-second watcher skips its tick while a read is in flight, and the status text uses the last known existence instead of a fresh lookup. A `sample` taken during the prompt (a2d79d3) found `SecItemCopyMatching` waiting in one place only, that queue, and no blocked Swift concurrency thread. Fetches stay one at a time. One remaining direct call: the renewer still reads the stamp itself, when an attempt starts (on the actor, after a fetch's read has finished) and when it ends (on a background queue). Neither can overlap a prompt from the same fetch.
  - Known gap: a payload that is not JSON also shows the Keychain-prompt message. Telling "could not read" apart from "unexpected format" needs a result type on `ClaudeCredentialSource`. Left as is, because it is rare.
  - The diagnostic dump goes to a directory the app passes in (6.3).
- **Renewer:** the `claude mcp list` output goes to the null device unread. Draining and discarding it, as the C# version does, is not needed.

`quotascope-providertest` drives all of this against fakes, with no real CLI, account, Keychain, or network:

```powershell
cd mac
swift build
swift run quotascope-providertest
```

- **Codex:** it starts `fake-codex-app-server`, a stand-in that behaves like the measured app-server: it drops requests before `initialized`, sends `remoteControl/status/changed` and `account/updated`, answers a read, and then sends one sparse update. The test checks:
  - the read;
  - that the rolling update is merged, not mapped alone;
  - reconnect;
  - that the process is gone after `close`;
  - that a crashing app-server fails fast with its stderr line;
  - that a missing command says so;
  - bundle lookup with fake app bundles: Classic is skipped and the highest `com.openai.codex` app wins, the version comes from `codex-package.json`, and the stamp changes when that version does.
- **Claude:** in-memory credentials, transport, clock, and an inline renewer. The test checks:
  - the request headers;
  - the 60 s poll floor;
  - 429 backoff (1 m, then 5 m) that keeps the last rows;
  - stale data after a network failure;
  - the 401 pause and recovery nudge;
  - that no request goes out while paused, and that a fresh nudge waits out its cooldown;
  - resuming after a credentials change;
  - the proactive nudge and the `Session expires in 12m` countdown;
  - the 2-minute proactive retry;
  - the signed-out hint;
  - that the renewer stops after `close`.

Results (2026-10-17):

- **Windows:** 42 checks ok. A clean build shows no warnings in `mac/Sources`, and no `fake-codex-app-server` process is left behind.
- **macOS** (4b3e03c, 15.6.1 arm64, Command Line Tools only, Swift 6.1.2): `swift build` and `swift build -c release` with no warnings, 57/57 fixtures, and 42/42 provider checks. No fake process is left behind. A code review on the Mac confirmed that the test creates no `SecItemCopyMatching`, `URLSessionUsageTransport`, or real codex lookup.

Only the Mac can check these, and live runs there need the maintainer's explicit go-ahead (6.2):

- `KeychainCredentialSource` against the real item: the payload format, whether an attributes-only read prompts, and whether `mdat` moves on refresh.
- The `NSWorkspace` lookup and the login-shell `PATH`.
- The real `claude mcp list` renewal.
- SIGPIPE handling when the app-server dies.
- The app-server started from a Finder-launched app.

### 7.4 Permissions the user will see

| Prompt | When | Avoidable? |
|---|---|---|
| Keychain access for `Claude Code-credentials` | First Claude poll | No; "Always Allow" sticks only with a stable signature (6.1) |
| Notifications | First alert | No (system rule) |
| Login item notice | When "Start at login" is turned on | No (system rule) |

No Accessibility, Screen Recording, Full Disk Access, or Automation permission should be needed. If one shows up, treat it as a bug.

## 8. Repository layout

```
fixtures/                    shared input/expected pairs (section 5); used by C# and Swift
mac/Package.swift            SwiftPM package: QuotaCore, QuotaProviders, quotascope-selftest, quotascope-providertest, fake-codex-app-server; QuotaScope (the app, macOS only) later
mac/Sources/QuotaCore/…      exists: models, mappers, rows, alerts, settings model, hotkey grammar (7.3)
mac/Sources/quotascope-selftest/…  exists: runs QuotaCore against ../fixtures
mac/Sources/QuotaProviders/…      exists: providers, renewer, command and credential lookup (7.3)
mac/Sources/quotascope-providertest/…, fake-codex-app-server/…  exist: provider checks against fakes
mac/Sources/QuotaProviders/…
mac/Sources/QuotaScope/…
mac/Tests/…                  optional Swift Testing targets over the same fixtures (not XCTest; see 7.2)
mac/Resources/…              Info.plist template, icon, Localizable.xcstrings
mac/build.sh
docs/MACOS_BUILD.md          this file
```

`app-winui/` and `app/` change only as described in 5.3. No namespace change, no shared-file `OperatingSystem.IsMacOS()` branches.

## 9. Phases

| Phase | Deliverable | Verified by |
|---|---|---|
| **0. Spike on the Mac** | Answers to every **(verify)** item: Keychain item name and format, attributes read without prompt, `claude mcp list` refresh on macOS, ChatGPT.app bundle ID and its bundled `codex app-server` started from a Finder-launched app next to a running ChatGPT.app, `SMAppService` and notifications with ad-hoc vs self-signed, available Swift test framework. **Done (2026-10-17) as far as it can be without the app:** the test framework (Swift Testing), the bundle IDs, the app-server with and without ChatGPT.app running, update behavior, the Keychain item location (service only, empty account), and QuotaCore on macOS (53/53). Moved to phase 3, where they need the real app: the Keychain payload format, `mdat` on refresh, a prompt-free attribute read, `claude mcp list` refresh, `SMAppService` and notifications, and a Finder-launched start | Notes added to this file (6.1, 6.2, 7.2) |
| **1. Fixtures (Windows)** | `fixtures/` extracted from the C# self-tests, embedded in both projects (5.3). 1a (mappers) and 1b (renewer, hotkeys, rows, alerts) done | Both Windows projects build; `--self-test` passes on both; case count unchanged |
| **2. `QuotaCore` in Swift** | Models, mappers, credits gauge, row resolution, alerts, settings model, hotkey grammar. **Done (2026-10-17)** on Windows (7.3) | Swift self-test passes every `QuotaCore` fixture: 53 cases (codex 19, claude 10, rows 9, alerts 12, hotkeys 3). `renewer/` waits for phase 3 |
| **3. `QuotaProviders` in Swift** | Codex client, Claude client, Keychain source, renewer, command resolution. **Written and checked against fakes (2026-10-17):** 42/42 on Windows and on macOS (7.3). Open until the app exists: the four checks that touch real credentials (Keychain payload, attribute-read prompt, `mdat` on refresh, real `claude mcp list`), each with the maintainer's approval, and the live run | Behavior checklist (4.2) reviewed item by item; unit tests with fake clock/process; live run against both providers |
| **4. Menu bar MVP** | Status item with arc icon, popover with bars, refresh, right-click menu, settings file. **Built (2026-10-17, merged at 7ef321a):** status item with arc and warning/critical dot, popover with bars, click-to-toggle reset time, pinned panel, right-click menu, settings window (General, Providers, Appearance, Alerts, About; only changed keys are written), and `mac/build.sh`. The Mac session reports it running with real Codex and Claude data | Manual smoke test (10), not yet run by the maintainer |
| **5. Parity** | Gauges / fill / mix & match, columns, order and visibility, pin panel, time toggle, alerts + notifications, hotkeys, login item, EN/KO, settings window. **Split (decided 2026-10-17):** the QuotaScope thread handles shared logic and review: `RowLayout` + `fixtures/layout`, reset text + `fixtures/resettext`, Korean alert cases (all three done, 7.3), and merging the Mac branches. The Mac thread handles the app: notifications, global hotkeys (Windows virtual-key codes mapped to Mac key codes, plus the recorder), gauge, fill, and mix-and-match shapes and columns through `RowLayout`, start at login, the EN/KO String Catalog, and their settings tabs | Manual smoke test (10) |
| **6. Docs** | README / README.ko "Build on macOS" section, platform badge, limitations; PROJECT_MAP entries for `mac/` and `fixtures/` | Review |

The README must not claim a macOS download, installer, Homebrew cask, or auto-update; only "build from source on your Mac".

## 10. Manual smoke test (macOS)

- Menu bar icon appears; no Dock icon.
- Left click opens and closes the popover; right click opens the menu.
- `⌃⌥U` toggles the popover; refresh-all and pin hotkeys work when set.
- Refresh and Reconnect work from the menu and from the popover header.
- Pin detaches a floating panel that stays on top and survives Space switches.
- Switching light/dark in System Settings updates icon and popover live.
- Claude: the Keychain prompt appears once; after "Always Allow" and a rebuild with the self-signed identity, no new prompt.
- Codex: works when the app is launched from Finder and at login, not just from Terminal; finds the CLI bundled in ChatGPT.app with no `codex` on `PATH`; ignores ChatGPT Classic.app.
- An alert notification fires at a configured point.
- Start at login works after a reboot.
- Settings persist after quit and relaunch.
- Menu bar text: each `MenuBarText` choice shows the right number; it follows the gauge metric; a missing row shows `--`.
- Clicking the time text toggles clock time and remaining time on every row, and the choice survives a relaunch.
- **Korean:** with the language set to Korean (and with macOS itself in Korean), no label, reset text, menu item, or settings label is clipped at the default popup width, in both time modes and with every row shown (Spark rows, per-model weekly rows, credits). Repeat in English. Check light and dark.
- Quitting the app leaves no `codex` or `claude` child process behind.

## 11. Open decisions

| ID | Question | Recommendation |
|---|---|---|
| M1 | Architecture | **A**: all Swift, kept in step with C# by shared input/expected fixtures |
| M2 | Signing | Self-signed local identity when present, ad-hoc fallback; document creating the certificate. It does not keep "Always Allow" across rebuilds (6.1, checked 2026-10-17): each build asks once |
| M3 | Warning/critical in the menu bar | Template arc icon + small colored badge dot only at warning/critical |
| M4 | Themes on macOS | System / Light / Dark only; drop Midnight, glass, and UI scale on macOS |
| M5 | Minimum macOS | 13 Ventura |
| M6 | Bundle id | `io.github.evanhexx.quotascope` (no OpenAI/Anthropic naming) |
| M7 | Prebuilt Mac binaries later? | Out of scope; would need a Developer ID and notarization |
| M8 | Desktop / Notification Center widget? | **Deferred (decided 2026-10-17).** The menu bar item is the main surface and alert banners go to Notification Center. A WidgetKit widget may come after parity: it refreshes on a system budget (not live), can only show data the app writes to a shared container, likely needs an App Group (normally tied to a Team ID), and is an extension bundle that is awkward to build without Xcode. Check whether an ad-hoc or self-signed widget loads at all before planning it |
| M9 | Menu bar text | **Decided:** user picks it (`MenuBarText`); default highest weekly window |
| M10 | Default row shape | **Decided:** bars |
| M11 | Font | **Decided:** macOS system font |
| M12 | Default gauge metric | **Decided (2026-10-17):** `Remaining` on macOS when settings.json has no `GaugeMetric`; the shared default in `AppSettings.cs` stays `Used` |
| M13 | Hotkey modifier for ⌘ | **Decided (2026-10-17):** `Win` maps to ⌘; no new `Cmd` token, so the hotkey grammar stays the same on both platforms (6.3) |
| M14 | EN/KO strings on macOS | **Decided (2026-10-17):** `Loc` in `QuotaCore`, as on Windows, not a String Catalog: the language switches live, and the Command Line Tools cannot compile a catalog (3.4) |

## 12. Risks

- **Drift between C# and Swift.** Mitigated by fixtures for all deterministic logic (5) and the behavior checklist for the rest (4.2). A fix made on one side without a fixture is the failure mode to watch for in review.
- **Measured CLI behavior differs on macOS** (token refresh trigger, Keychain rewrite). Phase 0 checks it before any provider code is written.
- **Keychain format or service name differs**, or Claude Code changes storage. Phase 0 checks it; the file fallback stays.
- **Keychain prompt fatigue** with ad-hoc signing (6.1).
- **Undocumented endpoint** risk is the same as on Windows.
- **Two languages** to maintain. Accepted: the history shows the shared logic changes rarely, and fixtures turn most changes into a data diff.

> This project is not affiliated with, endorsed by, or sponsored by OpenAI or Anthropic. Codex is a product/service of OpenAI. Claude is a product/service of Anthropic.
