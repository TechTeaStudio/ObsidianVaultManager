---
type: decision
status: active
priority: high
tags: [adr, storage, profiles]
aliases: []
summary: "Profiles are plain files; the app's own memory is one SQLite database outside every vault, which is never the truth about a vault."
related:
  - "[[Profiles]]"
  - "[[Store]]"
  - "[[Vault Manager Architecture]]"
created: 2026-11-02
updated: 2026-11-03
---

# Profiles as files, everything else in SQLite

## Context

The app keeps two kinds of data of its own. Profiles are something a person owns and shares. The journal, undo records, sync bases, decisions and catalog cache are working memory.

## Decision

- **A profile is a plain file**, `*.vaultprofile.json` in the `profiles` folder of the app's data folder: `%LOCALAPPDATA%\TechTeaStudio\VaultManager\profiles` on Windows, `~/.local/share/TechTeaStudio/VaultManager/profiles` on Linux. The reason in the code is that it can live in git, be sent to someone or be edited by hand.
- **Everything else the app remembers is in SQLite**, `vault-manager.db`, in the same folder.
- **The database never lives in a vault and is never the truth about one.** The disk is.

One exception to "everything else": snapshots for Undo are files, and SQLite holds pointers to them (`CLAUDE.md`).

## Alternatives Considered

None are recorded. `CLAUDE.md` and `DESIGN.md` do not say why SQLite was picked over plain files for the working memory. What the code does rely on is the transaction: an operation and all its undo records land together or not at all.

## Consequences

- Nothing of the app's own is kept inside a vault.
- A file from elsewhere is untrusted, so `ProfileValidator` checks every name that becomes a path, and `ProfileFile` keeps `TypeNameHandling` off.
- A hand-edited broken profile is skipped and shows up by its absence.
- Facts about a vault are read from disk on each scan. The database stores decisions and history, not state.
- A newer database than the build is refused, so Undo history is not guessed at.
- The built-in profile repeats the global studio rules by hand; only its plugin list is pinned by a test.

## Links

- [[Profiles]]
- [[Store]]
- [[Vault Manager Architecture]]
