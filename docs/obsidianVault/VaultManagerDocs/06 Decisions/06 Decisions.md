---
type: index
status: active
priority: medium
tags: [adr, decisions]
aliases: []
summary: "Folder note for decision records."
related:
  - "[[Vault Map]]"
  - "[[Vault Manager Architecture]]"
created: 2026-11-02
updated: 2026-11-03
---

# 06 Decisions

ADR-style records, one decision per note. Past decisions stay after they are superseded: a new note links back. The reasons come from `CLAUDE.md` and `DESIGN.md`, and alternatives are listed only where those files name them.

## Notes

```dataview
TABLE WITHOUT ID file.link AS "Note", summary AS "About", status AS "Status", updated AS "Updated"
FROM "06 Decisions"
WHERE file.name != this.file.name
SORT updated DESC
```

%% Begin Waypoint %%
%% End Waypoint %%

## Key documents

- [[Copies not symlinks]] - plugins reach a vault as real files
- [[Profiles as files, everything else in SQLite]] - what is shareable and what is the app's own memory
- [[Restricted mode read from browser storage]] - not from `app.json`
- [[Registration waits while Obsidian runs]] - with what is still unverified
- [[Basalt palette]] - volcanic rock and one sulfur accent
- [[Run on click instead of plan then Apply]] - a copy saved first and Undo in view
- [[Release on push to product]] - a push to `product` makes the GitHub release and its tag, so no tag is pushed by hand

## Status

- Active decisions: 7
- Open questions: real-Obsidian behaviour in [[Registration waits while Obsidian runs]], compacted LevelDB in [[Restricted mode read from browser storage]]
