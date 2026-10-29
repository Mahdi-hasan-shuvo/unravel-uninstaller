# Security policy

Unravel deletes files and registry data and can end processes, so safety bugs are treated as security bugs.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Use GitHub's private reporting instead:
**Security → Report a vulnerability** on this repository.

Include the Unravel version (About & help page), your Windows build, whether Unravel ran as administrator,
and the steps to reproduce. You should get a reply within 7 days.

## What counts

- Unravel deleting, or offering pre-selected, something it should not: a system-critical file or key,
  data belonging to another program, or anything outside what the deletion preview showed.
- A way to bypass the blocked-item rules, the critical-process guard or the last-copy rule of the duplicate finder.
- Anything that lets another program or user run code through Unravel or escalate privileges.
- Backups, quarantine or logs exposing data to other users.

## Supported versions

Only the latest release receives fixes.
