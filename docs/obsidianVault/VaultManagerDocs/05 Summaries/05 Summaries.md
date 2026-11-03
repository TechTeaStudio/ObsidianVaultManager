---
type: index
status: active
priority: medium
tags: [summaries, releases]
aliases: []
summary: "Folder note for per-version change summaries."
related:
  - "[[Vault Map]]"
created: 2026-11-02
updated: 2026-11-03
---

# 05 Summaries

One note per shipped version or self-contained feature. Names carry the version so they sort. `CHANGELOG.md` stays the source for what changed.

## Notes

```dataview
TABLE WITHOUT ID file.link AS "Note", summary AS "About", status AS "Status", updated AS "Updated"
FROM "05 Summaries"
WHERE file.name != this.file.name
SORT created ASC, file.name ASC
```

%% Begin Waypoint %%
%% End Waypoint %%

## Key documents

- [[v0.6.0.0 - Rebuild into Vault Manager]] - the rebuild from `ObsidianScanner`
- [[v0.6.0.3 - Linux support]] - Linux as a shipped target, and three fixes for Obsidian on Linux

## Status

- Versions without a note (0.2.0.0, 0.2.0.1, 0.6.0.1, 0.6.0.2) are in `CHANGELOG.md` only.
