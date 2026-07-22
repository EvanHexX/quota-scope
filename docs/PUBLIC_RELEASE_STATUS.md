# Public Release Status

Last updated: 2026-06-21

## Completed

- Added repository-level `AGENTS.md` for Codex work rules.
- Added `docs/MODERNIZATION_PLAN.md` for .NET and optional WinUI 3 planning.
- Refined `.gitignore` for .NET desktop build outputs, editor state, logs, and local settings.
- Rewrote `docs/README.md` to remove maintainer-local absolute paths and make the notes more public-safe.
- Polished Windows Forms theme IDs/names and added the app/executable icon.

## Current technical status

- Current app project: `app/QuotaScope.csproj`
- Current target framework: `net10.0-windows`
- Current UI framework: Windows Forms
- Local verification: `dotnet build app/QuotaScope.csproj` and `dotnet run --project app/QuotaScope.csproj -- --self-test` pass with .NET SDK 10.0.301.
- Recommended near-term direction: keep the app on the supported modern .NET LTS target while preserving Windows Forms.
- Recommended WinUI 3 direction: TODO/prototype later in a separate branch; do not rewrite the main app before the first stable public release.

## Remaining before first public release polish

- Add `LICENSE`.
- Update `README.md` with install, build, usage, privacy, limitations, and disclaimer sections.
- Add screenshots after redacting private account details.
- Add a Windows GitHub Actions build workflow.
- Decide release packaging: framework-dependent zip, self-contained zip, or installer/MSIX later.

## Recommended next Codex task

Ask Codex to add CI without changing app behavior:

```text
Read AGENTS.md and docs/MODERNIZATION_PLAN.md first.
Add a GitHub Actions workflow that builds app/QuotaScope.csproj on windows-latest with .NET 10.
Run the existing self-test in CI.
Do not add packaging or WinUI 3 migration in this task.
```
