# Added features (beyond the original's scope)

Six features were added on top of the Revo Uninstaller Free feature set. They share one safety engine, one
quarantine for reversible deletion, and one activity log. Everything here was tested on a real Windows 11
system (build 26300, standard user rights); see *Evidence* under each feature.

## Shared foundations

| Component | What it does |
|---|---|
| **Safety engine** (`Core/Safety/SafetyEngine.cs`) | Rates every deletion candidate **High / Medium / Low** from *why* it was found (17+ categories), then adjusts for risk flags: system-critical, in use by a running program, Microsoft-signed, shared with another installed program, not created by the program, contains programs, Windows service. **System-critical items are blocked**: they can't be checked, and the remover skips them even if code tries. Critical means Windows folders, WindowsApps, Recovery/Boot, root system files, `HKLM\SYSTEM\…\Control`, Winlogon, IFEO, policies, kernel drivers and boot/system-start services, core services such as RpcSs, and Microsoft-signed `.sys` files. |
| **File signatures** (`FileSignature.cs`) | `WinVerifyTrust` for embedded Authenticode, plus a Windows catalog lookup (`CryptCATAdmin*`) for catalog-signed system files. Runs offline (no revocation network calls) and caches by file timestamp. |
| **Deletion preview** (`DeletionPreviewDialog`) | Shown before every destructive action. Groups items by confidence, warns about low-confidence items, and states how to recover. The selectable variant (used by the install tracker) pre-ticks only safe items. |
| **Quarantine** (`Quarantine.cs`) | Reversible deletion. Items are moved (or copied, then deleted, across volumes) into `%LOCALAPPDATA%\Unravel\Quarantine` with a manifest that is saved after every item. Restore puts items back, using a new name if the original path is in use again. Purge deletes them permanently. |
| **Activity log** (`ActivityLog.cs`) | One JSON object per line, one file per day in `%LOCALAPPDATA%\Unravel\Logs`. Every change Unravel makes (uninstall deletions, junk, cleaners, secure delete, startup changes, quarantine, WinGet, ended processes, tracker cleanups) and every failure is recorded with details. Viewable under **Activity & recovery**. |

## 1. Smart Uninstall Safety Engine

- The uninstall wizard shows a confidence badge on every registry and file leftover, with an explanation tooltip.
- **Select recommended** (formerly *Select all*) ticks only High and Medium items without risk flags. Low-confidence items must be ticked one at a time.
- "Check all leftovers by default" now pre-selects only safe items.
- Both delete buttons open the deletion preview instead of a plain yes/no question.
- In-use detection: an install folder that contains a running program is lowered one confidence level and flagged.

*Evidence:* `SafetyEngineTests` (19), `ScannerSafetyIntegrationTests` (every scanner reason maps to a category), blocked-node tests, and end-to-end uninstall runs with **Select recommended** (9/9 demo parts removed). One real issue was found and fixed during testing: items an install log recorded inside Windows' own settings were rated High. They are now Low and never pre-selected (`TrackerSafetyTests`, `RecordedItemsInWindowsOwnSettingsAreLowConfidenceInTheUninstaller`).

## 2. Installation Change Tracker

- **Snapshots** of Program Files (both), ProgramData, AppData (Roaming, Local, LocalLow), the Start menu and desktops; the registry under `HKCU\Software`, `HKLM\SOFTWARE` (including WOW6432Node) and `HKLM\…\Services`; and all services, scheduled tasks (Task Scheduler COM) and startup entries.
- Values are stored as 64-bit hashes, and snapshots are compressed binary files.
- A compiled noise filter drops caches, temp files, logs, browser profiles and Windows bookkeeping keys.
- **Diff** reports added, removed and modified items. Added folders and keys are reported once, with the number of items inside.
- **Logs** are linked to installed programs by the Uninstall key the install created, falling back to the name. The uninstaller's leftover scan then includes everything the install recorded ("Recorded during installation", High). Items recorded in Windows' own areas are Low.
- **Cleanup** removes only *added* items. Modified items are reported and never reverted blindly. In the selectable preview the user can untick anything another program may have created during the recording.
  - Files go to quarantine.
  - Registry items are exported to `.reg` files first, and nothing is deleted if the backup fails.
  - Scheduled tasks are exported to XML, then deleted.
  - Services are stopped and removed with `sc delete` (administrator only).
- **Undo cleanup** restores all of it: `.reg` import, task re-registration, and quarantine restore.

*Evidence:*
- Unit tests (`InstallTrackerTests`, 7): a sandbox install, a real scheduled task created with `schtasks`, save/load, cleanup then undo, and the uninstaller integration.
- Real-system snapshot: about 900k files and 470k registry keys in **about 55 s** when run as a test, and **32–37 s** in the app.
- UI end-to-end: snapshot → demo installer → compare (15 added, 12 modified) → removed 9/9 demo parts → undo restored 9/9.
- With the log present, a **Safe**-mode uninstall removed 9/9 parts; without a log it removes 6/9.

*Limits:*
- The Windows folder (System32, drivers) is not snapshotted, for speed. Drivers that installers copy there are seen only through their service entries.
- Changes made by other programs during the recording window appear in the log. That's why the preview lets you untick them, and why Windows' own areas are Low.
- Restored services need a restart to be registered again.

## 3. WinGet Package Manager Integration

- Runs `winget.exe` non-interactively, with output streamed into a console panel; progress bars are reduced to a "Progress: x / y" line.
- Cancel ends the whole process tree.
- **List, updates, search, install** (with a scope choice), **update / update all, uninstall, reinstall, export, import** ("Reinstall from list", latest or listed versions).
- Uninstall runs through the normal leftover wizard: the package is mapped to its Programs entry by ARP key or name, so the scan knows where it lived.
- **Error handling:** WinGet's HRESULT exit codes are mapped to plain messages, but only codes whose meaning is certain. Otherwise WinGet's own last message is shown with the hex code. A missing WinGet, or a need for administrator rights (with an offer to restart elevated), are both handled.
- The table parser is language-independent: columns come from header positions, and by order when headers are localised.

*Evidence:*
- `WinGetParserTests` (5, including a localised German header and two-table upgrade output).
- `WinGetLiveTests`: list maps ≥90% of ARP entries to registry keys; an unknown package returns code `0x8A150014` with a clear message; cancellation works.
- **The live install → export → reinstall → uninstall test of `jqlang.jq` passed** (opt-in: `UNRAVEL_LIVE_WINGET=1`).
- UI end-to-end: search → install → uninstall through the wizard → scan; WinGet confirms jq is gone.

## 4. Startup & Performance Insights

- **Startup impact** uses Task Manager's published thresholds (High: >1 s CPU or >3 MB disk I/O; Medium: 0.3–1 s or 0.3–3 MB).
  - Measured from the processes each enabled entry started, relative to sign-in (`sihost.exe` start time) for apps and boot time for services.
  - When Windows hides a SYSTEM service's path from a standard user, the executable name is used instead.
  - Entries can be switched on or off in place (same mechanism as the Startup manager).
- **Boot duration** comes from Windows' Diagnostics-Performance log (event 100). It needs administrator rights; otherwise the page says so.
- **Background processes:** apps (visible window), background programs and Windows processes, each with CPU and memory totals. Non-Windows processes are listed with end (guarded), open location and search actions.
- **Disk usage:** a cancellable folder-size tree (junctions are never counted twice) and the largest files; files can be moved to the Recycle Bin, with critical paths blocked.
- **Before & after baselines:** a 15-second CPU average plus startup counts, memory, commit, processes, background processes, threads, running services, system drive free space and boot time. Comparison shows better or worse per measure.

*Evidence:*
- `PerformanceTests` (9): thresholds, real startup analysis, boot info, disk usage sums and cancellation, baseline capture/store/compare.
- UI end-to-end: two baselines captured and compared (11 measures).
- Two accuracy bugs were found on the real system and fixed: running services shown as "not running", and sign-in measured from boot.

*Limit:* per-process counters cover the process's whole life, so long-running programs show an upper bound of their startup cost. The page says this in each row.

## 5. Duplicate & Orphan File Finder

- **Duplicates:** grouped by size, then a SHA-256 of the first and last 64 KB plus the length, then a **full SHA-256**. File names are never used.
  - Hard links are recognised by volume serial and file index and counted as one physical file.
  - Hashing runs in parallel with bounded workers, can be cancelled, and counts in-use files as unreadable rather than failing.
  - Keep rules: newest, oldest, shortest path, or the copy in a chosen folder.
  - **Deletion is refused if any group would lose every copy.** Removal goes to quarantine.
- **Orphans:** a folder in Program Files, ProgramData or AppData is reported only when *all* of these hold:
  1. No installed program or Windows app owns it, by folder, name, publisher or a distinctive name stem in any word of the folder name.
  2. Nothing refers into it: services (including ServiceDll), scheduled tasks, startup entries, file-association handlers, running processes, `PATH` and other environment variables, Start-menu, desktop or taskbar shortcuts.
  3. It isn't owned by SYSTEM or TrustedInstaller under Program Files.
  4. **No file inside** has been modified for 30 days.

  Each candidate lists this evidence, is Low confidence, and is never pre-ticked. Broken shortcuts (target missing on a present local drive) are reported too.

*Evidence:*
- `DuplicateFinderTests` (7): same name with different content, files differing only in the middle, hard links, size and pattern filters, keep rules with the last-copy guard, cancellation, locked files.
- `OrphanFinderTests` (5): including name-alone never qualifying, stems, broken shortcuts, and a real-system scan that never reports critical or installed-program folders.
- UI end-to-end on real folders: 37 duplicate groups (354 MB) in your media folders.
- Real-system orphan review found false positives (`Arduino15`, `chocolatey`, `com.seelen.seelen-ui`), which were fixed with the stem and environment/shortcut reference rules.

## 6. Live System Monitor

- One `NtQuerySystemInformation(SystemProcessInformation)` call per second gives CPU time, memory and I/O for **every** process, including protected ones, without opening handles.
- System CPU comes from `GetSystemTimes`, memory from `GlobalMemoryStatusEx`, and physical disk throughput and active time from PDH counters (English counter names, so any UI language works).
- Sampling runs only while the page is visible and not paused.
- **Alerts:**
  - sustained per-process CPU (default ≥25% of all cores for 30 s; Critical at ≥80%);
  - steady memory growth (≥300 MB in 60 s with ≥80% rising samples);
  - a process over 25% of RAM;
  - sustained I/O ≥50 MB/s;
  - an unsigned program running from Temp, Downloads, the Recycle Bin or Public;
  - **a Windows system file name running outside the Windows folder** (Critical);
  - system-wide CPU and low memory.

  Each alert has advice and buttons (End process, Open location, Search, Ignore). Alerts clear when the condition clears.
- **Critical-process guard:** System, smss, csrss, wininit, winlogon, services, lsass, svchost, dwm, MsMpEng and more, anything `IsProcessCritical` reports, and Unravel itself can never be ended. Explorer gets a warning.
- **Services tab:** start and stop (administrator), with core services protected.

*Evidence:*
- `MonitorTests` (7): sane sampling including protected processes; lsass, System and itself protected, with a harmless process ended; sustained-CPU raise/resolve; short spikes ignored; memory growth; masquerading and unsigned-temp alerts with ignore; services listing and core-service protection.
- UI end-to-end: a compiled CPU burner in `%TEMP%` triggered "cpuburner.exe is using 74% CPU" and "Unsigned program running from a temporary folder". **End process** from the alert ended it, and the alerts cleared.

## Not done / needs a human

- Anything that needs administrator rights was not exercised end-to-end: `HKLM` and Program Files cleanup, service removal by the tracker, service start/stop, and the boot duration reading. The code reports these clearly and offers **Restart as administrator**.
- Real Windows-app (MSIX) removal was not executed, to avoid uninstalling the tester's apps.
