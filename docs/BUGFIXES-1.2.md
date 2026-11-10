# Unravel 1.2: bug investigation and fixes

Each problem below was first reproduced or measured on a real Windows PC (Windows 11 build 26300, 113 programs,
162 packages). The cause was then traced, a failing test was written, and only then was the code changed.
Everything Unravel does here is read-only unless the user confirms an action.

## 1. Installed applications missing from the lists

| What was missing | Root cause | Fix | Evidence |
|---|---|---|---|
| **Per-user Windows Installer (MSI) programs** were not in *Programs* at all | Windows Installer does not write an `Uninstall` key for per-user products. Windows lists them from its own record under `HKLM\…\Installer\UserData\<user SID>\Products\<packed code>\InstallProperties`, which Unravel never read | That record is now a source, for the current user. A product that also has an `Uninstall` key is listed once. The record is Windows Installer's own, so **Remove entry** refuses to delete it and the leftover scan never offers it | A real per-user MSI (`tests/e2e/MsiDemo.wxs`) is listed by Windows' Programs and Features, `winget list` and `Get-Package`, but was absent from Unravel. Tests: `PerUserMsiDetectionTests` (5) |
| **10 of 225 WinGet packages** (Google Chrome, Android Studio, Microsoft Copilot, AMD Ryzen Master…) were missing from *Package manager* | The table parser dropped any row whose ID contained a space, to skip footer sentences. But for programs WinGet only knows from Programs and Features, the ID is the registry key name (`ARP\Machine\X86\Google Chrome`), which often has spaces | Spaces are allowed in `ARP\` and `MSIX\` IDs; footer sentences are still rejected | `winget list` on this PC: 225 rows, 215 parsed before, 225 after. Test: `PackagesWhoseIdContainsSpacesAreKept` |
| WinGet tables with **Chinese, Japanese or Korean names** were parsed into the wrong columns | WinGet pads columns by display width (a CJK character is two columns), the parser counted characters | Columns are measured in display columns | `WideCharactersDoNotShiftTheColumns`, `WideHeadersKeepTheColumnsToo` |
| Windows apps you can start from Start (Settings, Get Started, Click to Do) were hidden | Every system-signed package was hidden by default | System packages are listed when they have a Start menu entry; invisible plumbing stays under *Show frameworks and system apps* | `StartMenuSystemAppsAreListedButFrameworksAreNot` |
| Per-user programs written to `HKCU\Software\WOW6432Node\…\Uninstall`, and programs of another signed-in user when Unravel runs elevated with a different admin account | These locations were not read | Both are read; the other user's programs are labelled *Other user* | `DefaultSourcesReadExplicit32BitPerUserRegistrations`, `OnlyOtherPeoplesLoadedHivesAreAddedAsSources`, `ProgramsOfAnotherLoadedUserAreListedAsSuch` |

A read-only audit compared every source on the PC: Unravel's lists, `winget list`, `Get-AppxPackage`, `Get-StartApps`,
the raw registry and Start-menu shortcuts. After the fixes, no registered program or package was missing.
Programs that never register anywhere (portable apps with only a shortcut) cannot be listed by any of these sources.

## 2. Uninstaller failures

| Problem | Root cause | Fix | Evidence |
|---|---|---|---|
| **Uninstalling a Windows Installer program could leave it installed** | Windows Installer registers most products as `MsiExec.exe /I{code}`; 122 of 332 entries on this PC do. Microsoft documents `/i` as *install* and `/x` as *uninstall*. Run with `/I`, a package either opens its Change/Repair screen or simply installs itself again. WinGet also uninstalls MSI products by product code, not by this string | **Uninstall** now runs `MsiExec.exe /X{code}` for Windows Installer products. **Change** still uses `/I` | Real package: the registered `/I` command exited 0 and the program stayed installed (event 1035 *reconfigured the product*). Tests: `UninstallingAnMsiNeverRunsTheInstallSwitch` |
| Failed or cancelled uninstalls were reported as finished | The uninstaller's exit code was ignored | Windows Installer's documented codes are explained (1602 cancelled, 1603 fatal error, 1605 unknown product, 1612, 1618 another install running, 1925 needs admin, 1641/3010 restart). Other installers' codes are shown as-is, never called failures. Each run is written to the activity log | `WindowsInstallerExitCodesAreInterpreted`, `OtherInstallersExitCodesAreNotCalledFailures` |
| After a failed or cancelled uninstall, the leftover scan pre-selected the **working program** | Nothing told the scan that the program was still installed | When Windows still lists the program, nothing is pre-selected, every item is rated Low and a notice explains why | `NothingIsPreselectedWhileTheProgramIsStillInstalled` |
| Non-removable Windows apps (App Installer, Windows Security) could be sent for removal | The non-removable check read a property WinRT's `Package` class doesn't have, so it was always false | The flag comes from `Get-AppxPackage` (Windows' own answer). Such apps are listed but can't be uninstalled; the core refuses even if the UI is bypassed | `NonRemovableFactsComeFromWindowsNotFromAProbeThatNeverMatched`, `RemovingANonRemovablePackageIsRefusedBeforeWindowsIsAsked` |

## 3. Leftover detection

A read-only scan of all 112 installed programs (8,564 findings) was checked for items one program claimed that belong to another.

| Problem | Root cause | Fix | Test |
|---|---|---|---|
| Uninstalling Burp Suite or IntelliJ would pre-select the **JDK's** `Classes\Applications\java.exe` | The entry was claimed by file name alone (both bundle a `java.exe`) | The entry counts only when its open command runs this program's file | `AnApplicationsEntryOfAnotherProgramWithTheSameExeNameIsNotClaimed` |
| pgAgent and Bluefin claimed **PostgreSQL's** service and Start-menu shortcuts | They install into PostgreSQL's folder, and anything in that folder counted as theirs | Files in a folder that another installed program owns (same folder or a sub-folder) are not this program's | `ItemsInsideAFolderAnotherProgramAlsoOwnsAreNotClaimed`, `ItemsOfAPluginInstalledInsideTheProgramsFolderStayWithThePlugin` |
| **Scheduled tasks** a program left behind were not found (the README claimed they were) | The scanner had no scheduled-task step; only the install tracker knew tasks | Moderate and Advanced scans list tasks whose action runs a file of the program. `\Microsoft\` tasks are never offered. Each task is exported to XML before it is deleted (nothing is deleted if the export fails), and verification checks the task is gone | `ScheduledTasksThatRunTheProgramAreFoundButWindowsTasksNever`, `SafeScansDoNotLookAtScheduledTasks`, `ScheduledTaskRemovalExportsTheTaskFirstAndVerificationLooksItUp`, `ATaskIsNotDeletedWhenItsBackupFails` |

## 4. WinGet integration

| Problem | Root cause | Fix | Test |
|---|---|---|---|
| Wrong error messages | `0x8A150011` was described as *more than one package matches*. Microsoft's table defines it as *installer hash mismatch*; *multiple packages* is `0x8A150016` | The table was rechecked against Microsoft's return-code reference and extended (no sources, source open failed, pinned, uninstall command failed…) | `ErrorCodesFollowMicrosoftsTable` |
| One failing source emptied the whole list | Any non-zero exit code threw away the rows WinGet had printed | The rows are kept and the problem is logged; an error is shown only when nothing could be listed | `ListKeepsItsRowsWhenOneSourceFails`, `ListStillFailsLoudlyWhenThereIsNothingToShow` |
| Older WinGet versions (common on Windows 10) shorten long IDs with "…", and acting on them fails | They assume 120 columns when output is redirected | A shortened ID is expanded with a targeted `winget list/search --id` before any action. If it can't be expanded unambiguously, nothing is sent to WinGet | `TruncatedIdsAreExpandedBeforeActingOnThem`, `AnUnresolvableTruncatedIdIsNeverSentToWinGet` |
| A WinGet too old for Unravel's options failed with an unexplained code | `0x8A150002` (invalid arguments) had no message | It now says to update App Installer | `ErrorCodesFollowMicrosoftsTable` |

## 5. The recurring "You'll need a new app to open this ms-gamingoverlay link"

**What was found on this PC:** Xbox Game Bar (`Microsoft.XboxGamingOverlay`) is not installed, nothing handles
`ms-gamingoverlay:`, and Game DVR is still enabled. Windows opens that link for Win+G, a controller's Xbox button
and some games. With no handler, it shows the prompt. The AppX deployment log (back to 28 Oct, before
Unravel's first run on this PC) has no removal of Game Bar. Unravel's code has never referenced Game Bar or
its links. So Unravel did not cause this, but Unravel *could* have caused it for someone else, because it would
remove such an app without warning.

**Fixes:**
- *Windows apps* now checks, with Windows' own `Launcher.QueryUriSupportAsync`, whether the link has a handler.
  When it doesn't, the page explains the prompt and offers **Get Xbox Game Bar** (its Microsoft Store page,
  product 9NZKPSTSNW4P, confirmed with `winget show`) and **Open Windows Settings** (`ms-settings:gaming-gamebar`).
  Nothing is installed or changed without the user. No registry values are touched.
- Uninstalling a Windows app that handles `ms-*` links now warns that Windows may show this prompt afterwards.

Tests: `SystemLinkHealthTests` (4, including a live query of a link Windows does handle), `ProtocolsAreReadFromTheManifest`.

## 6. Windows 10 compatibility

- All 57 icon glyphs exist in *Segoe MDL2 Assets*, the icon font Windows 10 has (checked against the font's character map).
  Fonts fall back from Segoe UI Variable / Segoe Fluent Icons automatically.
- APIs used are available from Windows 10 2004 (build 19041), the minimum Unravel declares. `Launcher.QueryUriSupportAsync` dates from build 10240.
- Older WinGet versions are handled (see above).
- **Not tested on a real Windows 10 installation:** only Windows 11 was available. This remains a known limitation.

## Sources

- [msiexec command reference (Microsoft Learn)](https://learn.microsoft.com/windows-server/administration/windows-commands/msiexec)
- [MsiExec.exe and InstMsi.exe error messages (Microsoft Learn)](https://learn.microsoft.com/windows/win32/msi/error-codes)
- [Windows Installer properties for the Uninstall registry key (Microsoft Learn)](https://learn.microsoft.com/windows/win32/msi/uninstall-registry-key)
- [WinGet return codes (winget-cli repository)](https://github.com/microsoft/winget-cli/blob/master/doc/windows/package-manager/winget/returnCodes.md)
- [WinGet uninstall flow source (winget-cli repository)](https://github.com/microsoft/winget-cli/blob/master/src/AppInstallerCLICore/Workflows/UninstallFlow.cpp)
- [`winget list` (Microsoft Learn)](https://learn.microsoft.com/windows/package-manager/winget/list)
- [How can I parse `winget list` output without trimmed IDs (winget-cli discussion #4090)](https://github.com/microsoft/winget-cli/discussions/4090)
- [Launcher.QueryUriSupportAsync (Microsoft Learn)](https://learn.microsoft.com/uwp/api/windows.system.launcher.queryurisupportasync)
- [Launch Windows Settings: ms-settings URIs (Microsoft Learn)](https://learn.microsoft.com/windows/apps/develop/launch/launch-settings-app)
