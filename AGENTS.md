# AGENTS.md

## Project identity

This repository is `quota-scope`.

QuotaScope is an unofficial Windows tray app, plus a macOS menu bar app built from source, for quickly checking AI coding assistant usage limits without opening each provider's app, CLI view, or web dashboard.

The product is multi-provider, limited to an approved provider whitelist: **Codex (OpenAI)** and **Claude (Anthropic)**. Do not add Gemini, Cursor, OpenAI API billing, GitHub Actions quota, or any other provider without a new explicit maintainer approval.

## Current implementation assumptions

The repository holds two Windows apps and a macOS app. `app-winui/` is the released Windows app. `app/` is the legacy Windows Forms app, kept in the tree because the WinUI project compiles several of its files directly. `mac/` is the macOS menu bar app, a Swift port that users build on their own Mac (no Mac download, installer, or auto-update).

### Released app (WinUI 3)

- Main app project: `app-winui/QuotaScope.WinUI.csproj`
- UI stack: WinUI 3 / Windows App SDK, unpackaged and self-contained, x64
- Target framework: check `app-winui/QuotaScope.WinUI.csproj` before changing it
- Entry point: `app-winui/Program.cs` — a hand-written `Main` (`DISABLE_XAML_GENERATED_MAIN`) so `--self-test` runs before any XAML initialization
- Tray lifecycle, polling, menu, and hotkey wiring: `app-winui/TrayController.cs`
- Popup UI: `app-winui/Windows/UsagePopupWindow.cs`
- Settings window: `app-winui/Windows/SettingsWindow.cs`
- Tray icon host (Win32 `NotifyIcon` + `TrackPopupMenu`): `app-winui/Tray/TrayIconHost.cs`

### Shared sources

These files live under `app/` and are compiled by **both** projects, via the `Compile Include` items in `app-winui/QuotaScope.WinUI.csproj`. Editing one of them changes both apps.

- Settings model/load/save: `app/AppSettings.cs`
- Tray icon rendering: `app/TrayIconRenderer.cs`
- Hotkey parsing: `app/Hotkeys/HotkeyDefinition.cs`
- Provider abstraction: `app/Providers/IUsageProvider.cs`, `app/Providers/UsageModels.cs`
- Codex app-server client: `app/Providers/Codex/CodexAppServerClient.cs`
- Codex rate limit mapping: `app/Providers/Codex/RateLimitMapper.cs`
- Claude usage provider: `app/Providers/Claude/` (undocumented OAuth usage endpoint; token is read per poll and never stored or logged)

### Legacy app (Windows Forms, not released)

- Project `app/QuotaScope.csproj`, entry point `app/Program.cs`
- Tray lifecycle/menu/hotkey wiring: `app/TrayApplicationContext.cs`
- Popup UI: `app/UsagePopupForm.cs`
- It still builds, and it must keep building when a shared file changes. Do not port WinUI-only features into it unless the maintainer asks.

### macOS app and shared fixtures

- Swift package: `mac/Package.swift`. Shared logic (models, mappers, rows, alerts, settings model, hotkey grammar) in `mac/Sources/QuotaCore/`, providers in `mac/Sources/QuotaProviders/`, the menu bar app in `mac/Sources/QuotaScope/`, and the build/sign/install script `mac/build.sh`.
- Shared fixtures: `fixtures/<folder>/<case>.input.json` + `.expected.json`, read by the C# `--self-test` (embedded in the exes) and by `swift run quotascope-selftest`. Format and rules: `docs/MACOS_BUILD.md`, section 5.
- **C# is the reference.** A fixture is added or changed only together with the C# behavior, in the same commit, and the Swift side is brought in line with it. A fix made on one side without a fixture is the drift to watch for in review.

### Reference docs

- Project map: `docs/PROJECT_MAP.md`
- WinUI platform workarounds and popup chrome decisions: `docs/WINUI3_PARITY.md`
- macOS app design, build, shared fixture format, and Mac smoke test: `docs/MACOS_BUILD.md`
- Historical planning records: `docs/MODERNIZATION_PLAN.md`, `docs/PUBLIC_RELEASE_STATUS.md`

The app launches `codex app-server`, communicates over stdio JSON-RPC, calls `account/rateLimits/read`, and listens for `account/rateLimits/updated`. It should not require a separate OpenAI API key.

## Required workflow

Before editing code:

1. Read this file.
2. Read `README.md` and `docs/PROJECT_MAP.md`.
3. Inspect the specific source files affected by the task.
4. Summarize the intended change and keep it narrowly scoped.

Prefer small, reviewable diffs. Do not perform broad refactors, UI framework rewrites, namespace changes, or provider expansions as part of an unrelated fix.

## Build and verification

Use the smallest relevant command first.

```powershell
dotnet build app-winui/QuotaScope.WinUI.csproj
dotnet run --project app-winui/QuotaScope.WinUI.csproj -- --self-test
```

Build `app/QuotaScope.csproj` as well when the change touches a shared file under `app/`, so the legacy app is not left broken.

When a shared file under `app/` or a fixture under `fixtures/` changes, also run the Swift checks in `mac/` if a Swift toolchain is available (on Windows it needs an x64 Developer Command Prompt; see `docs/MACOS_BUILD.md` 7.3). Otherwise rely on CI and say so in the report:

```sh
cd mac
swift run quotascope-selftest
swift run quotascope-providertest
```

CI (`.github/workflows/ci.yml`) runs on every push and pull request: on Windows it builds both C# projects and runs both `--self-test`; on macOS it runs `swift build`, `quotascope-selftest`, and `quotascope-providertest`.

The maintainer often has `QuotaScopeWinUI.exe` running, which locks `app-winui/bin/`. When the build fails only at the executable copy step (`MSB3026`/`MSB3027`), build to a scratch output instead of killing the running app:

```powershell
dotnet build app-winui/QuotaScope.WinUI.csproj -p:BaseOutputPath=$env:TEMP/quota-scope-build/
```

For UI, tray, hotkey, or popup changes, report a manual Windows smoke-test checklist for the maintainer to run, and do not launch the GUI yourself unless asked. Cover:

- tray icon appears
- popup opens from tray click
- `Ctrl+Alt+U` toggles popup
- tray and popup Refresh/Reconnect menu items still work
- popup header refresh and pin buttons still work
- pinned popup behavior still works
- settings persist after restart

For changes to the macOS app, the Mac checklist is `docs/MACOS_BUILD.md`, section 10.

If a command fails, report the exact failure. Do not claim verification that was not actually run.

Keep the graphics-adapter self-restart working (`app-winui/GraphicsAdapters.cs`, `TrayController.CheckGraphicsAdapters`, `--restart-after` in `Program.cs`): WinUI 3 cannot redraw after a driver update (microsoft-ui-xaml#10844), so anything new that lives only in memory must survive or be re-created after that restart. See "Regression notes" in `docs/WINUI3_PARITY.md`.

## Modernization policy

`docs/MODERNIZATION_PLAN.md` and `docs/PUBLIC_RELEASE_STATUS.md` are historical records of how the app reached its current shape. Read them for background, not as current instructions: their "current state" sections still describe the pre-WinUI Windows Forms app.

The path they lay out is finished. WinUI 3 was approved on 2026-07-22, built on `feat/winui3-shell`, and shipped as `v1.0.0` on 2026-07-23. `docs/WINUI3_PARITY.md` is the doc that describes the app as it stands.

Still in force: do not perform a UI framework rewrite, a namespace change, or a provider expansion as part of an unrelated fix.

## Compatibility policy

The first public releases are tagged: `v0.1.0` (2026-06-21) and `v1.0.0` (2026-07-23, WinUI 3). The pre-release allowance to break local state no longer applies.

Breaking changes to settings keys, theme IDs, command-line behavior, or persisted app data are compatibility-sensitive. Do not rename or drop a settings key without either a fallback read or explicit maintainer approval, and document any such change in the READMEs.

Settings stay local: `settings.json` next to the executable (`AppContext.BaseDirectory`; on macOS `~/Library/Application Support/QuotaScope/settings.json`, with the same key names), and the app needs no credential of its own.

## Public repository safety

Never commit:

- secrets, tokens, API keys, cookies, session data, or credential files
- local absolute paths from a maintainer machine
- screenshots containing private account information or usage details that were not intentionally redacted
- generated build outputs such as `bin/`, `obj/`, publish folders, or local installer artifacts
- telemetry, analytics, or remote logging without explicit approval

Settings should remain local. Changes to how settings are stored are compatibility-sensitive now that `v1.0.0` is tagged; see the compatibility policy above.

## Branding and trademark safety

This is not an official OpenAI or Anthropic project.

Do not use OpenAI or Anthropic logos, official product artwork, or wording that implies affiliation, endorsement, or sponsorship.

Preserve this disclaimer in public-facing documentation when applicable:

> This project is not affiliated with, endorsed by, or sponsored by OpenAI or Anthropic. Codex is a product/service of OpenAI. Claude is a product/service of Anthropic.

## Documentation rules

For user-facing behavior changes, update `README.md` or `docs/` in the same change.

`README.md` (English) and `README.ko.md` (Korean) are both user-facing; keep user-facing changes in sync across the two.

Keep README claims accurate. Do not claim support for providers, platforms, package formats, installers, or auto-update mechanisms that are not implemented.

Public-facing documentation should be in English unless the maintainer explicitly asks otherwise. `README.ko.md` and the existing notes under `docs/` are the established exceptions.

## Git behavior

Do not rewrite history.
Do not force-push.
Do not change unrelated files.
Do not add new production dependencies unless the task requires them and the reason is documented.
Do not generate large binary artifacts unless explicitly requested.
Until the maintainer changes this instruction, include `Co-authored-by: effigiamsn <effigiamsn@users.noreply.github.com>` in project commits.
