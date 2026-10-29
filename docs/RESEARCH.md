# Research notes and method

## Target

`C:\Program Files\VS Revo Group\Revo Uninstaller\RevoUnin.exe`: Revo Uninstaller **Free 2.6.8.0**
(VS Revo Group Ltd). The installation also contains `RevoUninHelper.exe`, `RevoSrp.exe` (restore point
helper), a kernel driver `RevoProcessDetector.sys`, 47 language files and *Revo Uninstaller Help.pdf*.

## Authorization and its effect on the method

The product's `License.txt` states that users are **not allowed** to "modify, reverse engineer,
decompile, disassemble or otherwise attempt to reconstruct or discover the source code, or any parts
of it from the binaries". The task required analysing only what we are authorized to inspect, so:

- No disassembly, decompilation, binary diffing or debugging of Revo's executables or driver was
  done. Static binary analysis with REA (its Ghidra/IDA providers, PE inspection tools, etc.) was
  therefore not applied to the binaries.
- The REA skill (github.com/morluto/rea) was obtained and its workflow guidance was followed where it
  applies to an authorized black-box investigation: turn the request into a checklist of questions,
  gather the smallest useful evidence first, keep a ledger of observations versus inferences, and mark
  every claim answered or unresolved. REA's MCP server was not installed, because none of its
  analysis tools could be used on this target within the license.
- Analysis was limited to sources a licensed user may read or observe:
  1. **Bundled user guide** (*Revo Uninstaller Help.pdf*, 34 pages): the primary feature specification.
  2. **Official product page** (revouninstaller.com/products/revo-uninstaller-free/): Free vs Pro scope.
  3. **Running UI**: window screenshots of the original (only its own window was captured), Windows UI
     Automation (which exposes little, because the original uses custom-drawn Prof-UIS controls), and the
     status-bar counts.
  4. **Settings the original writes** under `HKCU\Software\VS Revo Group\Revo Uninstaller`: option
     names and defaults, e.g. `NTUninstall Mode = 2` (Moderate default), `DelToBin = 1`,
     `Create System Restore Pont = 1`, `Use Reg Install Date = 0`.
  5. **The English UI string file** (`lang\english.ini`, a plain-text configuration file): read only to
     enumerate command names such as *Remove Entry*, *Remove All Not Running* and the four uninstall
     modes. No text was copied; all Unravel wording is original.
- The original could not be driven by automation: it runs elevated, and Windows' UI Privilege
  Isolation blocks input from the standard-rights session used here.

## Questions ledger

| Question | Evidence | Status |
|---|---|---|
| Which modules exist in the Free edition? | Guide §1–§7; product page | Answered |
| How does the uninstall wizard flow work, and what do the modes do? | Guide §3, §7.2; INI strings for wizard steps; settings registry | Answered (search locations per mode are our own design; the original's exact locations are not observable) |
| How are leftovers presented and deleted? | Guide §3 (bold = created, check-branch, only bold deleted, red excluded) | Answered |
| How are programs enumerated, counted and dated? | Uninstall registry keys; the original's count (114) and dates match ours | Answered and validated |
| How are Windows apps named and filtered? | The original's rows compared with package data | Naming answered; filter partially (59 vs 68) |
| How does the AutoRun manager disable entries? | Not observable without binary analysis | Unresolved; replaced with the documented Windows mechanism (StartupApproved) |
| Which tools are in Windows Tools? | Guide §6.3 table (18 tools) | Answered; modern equivalents where tools were retired |

## Design decisions that differ deliberately

- **Standard rights by default.** The original requires elevation at launch. Unravel starts as the
  current user and offers *Restart as administrator*, because most features (per-user programs, all
  cleaners, startup items for the current user, uninstallers that elevate themselves) work without it.
- **Registry backups** for every deletion, and nothing is deleted if the backup fails.
- **StartupApproved** for enabling and disabling startup items, so the state agrees with Task Manager
  and Windows Settings.
- **Opt-in for destructive defaults** in the cleaners: cookies, sessions, form data, clipboard and
  the Recycle Bin start unchecked.
