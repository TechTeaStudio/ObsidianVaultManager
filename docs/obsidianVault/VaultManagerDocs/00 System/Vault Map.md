---
type: index
status: active
priority: high
tags: [vault, map, system]
aliases: []
summary: "Top-level orientation for the Vault Manager vault: folders, conventions and where to start."
related:
  - "[[Vault Manager Architecture]]"
  - "[[02 Services]]"
  - "[[04 Operations]]"
  - "[[05 Summaries]]"
  - "[[06 Decisions]]"
created: 2026-11-02
updated: 2026-11-03
---

# Vault Map

Where things live in the Vault Manager vault. Skim this before creating a note, and prefer updating an existing one.

Vault Manager is a desktop app for Windows and Linux by Tech Tea Studio for managing Obsidian across many vaults from outside Obsidian: plugins, versions, settings, profiles and new vaults. The repository is `ObsidianVaultManager` and the release branch is `product`.

## What you need and where to go

| What you need | Where to go |
| :-- | :-- |
| How the parts fit, how a change runs, who re-runs a waiting line, what is stored where | [[Vault Manager Architecture]] |
| One area of the core | [[Obsidian]], [[Changes]], [[Profiles]], [[Sync]], [[Catalog]], [[Store]] |
| Why something is the way it is | [[06 Decisions]] |
| Publishing a build | [[Release]] |
| Trying the app or looking at a visual change | [[Demo Mode]] |
| What shipped | [[05 Summaries]] |

## Folder layout

| Folder | Purpose |
| :-- | :-- |
| `00 System` | Vault Map and templates |
| `01 Architecture` | How Core and App split, the change flow, storage |
| `02 Services` | One note per area of `src/VaultManager.Core` |
| `04 Operations` | Release with `--smoke`, and the `--demo` mode |
| `05 Summaries` | Per-version change summaries |
| `06 Decisions` | ADR-style records, one decision per note |
| `07 Meetings` | Meeting notes |
| `Inbox` | Unsorted; move out promptly |
| `Legacy` | Pre-vault documents, kept as they were |

`03 Plugins` is intentionally absent. Vault Manager manages Obsidian plugins in other vaults, but it has no plugin system of its own.

## Sources

The notes summarise the repo's developer docs and the code. Read the source before relying on a detail.

- `CLAUDE.md` - guardrails, layout, commands, rules
- `DESIGN.md` - the look and the zones of the window
- `CHANGELOG.md` - what changed per version
- `README.md` - user-facing behaviour and where things live
- `src/VaultManager.Core` - the rules
- `src/VaultManager.App` - the window. It has no note of its own. See the Status section of [[02 Services]] for where its behaviour is described

## Conventions

### Frontmatter

Every note uses these fields and no others:

```yaml
type: project | note | decision | meeting | person | task | concept | index | summary | runbook
status: active | draft | blocked | archived | deprecated
priority: low | medium | high | critical
tags: []
aliases: []
summary: ""
related: []
created: YYYY-MM-DD
updated: YYYY-MM-DD
```

### Writing

- English, like the repo's other developer docs.
- Plain hyphens only: no em or en dashes.
- No horizontal-rule lines in body text. Use headings.
- Normal notes stay under 1000 words.

### Folder notes

Every folder has a note with the same name. It holds a short introduction, a live Dataview table, a Waypoint block and key documents.

### Waypoint

Navigation between `%% Begin Waypoint %%` and `%% End Waypoint %%` is generated. Never edit it by hand.

### Templates

They live in `00 System/Templates/` and run through Templater. Replace every `<% tp.* %>` placeholder in a finished note.

### Vault notes

Vault notes go through the `obsidian-worker` agent.

## Plugins active

Dataview, Folder notes, Metadata Menu, Obsidian Git, Linter, Omnisearch, Templater, Waypoint, Automatic Table of Contents.
