---
type: index
status: active
priority: medium
tags: [operations]
aliases: []
summary: "Folder note for runbooks: release and the --demo mode."
related:
  - "[[Vault Map]]"
created: 2026-11-02
updated: 2026-11-03
---

# 04 Operations

How to publish Vault Manager and how to try it safely.

## Notes

```dataview
TABLE WITHOUT ID file.link AS "Note", summary AS "About", status AS "Status", updated AS "Updated"
FROM "04 Operations"
WHERE file.name != this.file.name
SORT updated DESC
```

%% Begin Waypoint %%
%% End Waypoint %%

## Key documents

- [[Release]] - versioning, CI, the `--smoke` check, and how a push to `product` publishes the Windows exe and the Linux file
- [[Demo Mode]] - the sealed world behind `--demo`, its temp folder, and the state gallery

## Status

- Both runbooks are written from the workflows, the docs and the code.
- Local git tags on 2026-11-03: only `v0.2.0.1`. From 0.6.0.2 on, `release.yml` makes each release's tag, so none is pushed by hand.
