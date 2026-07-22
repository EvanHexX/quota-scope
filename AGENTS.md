# AGENTS.md

## Project identity

This repository is `quota-scope`.

QuotaScope is an unofficial Windows tray app for quickly checking AI coding assistant usage limits without opening each provider's app, CLI view, or web dashboard.

The product is multi-provider, limited to an approved provider whitelist: **Codex (OpenAI)** and **Claude (Anthropic)**. Do not add Gemini, Cursor, OpenAI API billing, GitHub Actions quota, or any other provider without a new explicit maintainer approval.

## Current implementation assumptions

- Main app project: `app/QuotaScope.csproj`
- Current UI stack: Windows Forms tray app
- Current target framework: check `app/QuotaScope.csproj` before changing it
- Entry point: `app/Program.cs`
- Tray lifecycle/menu/hotkey wiring: `app/TrayApplicationContext.cs`
- Popup UI: `app/UsagePopupForm.cs`
- Provider abstraction: `app/Providers/IUsageProvider.cs`, `app/Providers/UsageModels.cs`
- Codex app-server client: `app/Providers/Codex/CodexAppServerClient.cs`
- Codex rate limit mapping: `app/Providers/Codex/RateLimitMapper.cs`
- Claude usage provider: `app/Providers/Claude/` (undocumented OAuth usage endpoint; token is read per poll and never stored or logged)
- Project map: `docs/PROJECT_MAP.md`
- Modernization plan: `docs/MODERNIZATION_PLAN.md`

The app currently launches `codex app-server`, communicates over stdio JSON-RPC, calls `account/rateLimits/read`, and listens for `account/rateLimits/updated`. It should not require a separate OpenAI API key.

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
dotnet build app/QuotaScope.csproj
dotnet run --project app/QuotaScope.csproj -- --self-test
```

For UI, tray, hotkey, or popup changes, also report a manual Windows smoke-test checklist covering:

- tray icon appears
- popup opens from tray click
- `Ctrl+Alt+U` toggles popup
- Refresh/Reconnect menu still works
- pinned popup behavior still works
- settings persist after restart

If a command fails, report the exact failure. Do not claim verification that was not actually run.

## Modernization policy

Follow `docs/MODERNIZATION_PLAN.md`.

The preferred modernization path is:

1. public-repo hygiene and documentation
2. Windows Forms app on a supported modern .NET LTS target
3. pre-release polish and settings cleanup
4. public release packaging
5. optional WinUI 3 prototype only after the current app is stable

Do not migrate to WinUI 3 in the same task as .NET/theme/icon/README polish. Treat WinUI 3 as a separate prototype/rewrite phase with its own branch and parity checklist.

## Pre-release compatibility policy

There has not been a public release or external download yet.

Until the first tagged public release, compatibility with old local settings, theme IDs, names, screenshots, or internal option strings is not required. Prefer clean names and simple code over migration code.

After the first tagged public release, breaking changes to settings, theme IDs, command-line behavior, or persisted app data should be treated as compatibility-sensitive and documented.

## Public repository safety

Never commit:

- secrets, tokens, API keys, cookies, session data, or credential files
- local absolute paths from a maintainer machine
- screenshots containing private account information or usage details that were not intentionally redacted
- generated build outputs such as `bin/`, `obj/`, publish folders, or local installer artifacts
- telemetry, analytics, or remote logging without explicit approval

Settings should remain local. If settings storage changes before the first tagged public release, no compatibility migration is required unless the maintainer explicitly requests it.

## Branding and trademark safety

This is not an official OpenAI or Anthropic project.

Do not use OpenAI or Anthropic logos, official product artwork, or wording that implies affiliation, endorsement, or sponsorship.

Preserve this disclaimer in public-facing documentation when applicable:

> This project is not affiliated with, endorsed by, or sponsored by OpenAI or Anthropic. Codex is a product/service of OpenAI. Claude is a product/service of Anthropic.

## Documentation rules

For user-facing behavior changes, update `README.md` or `docs/` in the same change.

Keep README claims accurate. Do not claim support for providers, platforms, package formats, installers, or auto-update mechanisms that are not implemented.

Public-facing documentation should be in English unless the maintainer explicitly asks otherwise.

## Git behavior

Do not rewrite history.
Do not force-push.
Do not change unrelated files.
Do not add new production dependencies unless the task requires them and the reason is documented.
Do not generate large binary artifacts unless explicitly requested.
Until the maintainer changes this instruction, include `Co-authored-by: effigiamsn <effigiamsn@users.noreply.github.com>` in project commits.
