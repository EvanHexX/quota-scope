# macOS Build Plan

> Status as of 2026-10-17: **plan only. No macOS code exists yet, and nothing here has been run on a Mac.** Items marked **(verify)** are assumptions to check on the target Mac in phase 0.

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
- Optional compact text next to the icon (for example `72%`), a common pattern in Mac menu bar utilities. Off by default.
- Left click toggles the popover. Right click (or Control-click) opens an `NSMenu` with Refresh, Reconnect, Settings…, Quit, mirroring the Windows tray menu.
- Hover tooltip lists remaining quota per window, as on Windows.

### 3.2 Popover

- SwiftUI view hosted in an `NSPopover` anchored to the status item (arrow, system material background, light/dark automatic). Plain `MenuBarExtra(.window)` is simpler but gives no control over right-click and pinning, so use `NSStatusItem` + `NSPopover` from AppKit with SwiftUI content.
- Provider sections with headers, rows using native controls: `Gauge` (`.accessoryCircularCapacity`) for gauges, capsule bars for bars, and the fill card as a rounded rectangle with a partial fill. Shapes, order, visibility, and columns come from the ported row resolution.
- Header: last update, refresh button (SF Symbol `arrow.clockwise`), pin button (`pin` / `pin.fill`).
- **Pinned** = detach the popover into a small floating panel (`NSPanel`, `.nonactivatingPanel`, `.floating` level), the native equivalent of the Windows pinned popup. Popover behavior `.transient` when unpinned.
- Time text click toggles clock time / remaining time, as on Windows.
- Typography: system font, `monospacedDigit()` for percentages and times.

### 3.3 Settings window

- SwiftUI `Settings` scene (opens with `⌘,`), toolbar-style tabs: General, Providers, Appearance, Alerts, Hotkeys, About. Same sections as the Windows settings window, laid out with `Form` + `.formStyle(.grouped)`.
- Hotkey recorder: a field that captures a key combination and shows it with Mac symbols (`⌃⌥U`).

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
| EN/KO UI (`Loc.cs`) | String Catalog (`Localizable.xcstrings`) with en and ko | Language setting: System / English / Korean |
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

Both:

- One provider failing never affects the other.

## 5. Shared fixtures

### 5.1 Layout and format

Each case is an **input plus the expected result**, not just a sample payload. Row `Label` values are the keys under which settings are stored (`RowShapes`, `RowOrder`, `RowVisibility`, `RowAlerts`, `RowAlertPoints`), so a one-character difference between platforms would silently detach a user's settings. The expected files pin those strings.

```
fixtures/
  codex/
    weekly-only.input.json              raw account/rateLimits/read result
    weekly-only.expected.json
    five-hour-and-weekly.input.json
    five-hour-and-weekly.expected.json
    sparse-update.input.json            { "read": {…}, "updates": [{…}, …] }
    sparse-update.expected.json
    …
  claude/
    legacy-fields.input.json            raw /api/oauth/usage body
    limits-array.input.json
    credits-spend.input.json
    …  (each with .expected.json)
  alerts/
    *.input.json                        { "settings": {…}, "usages": [ProviderUsage, …] }
    *.expected.json                     notifications fired per step, row alert levels
  rows/
    *.input.json                        { "settings": {…}, "rows": [UsageRowRef, …] }
    *.expected.json                     resolved order, visibility, shape per row key
  settings/
    *.json + *.expected.json            settings.json files, including old ones, and the values they load to
  hotkeys/
    cases.json                          [{ "text": "ctrl+alt+u", "ok": true, "formatted": "Ctrl+Alt+U" }, …]
```

Each `*.input.json` may carry an optional `"options"` object for mapper inputs that come from settings (`showSecondaryRows`, `showCredits`, `creditsFullAmount`).

Expected rows list every field of `UsageRow`: `label`, `scope`, `isPrimary`, `usedPercent`, `resetsAt` (ISO 8601 UTC), `windowDurationMins`, `detailText`, `beyondFull`, `creditsLeft`, plus `overallUsedPercent` and `state` for the provider. Percentages compare with a tolerance of 1e-9; strings compare exactly.

### 5.2 Rules

- **C# is the reference.** A fixture is added or changed only together with the C# behavior, in the same commit, and the expected file is reviewed by hand like code.
- A new payload shape seen in the wild becomes a new fixture first, then a fix on both sides.
- Fixtures contain **no real account data**: synthetic or redacted payloads only, same as the current self-test samples.
- Time-dependent output (remaining time, "expires in") is computed from a `"now"` value in the input, never from the wall clock.

### 5.3 Windows-side change (phase 1)

Moving the samples out of the C# self-tests is the only change to the Windows apps in this plan:

- Today the samples are string literals inside `RateLimitMapper.RunSelfTest`, `ClaudeUsageMapper.RunSelfTest`, `ClaudeSessionRenewer.RunSelfTest`, `HotkeyDefinition.RunSelfTest`, `RowShapes.RunSelfTest`, and `UsageAlerts.RunSelfTest`. They move to `fixtures/`, and each self-test loops over its folder and compares with the expected file.
- `--self-test` also runs from a released exe, where no repository is present. The fixtures are therefore compiled in as **`EmbeddedResource`** items, read with `Assembly.GetManifestResourceStream`. No files ship next to the exe.
- The legacy app (`app/Program.cs`) runs the mapper, renewer, and hotkey self-tests too, so **both** `app/QuotaScope.csproj` and `app-winui/QuotaScope.WinUI.csproj` embed the fixtures. The legacy project includes only the folders its self-tests use.
- Keep any self-test assertion that is not a data comparison (for example, invariants checked across cases) in C# as it is.
- Verification: build both projects and run `--self-test` on both, before and after, and confirm the number of cases did not drop.

## 6. macOS platform code

### 6.1 Claude credentials live in the Keychain **(verify)**

On macOS, Claude Code stores its OAuth credentials in the login Keychain (generic password, service `Claude Code-credentials`), not in `~/.claude/.credentials.json`. The JSON payload is believed to be the same (`claudeAiOauth.accessToken`, `expiresAt`).

- `KeychainCredentialSource` reads the item with `SecItemCopyMatching`, parses it in memory, uses the token for the one request, and drops it. Never cached, copied, or logged.
- **Change detection** (replaces the Windows `FileSystemWatcher`): poll the item's modification date every few seconds via an attributes-only query (`kSecReturnAttributes`, no `kSecReturnData`). **(verify that this does not trigger a Keychain prompt)**
- Fall back to `~/.claude/.credentials.json` with a file watcher if that file exists (Claude Code uses it when the Keychain is unavailable).

**Keychain prompts.** The item's ACL belongs to Claude Code, so the first read shows a "QuotaScope wants to use your confidential information…" prompt.

| Approach | Prompt behavior | Security | Verdict |
|---|---|---|---|
| Run `/usr/bin/security find-generic-password -s "Claude Code-credentials" -w` | "Always Allow" adds `/usr/bin/security` to the item's ACL, which survives rebuilds | Weakens the item: **any** process can then read the token silently through `security` | Not used |
| `SecItemCopyMatching` from the app | "Always Allow" trusts QuotaScope's code signature | Only QuotaScope gets access | **Used** |

With ad-hoc signing every rebuild is a new signature, so the prompt returns after each rebuild. The fix without a developer account: create a **self-signed code-signing certificate** once (Keychain Access → Certificate Assistant → Create a Certificate…, type "Code Signing", name e.g. `QuotaScope Local`). The build script signs with it when `QUOTASCOPE_SIGN_IDENTITY` is set, which keeps the designated requirement stable across rebuilds so "Always Allow" sticks. **Decision M2.**

### 6.2 Finding `codex` and `claude` from a GUI app

Apps launched from Finder or at login do **not** inherit the shell `PATH`, so `/opt/homebrew/bin`, `~/.local/bin`, npm global bins, nvm shims etc. are missing.

Resolution order for both commands:

1. The configured command if it is an absolute path.
2. Known locations: `~/.local/bin`, `/opt/homebrew/bin`, `/usr/local/bin`, `~/.npm-global/bin`, `~/.bun/bin`, the newest `~/.nvm/versions/node/*/bin`.
3. Ask the user's login shell once and cache the **path** (not the environment): `$SHELL -lc 'command -v codex'`.
4. Otherwise report "not found" with the paths tried, and let the user set the command in settings.

Node-based shims also need `node` on `PATH`; start children with the login shell's `PATH` (resolved once, kept in memory).

### 6.3 Settings and data paths

| Data | Windows (unchanged) | macOS |
|---|---|---|
| `settings.json` | next to the exe (`AppContext.BaseDirectory`) | `~/Library/Application Support/QuotaScope/settings.json` (writing inside a signed `.app` would break the signature) |
| Crash log | next to the exe | `~/Library/Logs/QuotaScope/` |
| `claude_usage_last.json` diagnostic dump | next to the exe | `~/Library/Application Support/QuotaScope/` |

- **Same JSON schema and key names** as `AppSettings.cs`, enforced by the `settings/` fixtures. No Windows key is renamed or dropped. macOS ignores Windows-only keys (`UiScale`, `Glassmorphism`, `GlassStrength`, `LastPopupX/Y`); keys added for macOS are ignored by Windows. Unknown keys are preserved on save where practical.
- Hotkey strings keep the `Ctrl+Alt+U` grammar of `HotkeyDefinition`; the Mac UI shows `Ctrl→⌃`, `Alt→⌥`, `Shift→⇧`, and adds `Cmd→⌘`. Adding `Cmd` to the macOS parser must not change how Windows parses existing strings (the `hotkeys/` fixtures cover both).

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

`--self-test` mirrors the Windows convention and works with the Command Line Tools alone. `swift test` (XCTest or Swift Testing) can run the same fixture checks during development **(verify which test framework the installed CLT provides; XCTest has historically needed full Xcode)**.

### 7.3 Permissions the user will see

| Prompt | When | Avoidable? |
|---|---|---|
| Keychain access for `Claude Code-credentials` | First Claude poll | No; "Always Allow" sticks only with a stable signature (6.1) |
| Notifications | First alert | No (system rule) |
| Login item notice | When "Start at login" is turned on | No (system rule) |

No Accessibility, Screen Recording, Full Disk Access, or Automation permission should be needed. If one shows up, treat it as a bug.

## 8. Repository layout

```
fixtures/                    shared input/expected pairs (section 5); used by C# and Swift
mac/Package.swift            SwiftPM package: QuotaCore, QuotaProviders, QuotaScope
mac/Sources/QuotaCore/…
mac/Sources/QuotaProviders/…
mac/Sources/QuotaScope/…
mac/Tests/…                  optional swift test targets over the same fixtures
mac/Resources/…              Info.plist template, icon, Localizable.xcstrings
mac/build.sh
docs/MACOS_BUILD.md          this file
```

`app-winui/` and `app/` change only as described in 5.3. No namespace change, no shared-file `OperatingSystem.IsMacOS()` branches.

## 9. Phases

| Phase | Deliverable | Verified by |
|---|---|---|
| **0. Spike on the Mac** | Answers to every **(verify)** item: Keychain item name and format, attributes read without prompt, `claude mcp list` refresh on macOS, `codex app-server` started from a Finder-launched app, `SMAppService` and notifications with ad-hoc vs self-signed, available Swift test framework | Notes added to this file |
| **1. Fixtures (Windows)** | `fixtures/` extracted from the C# self-tests, embedded in both projects (5.3) | Both Windows projects build; `--self-test` passes on both; case count unchanged |
| **2. `QuotaCore` in Swift** | Models, mappers, credits gauge, row resolution, alerts, settings model, hotkey grammar | Swift `--self-test` passes every fixture |
| **3. `QuotaProviders` in Swift** | Codex client, Claude client, Keychain source, renewer, command resolution | Behavior checklist (4.2) reviewed item by item; unit tests with fake clock/process; live run against both providers |
| **4. Menu bar MVP** | Status item with arc icon, popover with bars, refresh, right-click menu, settings file | Manual smoke test (10) |
| **5. Parity** | Gauges / fill / mix & match, columns, order and visibility, pin panel, time toggle, alerts + notifications, hotkeys, login item, EN/KO, settings window | Manual smoke test (10) |
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
- Codex: works when the app is launched from Finder and at login, not just from Terminal.
- An alert notification fires at a configured point.
- Start at login works after a reboot.
- Settings persist after quit and relaunch.
- Quitting the app leaves no `codex` or `claude` child process behind.

## 11. Open decisions

| ID | Question | Recommendation |
|---|---|---|
| M1 | Architecture | **A**: all Swift, kept in step with C# by shared input/expected fixtures |
| M2 | Signing | Self-signed local identity when present, ad-hoc fallback; document creating the certificate |
| M3 | Warning/critical in the menu bar | Template arc icon + small colored badge dot only at warning/critical |
| M4 | Themes on macOS | System / Light / Dark only; drop Midnight, glass, and UI scale on macOS |
| M5 | Minimum macOS | 13 Ventura |
| M6 | Bundle id | `io.github.evanhexx.quotascope` (no OpenAI/Anthropic naming) |
| M7 | Prebuilt Mac binaries later? | Out of scope; would need a Developer ID and notarization |

## 12. Risks

- **Drift between C# and Swift.** Mitigated by fixtures for all deterministic logic (5) and the behavior checklist for the rest (4.2). A fix made on one side without a fixture is the failure mode to watch for in review.
- **Measured CLI behavior differs on macOS** (token refresh trigger, Keychain rewrite). Phase 0 checks it before any provider code is written.
- **Keychain format or service name differs**, or Claude Code changes storage. Phase 0 checks it; the file fallback stays.
- **Keychain prompt fatigue** with ad-hoc signing (6.1).
- **Undocumented endpoint** risk is the same as on Windows.
- **Two languages** to maintain. Accepted: the history shows the shared logic changes rarely, and fixtures turn most changes into a data diff.

> This project is not affiliated with, endorsed by, or sponsored by OpenAI or Anthropic. Codex is a product/service of OpenAI. Claude is a product/service of Anthropic.
