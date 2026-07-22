# Modernization Plan

Last updated: 2026-07-22

## Current repository state

QuotaScope (formerly Codex Usage Tray) is currently a small Windows tray utility. The current implementation uses `codex app-server` over stdio JSON-RPC to read rate limit data.

Current technical baseline:

- App project: `app/QuotaScope.csproj`
- Current target: `net10.0-windows`
- Current UI stack: Windows Forms
- Current entry point: `app/Program.cs`
- Current self-test path: `dotnet run --project app/QuotaScope.csproj -- --self-test`
- Current documentation map: `docs/PROJECT_MAP.md`

## Modernization decision

### Recommended near-term direction

Keep the app on Windows Forms for the first public release, using the current modern .NET target.

Current near-term target:

```xml
<TargetFramework>net10.0-windows</TargetFramework>
<UseWindowsForms>true</UseWindowsForms>
```

Rationale:

- `.NET 6` is out of support.
- `.NET 10` is an LTS release with a longer support window.
- The current UI is already a custom tray/popup implementation using Windows Forms, `NotifyIcon`, GDI+ drawing, and Win32-style hotkey/window behavior.
- A .NET target upgrade was much smaller and safer than a UI framework rewrite.

### Pre-release compatibility policy

There has not been a public release or external download yet.

Until the first tagged public release, compatibility with old local settings, theme IDs, names, screenshots, or internal option strings is not required. Prefer clean names and simple code over migration code.

After the first tagged public release, breaking changes to settings, theme IDs, command-line behavior, or persisted app data should be treated as compatibility-sensitive and documented.

### WinUI 3 decision

Do not migrate the main app to WinUI 3 before the first stable public release.

WinUI 3 is a valid future option, but it should be treated as a separate v2 prototype/rewrite because the current app depends heavily on Windows Forms concepts:

- `NotifyIcon`
- `ApplicationContext`
- borderless `Form` popup behavior
- custom GDI+ painting
- `TransparencyKey`
- native-window global hotkey handling
- context menus shared between tray icon and popup

Expected WinUI 3 benefits:

- modern Fluent UI controls and styling
- better fit with current Windows app design language
- stronger foundation for animation-heavy UI
- Windows App SDK APIs for modern app lifecycle/windowing scenarios

Expected WinUI 3 costs/risks:

- popup and tray behavior may require extra Win32 interop or dependencies
- current custom drawing must be replaced with XAML/Win2D/composition equivalents
- packaging/runtime deployment becomes more complex
- migration may delay the public release without improving the core Codex usage-monitoring value

## Phase 0 — Public repository hygiene

Checklist:

- [x] Add `AGENTS.md` for Codex working rules.
- [x] Add this modernization plan.
- [x] Add or verify `.gitignore` for .NET, Visual Studio, logs, and local settings.
- [ ] Add a license file before promoting the repository publicly.
- [x] Remove or avoid maintainer-local absolute paths from docs.
- [ ] Add public README sections for installation, usage, privacy, limitations, and disclaimer.
- [ ] Add screenshots only after redacting private account information.

Exit criteria:

- A new contributor can understand what the app does and how to build it.
- Public documentation does not imply OpenAI affiliation.
- Public documentation does not expose local machine paths, secrets, or private account details.

## Phase 1 — Move from .NET 6 to supported .NET LTS while keeping Windows Forms

Goal:

Upgrade runtime support without changing UI architecture.

Checklist:

- [x] Check installed SDKs with `dotnet --list-sdks`.
- [x] Confirm the selected modern .NET SDK is available locally.
- [x] Change the app project (now `app/QuotaScope.csproj`) from `net6.0-windows` to the selected modern target, preferably `net10.0-windows`.
- [x] Keep `<UseWindowsForms>true</UseWindowsForms>`.
- [x] Run `dotnet build` on the app project.
- [x] Run the app project with `-- --self-test`.
- [ ] Run the Windows tray smoke test:
  - [ ] tray icon appears
  - [ ] popup opens from tray click
  - [ ] `Ctrl+Alt+U` toggles popup
  - [ ] Refresh works
  - [ ] Reconnect works
  - [ ] pinned popup behavior works
  - [ ] settings persist after restart
- [x] Update `README.md` build requirements.
- [x] Update docs if target framework or commands changed.

Exit criteria:

- App builds and runs on the selected modern target.
- Self-test passes.
- Existing tray behavior is preserved.
- No WinUI 3 migration is included in this phase.

Rollback:

- Revert only the target-framework change and any directly related documentation updates.

## Phase 2 — Pre-release polish and settings cleanup

Goal:

Polish the current Windows Forms app before the first public release.

Checklist:

- [x] Clean up theme IDs and names without backward-compatibility migration.
- [x] Add a true black/dark theme.
- [x] Rename the current `Glassmorphism` theme to a more accurate name.
- [x] Add a new theme that is more genuinely glass-like within the current Windows Forms/GDI+ implementation limits.
- [x] Add an app icon and wire it to the executable and tray icon.
- [ ] Move default settings storage away from the app output folder to a per-user application data path if doing so remains simple.
- [ ] Consider extracting Codex rate-limit reading behind an interface, but keep only the Codex provider implemented.
- [ ] Avoid adding non-Codex providers until explicitly requested.
- [ ] Add unit tests or keep the current self-test if a full test project is not worth the overhead yet.
- [ ] Add validation for unusual/missing Codex app-server payload fields.
- [ ] Improve error messages for missing `codex`, expired login, app-server timeout, and JSON-RPC errors.

Exit criteria:

- Theme names are clean and not misleading.
- App icon appears correctly where the current app architecture supports it.
- Failure states are clearer for users.
- Provider-ready structure exists only where it improves maintainability.

## Phase 3 — Public release readiness

Goal:

Prepare a clean first public GitHub release.

Checklist:

- [ ] Add `LICENSE`.
- [ ] Add GitHub Actions build workflow on `windows-latest`.
- [ ] Add release/publish instructions.
- [ ] Decide deployment mode:
  - [ ] framework-dependent zip
  - [ ] self-contained zip
  - [ ] MSIX or installer later
- [ ] Add a privacy section explaining that the app launches local `codex app-server` and does not use a separate OpenAI API key.
- [ ] Add a limitations section explaining dependence on Codex app-server behavior.
- [ ] Add screenshots after redaction.
- [ ] Add disclaimer:

> This project is not affiliated with, endorsed by, or sponsored by OpenAI. Codex is a product/service of OpenAI.

Exit criteria:

- A user can download/build/run the app from the README.
- Build instructions are reproducible.
- The release does not include local artifacts or private data.

## Phase 4 — Optional WinUI 3 prototype

Goal:

Evaluate whether WinUI 3 improves the app enough to justify a rewrite.

Status: superseded by Phase 5. The prototype-only constraint of this phase was lifted by explicit maintainer approval on 2026-07-22; WinUI 3 may now be applied to the main app under the Phase 5 plan below. The parity checklist in this phase still applies before the main app is replaced.

Branch recommendation:

```text
prototype/winui3-popup
```

Checklist:

- [ ] Do not change the Codex app-server client during the prototype.
- [ ] Build a minimal WinUI 3 shell that can show the same usage data.
- [ ] Solve tray icon integration deliberately; do not assume WinUI 3 has a direct `NotifyIcon` equivalent.
- [ ] Reproduce global hotkey behavior.
- [ ] Reproduce popup positioning and pinning behavior.
- [ ] Reproduce dark popup visual style.
- [ ] Compare app startup time, memory use, packaging complexity, and maintenance cost.
- [ ] Document the result before replacing the main app.

WinUI 3 migration can replace the main app only if all parity items pass and the maintenance/deployment cost is acceptable.

## Phase 5 — Multi-provider & WinUI (approved 2026-07-22)

The maintainer explicitly approved the following scope changes on 2026-07-22:

1. Expand the product scope from Codex-first to multi-provider, limited to an approved whitelist: Codex (OpenAI) and Claude (Anthropic). No other providers without new explicit approval.
2. Rename the repository / product / namespace / executable to QuotaScope.
3. Apply WinUI 3 to the main app. The Phase 4 prototype-only constraint is lifted, but the Phase 4 parity checklist must still pass before the WinForms app is replaced.
4. Add a dedicated settings window and user-configurable hotkeys.

Planned sub-phases (each is a separate task and a separate commit):

- Phase 5.0: rename the project to QuotaScope and update rule documents. (This phase.)
- Phase 5.1: introduce an `IUsageProvider` abstraction while keeping Codex as the only provider.
- Phase 5.2: add a Claude usage provider.
- Phase 5.3: port the UI to WinUI 3, split settings into a dedicated window, and support custom hotkeys.

### Display model decisions (maintainer, 2026-07-22)

Context: the Codex app-server payload changed upstream. The 5-hour window is gone
(`secondary` is now `null`), the weekly window is the only `primary`, and new
fields appeared: `credits` (`hasCredits`/`unlimited`/`balance`),
`spendControlReached`, and top-level `rateLimitResetCredits`. The old hardcoded
5h/1w row layout therefore renders an empty 5h row.

Decisions (apply from Phase 5.1 onward):

1. **Rows are payload-driven (dynamic).** Render only the rate-limit windows the
   provider actually returns. No hardcoded 5h/1w slots. Row labels derive from
   `windowDurationMins` (300 -> `5h`, 10080 -> `1w`).
2. **Per-provider display options are mix-and-match.** The user selects which
   row groups to show per provider: primary windows are always shown; secondary
   model rows (e.g., GPT-5.3-Codex-Spark) and credit rows are individually
   toggleable.
3. **Credits are a symmetric concept across providers.** Codex `credits` and
   Claude `extra_usage` both map to an optional "Credits" row controlled by a
   per-provider display option.
4. **Settings schema.** `providers` dictionary with per-provider
   `{ enabled, refreshSeconds, showSecondaryRows, showCredits, command }`.
   No migration from the old flat keys (pre-release policy).
5. The Phase 5.1 "pixel-identical UI" completion condition is superseded by
   decision 1: the empty 5h row disappears by design.

## Suggested task order for Codex

Use small tasks like these:

1. "Audit public repo hygiene and report only. Do not modify files."
2. "Polish the existing Windows Forms app: clean theme names, add a black theme, add a more glass-like theme, and add the app icon. No WinUI 3 migration. No backward-compatibility migration required before first public release."
3. "Move settings storage to per-user AppData if it stays simple. No migration required before first public release."
4. "Add GitHub Actions build workflow for Windows. Do not publish artifacts yet."
5. "Draft README sections for install, usage, privacy, limitations, and disclaimer using provided screenshots."
6. "Create a separate WinUI 3 prototype branch and report feasibility. Do not merge into main."

## Source notes

- Microsoft .NET support policy: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- Windows App SDK release channels: https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels
- WinUI 3 overview: https://learn.microsoft.com/en-us/windows/apps/winui/winui3/
