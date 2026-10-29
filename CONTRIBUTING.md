# Contributing to Unravel

Thanks for helping. Bug reports, tested fixes and well-scoped features are all welcome.

## Before you start

- For anything bigger than a small fix, open an issue first so we can agree on the approach.
- Unravel removes data from people's PCs. Every change that deletes, disables or ends something must keep
  the existing safeguards: the safety engine's confidence rating, blocked critical items, the deletion
  preview, registry backups and quarantine.
- This is a clean-room project. Do not contribute code, text, icons or other assets taken from another
  uninstaller, and do not contribute findings obtained by decompiling or disassembling one.

## Build and test

Requirements: Windows 10 2004 or later, the .NET 8 SDK.

```powershell
dotnet build Unravel.sln
dotnet test tests/Unravel.Tests
```

- Tests must never touch real user data. Registry tests run under `HKCU\Software\UnravelTests\<random>`
  and file tests under `%TEMP%\unravel-tests\<random>`; use the fixtures in `tests/Unravel.Tests/Fixtures.cs`.
- `tests/e2e` holds PowerShell scripts that drive the real app through UI Automation, including a harmless
  demo program with leftovers (`demo_install.ps1`). Run the relevant ones when you change a workflow.

## Code style

- Behaviour goes in `src/Unravel.Core` (no UI); `src/Unravel.App` only presents it.
- Match the surrounding code: naming, comment density and idioms.
- Keep the build at zero warnings.

## Pull requests

- One topic per pull request, with a short description of what changed and how you tested it.
- Add or update tests for behaviour changes.
- Mention anything you could not test, such as paths that need administrator rights.
