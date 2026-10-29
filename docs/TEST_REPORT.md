# Test report

**Date:** 2026-10-29.
**Machine:** Windows 11 Pro Education N, build 26300, x64, .NET SDK 8.0.427.
**Rights:** tests ran as a standard user (not elevated). The original Revo Uninstaller 2.6.8 was open,
elevated, on the same desktop.

## Update: six added features (later the same day)

| Suite | Result |
|---|---|
| Release build | **0 warnings, 0 errors** |
| Automated tests | **222 passed, 0 failed**, 2 skipped (a diagnostic dump; the live WinGet install test, which is opt-in and **passed when run** with `UNRAVEL_LIVE_WINGET=1`) |
| New UI end-to-end runs | Install tracker (snapshot → install → compare → remove 9/9 → undo 9/9); uninstall using the install log (Safe mode 9/9 instead of 6/9); Hunter drag → stop autostart → uninstall a running program (9/9); WinGet search → install → uninstall through the wizard; performance baselines captured and compared; live-monitor alerts raised by a CPU burner and the process ended from the alert; real-system duplicate and orphan scans |
| Regression | The earlier uninstall, startup and Hunter scenarios re-run after the safety changes: all pass |

Issue found while recording the README demo and fixed: the duplicate list's grouping squeezed every column to its 20 px minimum, so file rows looked empty (fix: a rows presenter as the group panel; verified on screen with three duplicate groups).

Issues found by the real-system runs and fixed: running services reported as not running (paths hidden from standard users), sign-in measured from boot, Windows bookkeeping keys pre-selected from an install log, and orphan false positives (`Arduino15`, `chocolatey`, `com.seelen.seelen-ui`). Details per feature are in [NEW_FEATURES.md](NEW_FEATURES.md). The scripts are `tests/e2e/e2e_tracker.ps1`, `e2e_features.ps1`, `e2e_winget_perf.ps1` and `e2e_hunter_uninstall.ps1`.

## Summary (original feature set)

| Suite | Result |
|---|---|
| Release build (`dotnet build -c Release --no-incremental`) | **0 warnings, 0 errors** |
| Automated tests (`dotnet test`) | **141 passed, 0 failed**, 1 skipped (a manual diagnostic dump) |
| End-to-end UI runs (UI Automation against the real app) | 6 scenarios passed, 1 blocked by the environment (see below) |
| Validation against the original and against Windows | 5 checks passed, 1 difference documented |

## Automated tests by area

| Test class | Tests | What it proves |
|---|---:|---|
| ProgramInventoryTests | 17 | Filtering (components, updates, parent keys, no-name and no-uninstaller entries); field reading; installer detection; install-date sources; localized indirect names; forced MSI / modify / repair command lines; clear errors for unsupported actions; invalid-uninstaller message; Remove Entry backs up and the backup restores; HTML escaping on export |
| ProgramInventoryLiveTests | 1 | Every program in Windows *Programs and Features* (shell folder `::{7B81BE6A…}`) is also in Unravel's list |
| LeftoverScannerTests | 15 | A fake product spread across a sandboxed registry and folder tree. Built-in mode finds nothing; Safe/Moderate/Advanced each find exactly their documented locations; unrelated data, other vendors' products, search roots and shared folders are never offered; name clashes with another installed program are demoted; tree semantics (bold leaves, check-a-branch); exclude rules (wildcards, include-subkeys, disabled rule); deletion removes only checked created items, writes a backup, and the backup restores; a failed backup blocks deletion; Mark as Created; HTML export; cancellation |
| NameMatcherTests | 11 | Version stripping, company-suffix stripping, generic-name rejection, protected real system locations, broad install folders ignored |
| AutorunTests | 12 | Status (running / not running / invalid), parsing of target and arguments, StartupApproved disable/enable bytes (Task Manager-compatible), reading existing disabled flags, RunOnce park/restore, Add (Run, RunOnce, Startup folder) with duplicate and input validation, disable and remove for Startup-folder items, Remove clears the approval flag, Run and Kill of a real process, real-system load |
| JunkScannerTests | 8 | Patterns, 24-hour rule (and turning it off), folder exclusions, junctions are not followed, cancel returns partial results, input validation, locked files reported, defaults from settings |
| CleanupEngineTests | 8 | Registry modes (values / subtree / single value) with preview-then-execute; files with pattern, age and lock handling; Chromium profile per category (history, downloads, cookies, forms, cache, sessions; payment cards and metadata kept); a locked browser database is reported as in use; Firefox history keeps bookmarked places; Office lists across versions and per-account lists; Windows cleaner on redirected locations (recent temp files kept); real-system previews run without errors |
| SecureDeleteTests | 6 | Content is random and the same length before deletion and the renamed copy is gone; recursive folders with read-only/hidden files; **a junction inside a folder does not erase its target**; locked files are reported while others are erased; missing paths; cancellation |
| FreeSpaceWiperTests | 2 | Requested passes and bytes written, file-table record pass, progress reaches 100%, temporary files removed after completion and after cancel |
| WindowsAppsTests | 5 | Frameworks and system packages excluded by default; names match the Start menu ("Calculator"); hidden apps use the manifest name (exact match with the original's text for the DDLM package); stub packages listed; Windows-app leftover scan uses the package identity and never the WindowsApps folder |
| WindowsToolsTests | 16 | 18 unique tools; 15 core tools available on this Windows |
| HunterResolverTests | 3 | Most-specific install folder wins; shortcut-by-label resolution; the window under a point resolves to the owning process (cross-checked with an independent API) |
| Util tests (Wildcard, RegPath, CommandLine, MsiGuid, FileOps, ShellLink, Settings) | 37 | Parsing, 32-bit registry view rendering, quoted and unquoted command lines with spaces, packed GUIDs, deletes and locks, shortcuts, settings round-trip and recovery from a corrupt settings file |

All registry tests run inside `HKCU\Software\UnravelTests\<random>`, and all file tests run inside
`%TEMP%\unravel-tests\<random>`. Both are removed afterwards. The tests never touch real program data.

## End-to-end UI runs (`tests/e2e`)

The scripts drive the real `Unravel.exe` through Windows UI Automation, the same way assistive
technology would. `demo_install.ps1` installs a harmless per-user program, *Contoso Demo App*. Its
uninstaller removes only its folder and registration, and leaves 7 typical leftovers behind.

| Scenario | Result |
|---|---|
| Programs: search → select → Uninstall → confirm → wizard runs the uninstaller and waits → **Moderate** scan → Select all → Delete (registry) → Select all → Delete (files) → Finish | ✅ 4 registry + 3 file leftovers found; **9/9 demo parts gone** |
| Same with **Safe** | ✅ Program keys and Start menu removed; AppData folders and the Run entry kept, as designed (6/9 gone) |
| Same with **Advanced** | ✅ 9/9 gone; no extra items flagged on the real system |
| Startup manager: flip the switch off and on for a test Run entry | ✅ StartupApproved went none → `0x03` (disabled) → `0x02` (enabled) |
| Every page opens without an error dialog (12 pages) | ✅ Screenshots reviewed in dark and light themes |
| Windows history → Analyze on the real system (read-only) | ✅ 30,951 items / 5 GB found, no errors |
| Hunter mode: drag the target onto Notepad / the demo program | ✅ Verified later once the elevated original was closed: real mouse drags identify the window, show the action menu and the options menu, *Stop starting with Windows* sets the StartupApproved flag, and *Uninstall…* closes the running program and removes 9/9 demo parts (`e2e_hunter.ps1`, `e2e_hunter_uninstall.ps1`). |

## Validation against the original and against Windows

| Check | Result |
|---|---|
| Number of installed programs | ✅ Unravel **114** = Revo status bar "Installations: 114" |
| Programs and Features (Windows' own list) | ✅ 0 missing; an earlier run found one (*Remote Desktop Connection*, registered via `DisplayName_Localized`), which was fixed |
| Install dates | ✅ Both default to the uninstall key's last-write time (e.g. 9/11/2026 11:03:30 AM in the original) |
| Startup enabled/disabled states | ✅ Match Windows' StartupApproved data (same source as Task Manager) |
| Windows Apps naming | ✅ Start-menu names and manifest names match the original's rows (Calculator, Clock, HEIC to JPEG, Microsoft Windows App Runtime DynamicDependency LifetimeManager) |
| Windows Apps count | ⚠️ Original 59, Unravel 68. The extra entries include Camera, Copilot, Microsoft Edge, AMD Radeon Software and App Installer. No package property separating them could be found (signature kind, non-removable flag, stub, bundle, install location were compared). |

## Not tested, and why

| Area | Reason | Mitigation |
|---|---|---|
| Anything needing administrator rights: `HKLM` leftover deletion, restore point creation, service start-type changes, `C:\Windows\Temp`, delete-on-reboot | The session was not elevated, and a UAC prompt would need a human | The code paths report access-denied clearly. The restore point reports "requires administrator" (seen in E2E). |
| Real Windows-app removal | Would uninstall the tester's apps | Listing, naming and leftover identity are tested. Removal is a single Windows API call (`RemovePackageAsync`). |
| Real MSI uninstall / repair / modify | No disposable MSI package was available on the machine | Command lines are unit-tested. Execution uses the same tested start-and-wait path. |
| Cleaning real browser, Office and Windows history | Destroys the tester's data | Synthetic profiles and redirected registry locations; live runs were preview-only |
| Full free-space wipe of a real drive | Takes hours and fills the disk | Same code with a 6 MB cap; cancellation and cleanup tested |
| Recycle Bin delete path | Would fill the tester's Recycle Bin | Uses the standard Windows shell API (`SHFileOperation` with `FOF_ALLOWUNDO`); falls back to reporting the error |
| Tray-icon resolution in Hunter mode | Depends on Windows' tray accessibility labels | Best effort, documented as Partial |

## Reproducing

```powershell
dotnet test tests/Unravel.Tests
dotnet build src/Unravel.App
tests/e2e/demo_install.ps1; tests/e2e/e2e_uninstall.ps1 -Mode ModeModerate; tests/e2e/demo_check.ps1
tests/e2e/e2e_startup.ps1
```
