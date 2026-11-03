---
type: decision
status: active
priority: high
tags: [adr, obsidian, restricted-mode]
aliases: []
summary: "Restricted mode is read from Obsidian's LevelDB browser storage, because app.json does not hold it. Checked against Obsidian 1.14.5."
related:
  - "[[Obsidian]]"
  - "[[v0.6.0.0 - Rebuild into Vault Manager]]"
created: 2026-11-02
updated: 2026-11-03
---

# Restricted mode read from browser storage

## Context

The app shows which vaults have community plugins turned off. It used to read this from `app.json`, where Obsidian does not keep it (fixed in 0.6.0.0).

`CLAUDE.md` records the facts checked against Obsidian: version 1.14.5 has no `restrictedMode` string at all, and the state is `enable-plugin-<vault id>` in `Local Storage\leveldb` under Obsidian's settings folder. Those facts name the Windows path, `%APPDATA%\obsidian`. On Linux the folder is the one of the install in use: `$XDG_CONFIG_HOME/obsidian` or `~/.config/obsidian` for the standard install, `~/.var/app/md.obsidian.Obsidian/config/obsidian` for Flatpak, and `~/snap/obsidian/current/.config/obsidian` for Snap. How the install is picked is in [[Obsidian]].

## Decision

`CommunityPluginsReader` reads the `.log` and `.ldb` files of that folder as raw bytes, oldest first so the newest write of a key wins, and matches the key with a regular expression. It uses no LevelDB library. The result per vault says whether community plugins are on: `On`, `Off` (Restricted mode), `NeverEnabled` or `Unknown`.

`Unknown` stays unknown: when the store is missing or cannot be read, the app does not guess. `NeverEnabled` means "no key seen", and the window words it as seeming to be off, not as a fact. See [[Obsidian]].

## Alternatives Considered

| Option | Why rejected |
| :-- | :-- |
| `app.json` | Obsidian does not keep the state there |

## Consequences

- The app only reads this state. No change line writes it, so turning Restricted mode off stays with Obsidian.
- The reader is best effort. The format belongs to Obsidian and was checked against one version.

## Unverified

- **Compacted files.** A compacted LevelDB file may compress a key so that it looks absent, and that gives `NeverEnabled`, which `Restricted_mode_comes_from_the_browser_store_and_app_json_does_not_matter` pins for an absent key. An older readable value could also win over a newer compressed one, and then `On` or `Off` would be stale with no hedge in the wording. This comes from reading the code. It has not been tried on a real compacted store.
- **Other Obsidian versions.** Only 1.14.5 is recorded as checked.
- **Linux.** The reader takes the same relative path, `Local Storage/leveldb`, under the install's folder on Linux. Nothing has been checked against Obsidian on Linux.

## Links

- [[Obsidian]]
- [[v0.6.0.0 - Rebuild into Vault Manager]]
