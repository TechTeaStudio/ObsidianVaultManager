# Changelog

## 0.6.0.2

### Added

- Releases from product: every push to `product` with a new `VERSION` tests the app, builds the self-contained exe, and publishes it as a GitHub release with notes from this changelog and a SHA-256 checksum. A push that keeps the same version runs the checks and releases nothing.

### Changed

- Pull requests to `product` are checked by `ci.yml`; a push to `product` is checked once, inside the release workflow.

## 0.6.0.1

### Changed

- New app icon: a basalt tile with an obsidian shard and one sulfur facet, drawn per size so it stays legible in the taskbar at 16 px. It replaces the folder icon.
- README rewritten for the one-click window, with fresh screenshots from the demo.
- The repository moved to `TechTeaStudio/ObsidianVaultManager`; links follow it.

## 0.6.0.0

The app is rebuilt from `ObsidianScanner` into Vault Manager, split into a tested core and a new window.

### Fixed

- Importing settings for an installed but disabled plugin crashed with `IOException`: it copied `data.json` onto itself.
- A damaged `community-plugins.json` read as empty, and the next write wiped the vault's whole enabled list. It is now refused and left untouched.
- The first vault in the list was the copy source, so an old plugin version could spread to other vaults. The newest copy is now the source, compared as numbers: 2.15.2 is newer than 2.8.3.
- The plugin list showed the version from the first vault it found, which hid differences between vaults.
- Restricted mode was read from `app.json`, where Obsidian does not keep it. It now comes from Obsidian's browser storage.
- A vault on an unplugged drive vanished from the list without a word. It now stays, marked offline, and changes wait for it.
- An unreadable vault list looked the same as having no vaults.
- Writes went straight into open vaults. They now wait until the vault is closed.
- Plain writes could leave half a file behind on a crash or a dropped drive. All writes are now atomic.
- Every action rebuilt the window state, losing search text and selection.

### Added

- One-line list of what needs attention with an Update all button, a table of all plugins, and a page for each vault.
- Changes run on click. A copy is saved first, Undo survives a restart, and each line reports its own result.
- Changes for an open vault or an unplugged drive wait, finish by themselves when they can, and survive a restart.
- Profiles as plain files with import, export and an editor, a built-in Tech Tea Studio profile, and a check of each vault against a profile.
- New vault from a profile, including its place in Obsidian's vault list.
- Settings sync between vaults, by category, with a three-way compare. Files holding a live key are blocked.
- Online check and install from the official catalog, on a click only, with the size shown first.
- Warning when the same plugin version has different files in different vaults, and a mark for plugins not in the catalog.
- SQLite database for the journal, undo, sync history and decisions, kept outside every vault.
- Basalt theme, Russian and English, `--demo` mode, state gallery, 431 tests.

### Changed

- Vault search matches anywhere in a name, instead of only at the start of a word.
- The app is one self-contained exe named after Vault Manager.

## 0.2.0.1

Parallel SDK install via a TFM matrix on Ubuntu.

## 0.2.0.0

Multi-target net6, net8, net9 and net10. Release workflow publishes a self-contained exe.
