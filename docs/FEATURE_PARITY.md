# Feature parity: Revo Uninstaller Free 2.6.8 → Unravel

**Source of the feature list.** The original's bundled *Revo Uninstaller Help.pdf* (User's Guide,
updated 13 September 2024), the product page at revouninstaller.com, the original's settings as
stored under `HKCU\Software\VS Revo Group\Revo Uninstaller`, the UI labels in its `lang\english.ini`
(used only to enumerate commands, not copied), and screenshots of the running original.

**Status key**

- ✅ **Implemented**: present, with the cited evidence.
- 🔁 **Adapted**: same purpose, deliberately different mechanism (the reason is given).
- ⚠️ **Partial**: works, with a stated limitation.
- ❌ **Not implemented**: with the reason.

**Evidence key**

- *U:* automated unit or integration tests (class names in `tests/Unravel.Tests`).
- *E2E:* UI-automation run against the real app (`tests/e2e`).
- *V:* visual check (screenshot).
- *O:* compared against the original or against Windows itself.

## 1. Uninstaller (Programs)

| Original feature (guide §3) | Unravel | Status | Evidence |
|---|---|---|---|
| List installed programs (HKLM 64/32-bit, HKCU) | `ProgramInventory` | ✅ | U: ProgramInventoryTests (17); **O: 114 programs in Unravel = "Installations: 114" in Revo**; O: every name in Windows *Programs and Features* is present (ProgramInventoryLiveTests) |
| Show system components (option) | Settings › Uninstaller | ✅ | U: OptionsRevealComponentsAndUpdates |
| Show system updates (option; XP-only in the original) | Settings › Uninstaller | ✅ | U: DetectsUpdates |
| Columns: Name, Size, Version, Type, Install Date, Company, Comment, Website (+ Uninstall string, Registry key, Installer from the original's list) | DataGrid columns + column chooser | ✅ | V, U: ReadsFieldsAndDetectsInstaller |
| Views: Icons / List / Details | Tiles / List / Details | ✅ | V |
| Tooltip and description panel with program details | Row tooltip + details pane with links | 🔁 | V (pane replaces the dockable description panel) |
| Search by name or company, with a found count | Search box + "Search by" (Name / Publisher / Any field) | ✅ | V |
| Uninstall (toolbar, double-click, context menu) | Same, plus the Del key | ✅ | **E2E: demo program uninstalled through the UI** |
| Forced MSI Uninstall (when the uninstall command is `/I`) | Context menu, shown only when applicable | ✅ | U: MsiProgramSupportsForcedUninstallModifyRepair |
| Change / Repair when the installer supports it | Toolbar + context menu | ✅ | U: same test (command lines) |
| Refresh | Toolbar, F5 | ✅ | V |
| Search at Google for name / publisher | Context menu | ✅ | code path `Shell.SearchGoogle` |
| Open About / Help / Update link | Context menu + details pane | ✅ | U: ReadsFieldsAndDetectsInstaller (URL read) |
| Install Location | Context menu + details pane | ✅ | V |
| Open Registry Key (regedit at the key) | Context menu (sets regedit's LastKey, then starts regedit) | ✅ | manual |
| Export list to HTML / TXT, choosing details | Export button; uses the visible columns | ✅ | U: ExportsTextAndEscapedHtml |
| Remove Entry (toolbar string in the original) | Context menu, with a `.reg` backup | ✅ | U: RemoveEntryBacksUpThenDeletes (backup restores it) |
| "Use program's uninstall data for the Install Date" | Option; default uses the key timestamp, like the original | ✅ | U: WithoutInstallDateOptionUsesKeyTimestamp; O: the original's dates equal key timestamps |
| Fast loading mode (skip icons and size calculation) | Option | ✅ | code path |
| Indirect display names (`DisplayName_Localized`) | Resolved with SHLoadIndirectString | ✅ | U: ResolvesLocalizedIndirectDisplayName (found by the Windows oracle test) |

## 2. Uninstall wizard and leftover scan (guide §3, §7.2)

| Original feature | Unravel | Status | Evidence |
|---|---|---|---|
| Analyse application data before uninstall | `ScanTarget.FromProgram` snapshot (folder, executables, MSI code, key) | ✅ | E2E log line "Analyzed …" |
| Create System Restore point (option) | WMI `SystemRestore.CreateRestorePoint` | ⚠️ | Needs administrator rights; skipped with a message otherwise (E2E). Windows' 24-hour throttle can silently skip creation. |
| Run the built-in uninstaller; wait for it | Starts it, then waits for the whole process tree (handles self-relaunching uninstallers), with "The uninstaller has finished" to stop waiting | ✅ | E2E; code `ProcessUtil.WaitForProcessTreeAsync` |
| Invalid uninstall command message | Logged in the wizard; scanning is still possible | ✅ | U: StartingMissingUninstallerReportsInvalidCommand |
| Forcibly stop running executables (option) | Lists the processes in the install folder and asks first | ✅ | code path |
| "Only run the built-in uninstaller" (option) | Wizard ends after the uninstaller, with Back to scan anyway | ✅ | code path |
| Modes: Safe / Moderate / Advanced | Same three modes; default from Settings (Moderate) | ✅ | U: SafeModeFindsProgramIdentityOnly, ModerateAddsSharedLocations, AdvancedAddsDeepAndComFindings; **E2E Safe = 6/9 parts removed, Moderate = 9/9, Advanced = 9/9** |
| Registry leftovers tree; bold = created; check a parent to check its children; only checked bold items deleted | Same semantics | ✅ | U: RegistryTreeShowsBoldLeavesUnderPlainParents, UncheckedItemsAreKept; E2E |
| Context menu: Expand/Collapse all, Expand/Collapse selected, Uninstall (delete) selected key, Mark as Created, Add to Exclude List, Open Registry Key, Export All (HTML) | All present | ✅ | U: MarkAsCreatedMakesPlainNodeDeletable, ExportsHtml |
| Excluded items shown in red, never deleted | Same | ✅ | U: ExcludeRulesMarkItemsAndProtectThem; V (e2e_2_registry.png) |
| Select All / Delete / Next | Same, with an "N of M selected" counter | ✅ | E2E |
| File and folder leftovers list with count and size | Same, plus "why it was found" | ✅ | E2E, V |
| Files context menu: Open, Properties, Open Containing Folder, Export | Same, plus check/uncheck selected and add folder to exclude list | ✅ | code paths |
| Delete to Recycle Bin (option, default on) | Same | ⚠️ | Recycle path not covered by automated tests (to avoid filling the tester's Recycle Bin); permanent path covered by tests and E2E |
| Remaining files deleted at next restart | MoveFileEx delay-until-reboot when administrator | ✅ | code path |
| Shared-install warning ("X is installed in the same location as Y") | Shared folders are context-only, with a note naming the other program | ✅ | U: FolderSharedWithAnotherProgramIsNotMarkedCreated |
| Safety algorithms that withhold risky items | Protected roots and ancestors, generic-name filter, other-program name clash check | ✅ | U: NeverOffersUnrelatedOrProtectedItems, ScannerRefusesProtectedRealSystemLocations, NameSharedWithAnotherInstalledProgramIsNotMarkedCreated |
| Registry exclude list: wildcards `*` `?`, "include subkeys", enable checkbox, Add/Remove/Reset defaults | Settings › Uninstaller exclusions | ✅ | U: ExcludeRulesMarkItemsAndProtectThem; WildcardTests (test cases taken from the wildcard examples in the guide) |
| Folder exclude list | Same | ✅ | U: same test |
| Check all leftovers by default (option) | Same | ✅ | U: ExcludeRulesMarkItemsAndProtectThem (checkByDefault) |
| Open the wizard maximized (option) | Same | ✅ | code path |
| Registry backup before deletion | Always, unless turned off; restore from Settings | ✅ (beyond the original Free) | U: DeletesOnlyCheckedCreatedItemsWithBackup, BackupFailurePreventsDeletion |

## 3. Windows Apps (guide §4)

| Original feature | Unravel | Status | Evidence |
|---|---|---|---|
| List installed apps with Name, Size, Install Date, Version, Publisher, Description | `WindowsApps.List` (PackageManager) | ⚠️ | U: WindowsAppsTests (5). **Naming matches the original** (Start-menu name, else manifest name; e.g. "Calculator", "Microsoft Windows App Runtime DynamicDependency LifetimeManager", identical to the original's text). Count differs: the original showed 59, Unravel shows 68. Unravel additionally lists Camera, Copilot, Microsoft Edge, AMD Radeon Software and App Installer; the original's filter rule for these could not be determined from black-box observation. |
| Uninstall with the same leftover wizard | `UninstallJob.ForWindowsApp` → `RemovePackageAsync` | ⚠️ | Leftover identity tested (U: ScanTargetUsesPackageIdentity…). Actual package removal was **not executed** in testing to avoid deleting the tester's apps. |
| Run | `shell:AppsFolder\<AUMID>` | ✅ | code path |
| Search at Google, Install Location, Open in Microsoft Store, Open Registry Key | Context menu | ✅ | code paths |
| Full registry backup before app uninstall | Backs up the keys it deletes | 🔁 | A full-hive export takes minutes and gigabytes; targeted backups restore what was removed. |

## 4. Hunter mode (guide §5)

| Original feature | Unravel | Status | Evidence |
|---|---|---|---|
| Hide main window, show a target in the upper-right corner | Same (position remembered) | ✅ | E2E: main window hidden, Hunter window shown |
| Drag onto window / desktop icon / quick launch / tray, with a live tooltip | Windows: WindowFromPoint → process image. Desktop/taskbar: UI Automation label → shortcut target or running process. | ⚠️ | U: FindsWindowOfAnotherProcess, ResolvesShortcutByLabel. **E2E with real mouse input: drag onto a running program → menu → Stop starting with Windows / Uninstall (9/9 parts removed).** Tray icons resolve only when the label matches a program name or running process (best effort). |
| Menu: Uninstall, Stop Auto Starting, Kill, Kill and Delete, Open Containing Folder, Search Google (name / file / company), Properties, Cancel | All present | ✅ | U: MatchesMostSpecificInstallFolder (program matching); autorun matching via `HunterResolver.MatchAutorun` |
| Options: Open main window, Auto start with Windows, Always on top, Size (S/M/L), Transparency, Help, Exit | All present (also in Settings) | ✅ | code paths |

## 5. Tools (guide §6)

| Original feature | Unravel | Status | Evidence |
|---|---|---|---|
| **AutoRun Manager**: list with Name, Launch path, Description, Publisher, Status, Location | Startup manager | ✅ | U: AutorunTests (12); V |
| Enable/disable with a checkbox | On/off switch writing Windows' StartupApproved flags (Task Manager-compatible) | 🔁 | U: DisableAndEnableUseStartupApprovedFlags, ReadsExistingTaskManagerDisabledState; **E2E: UI switch wrote 0x03 then 0x02**; O: states match `StartupApproved` on the real machine |
| Run / Kill / Remove | Same | ✅ | U: RunAndKillAProcess, RemoveDeletesRunValueAndApprovalFlag |
| Add: All users / Current user; Registry Run / RunOnce / Startup folder | Add dialog | ✅ | U: AddsRunAndStartupFolderEntriesAndRejectsDuplicates, AddValidatesInput |
| Search Google (name / file / publisher / description), Open containing folder, Go to location, Open (uninstall) registry key, Copy launch path, Properties, Open Windows Services | Context menu | ✅ | code paths |
| Remove selected / all not valid / all not running / all disabled | Context menu | ✅ | code paths (each uses the tested `Remove`) |
| Services | Auto-start services, optionally including Microsoft's; disable/enable needs administrator rights | ⚠️ | Load is tested; changing service start type was not executed (not elevated) |
| Choose what to print | — | ❌ | Printing was not implemented; use Export in Programs or copy commands instead. |
| **Junk Files Cleaner**: drives, built-in + custom patterns, exclude patterns, Scan with Cancel (partial results), Delete, Recycle Bin option, ignore files accessed in the last 24 h, context menu (Open, Search, Open folder, Properties, check/uncheck all/selected) | Junk files page + Settings | ✅ | U: JunkScannerTests (8: patterns, 24 h rule, exclusions, junctions not followed, cancel returns partial results, locked files reported) |
| **Windows Tools** (18) | Windows tools page (18) | 🔁 | U: WindowsToolsTests (16 availability checks on this Windows). XP-only Network Diagnostics → the Settings troubleshooter page. "Security Center" → Windows Security. |
| **Browsers Cleaner** (IE, Firefox, Chrome, Opera) | Chrome, Edge, Brave, Vivaldi, Chromium, Opera, Opera GX, Firefox, IE/WinINet | ✅ | U: ChromiumProfileIsCleanedPerCategory, FirefoxHistoryKeepsBookmarkedPlaces, LockedBrowserDatabaseReportsInUse (synthetic profiles); live read-only Analyze runs on the real profiles. Real browser data was not deleted during testing. |
| Check/uncheck all items of a browser | "Select all" per browser | ✅ | V |
| **MS Office Cleaner**: Word, Excel, PowerPoint, Access 2000–2016, FrontPage 2000–2003 | Same, all versions including Microsoft 365 and per-account lists | ✅ | U: OfficeCleanerClearsAllVersionsAndUserLists |
| **Windows Cleaner**: the 16 items listed in guide §6.6 | Same 16 items | ✅ | U: WindowsCleanerTargetsRedirectedLocations; live read-only Analyze: 30,951 items / 5 GB found, no errors (V) |
| **Evidence Remover**: overwrite free space 3× on the chosen drive(s) | Wipe free space (1 or 3 passes, plus unused file-table records) | ⚠️ | U: FreeSpaceWiperTests (capped at 6 MB, passes counted, temporary files always removed, also after cancel). A full-drive wipe was not run (hours, fills the disk). |
| **Unrecoverable Delete**: Add files, Add folder, Delete forever, cancel, drag and drop | Secure delete page | ✅ | U: SecureDeleteTests (6: content verified random before deletion, folders recursive, read-only files, junction targets untouched, locked files reported, cancel) |

## 6. Options (guide §7)

| Original option | Unravel | Status |
|---|---|---|
| Language selection (40+ languages) | English only | ❌ Not implemented. Strings are in XAML and C#; a translation layer was out of scope. |
| Check for updates on startup | — | ❌ Unravel has no update server. |
| Skip information messages / Skip warning messages | Same | ✅ (irreversible actions such as Secure delete always ask) |
| View: toolbar text labels, small icons, reset panels/columns | Theme (system/light/dark), compact rows, details pane, reset columns | 🔁 The dockable toolbar layout was replaced by a fixed modern layout. |
| Revo Uninstaller Helper (background leftover notifications, update and promotion notices) | — | ❌ The background monitor was not implemented; promotions don't apply. |

## 7. Not in scope

These are listed on the original's product page as **Pro-only**, so they are not part of the Free
feature set that was reproduced: Forced Uninstall of already-removed programs, Quick/Multiple
Uninstall, Remove Browser Extensions, Real-Time installation monitoring and logs, the Multi-level
Backup Manager, and the portable USB edition.
