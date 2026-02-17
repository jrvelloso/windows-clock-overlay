# Contributing

Thanks for contributing.

## Local setup
```powershell
dotnet --version
dotnet restore
dotnet build .\windows-clock.sln
dotnet run --project .\WindowsClockOverlay
```

## Branch and PR flow
1. Create a feature branch from `main`.
2. Keep changes focused and small.
3. Run `dotnet build .\windows-clock.sln` before opening PR.
4. Open a PR using `.github/PULL_REQUEST_TEMPLATE.md`.

## Coding standards
- Keep behavior predictable and minimal.
- Prefer explicit naming and small methods.
- Avoid adding dependencies unless clearly needed.
- Preserve current UX simplicity.

## Commit messages
Use concise, descriptive commit titles, for example:
- `feat: persist overlay color and position`
- `fix: load tray icon from embedded resource`

## Reporting bugs
Use the bug report template and include:
- Windows version
- Steps to reproduce
- Expected vs actual behavior
- Screenshot if relevant
