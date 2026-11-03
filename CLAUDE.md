# CLAUDE.md

## Project

**Vault Manager** is a Windows desktop app by Tech Tea Studio for managing Obsidian across many vaults from outside Obsidian: plugins, versions, settings, profiles and new vaults. Repository name: `ObsidianVaultManager`. The release branch is `product`.

## Guardrails

These protect a user's notes and settings. Each one comes from a real failure in v0.2.0.1 or a fact checked against Obsidian.

- **Never rewrite a file you could not read.** `ReadResult` separates missing from unreadable. A cut `community-plugins.json` read as empty used to wipe the vault's whole enabled list.
- **Restricted mode is not in `app.json`.** Obsidian 1.14.5 has no `restrictedMode` string at all. The state is `enable-plugin-<vault id>` in `%APPDATA%\obsidian\Local Storage\leveldb`. `Unknown` stays unknown.
- **`open: true` in `obsidian.json` alone proves nothing.** It also marks the vault that was open when the app last closed. A vault is open only when Obsidian also runs.
- **No writes into an open vault.** Lines wait, persist in SQLite and finish by themselves when the gate opens, always through the executor's gates and hash checks. Vault Extension Sync documents that Obsidian rewrites its config on close, which would undo the change.
- **The newest copy is the source, compared as numbers.** First-vault-wins used to spread old versions.
- **Plugin settings stay behind by default.** They hold keys and vault-specific paths. `SecretScanner` blocks live values; only key names may ever appear in text or logs.
- **Copies, not links.** Symlink tools report admin rights, cloud folders and shared `data.json` problems, and studio vaults live in git with their plugins.
- **Nothing outside a click reaches the network.** The owner's connection is expensive: sizes are shown first, and local copies come before downloads.
- **Imported profiles are untrusted.** `ProfileValidator` checks every name that becomes a path.
- **The database never lives in a vault and is never the truth about one.** The disk is. Snapshots are files; SQLite holds pointers.
- **Registering a vault waits while Obsidian runs.** What a running Obsidian does with an external write to `obsidian.json` has not been established. Do not change this without testing it.

## Layout

```
src/VaultManager.Core/   no UI references: the rules
  Foundation/            atomic writes, shared reads, ReadResult, ChangeFailure
  Obsidian/              registry, enabled list, Restricted mode, versions, secrets, scanner
  Changes/               ChangeItem records, ChangeExecutor (apply, snapshot, undo)
  Profiles/              Profile, validator, store, import/export, audit, VaultBuilder
  Sync/                  SettingsSync three-way compare
  Catalog/               the only door to the network
  Store/                 AppDatabase (SQLite)
  Demo/                  sealed world for --demo and screenshots
src/VaultManager.App/    Avalonia window: thin views, view models, Themes/Basalt.axaml, resx
tests/VaultManager.Tests xunit on real temp folders, headless Avalonia for the window
```

## Commands

```bash
dotnet build VaultManager.slnx
```

```bash
dotnet test tests/VaultManager.Tests/VaultManager.Tests.csproj
```

```bash
dotnet run --project src/VaultManager.App/VaultManager.App.csproj -f net10.0 -- --demo
```

Write the state gallery to a folder, to look at a visual change:

```bash
VM_SCREENSHOTS=/d/some/folder dotnet test tests/VaultManager.Tests/VaultManager.Tests.csproj
```

`--demo` is sealed: everything lives in a temp folder and it never reaches the real vaults or the network. Do not start the app on the owner's desktop to check something; use the headless gallery.

## Rules

- Rules go in Core, test first. Views stay thin; a decision in a view is a bug.
- Tests use real temp folders and never touch the real `%APPDATA%` or the network.
- Every string a person reads is in `Strings.resx` and `Strings.ru.resx`. Nothing decides by the words of a string.
- All colours and fonts live in `Themes/Basalt.axaml`. `DESIGN.md` explains why, read it before a visual change.
- Plain hyphens only: no em or en dashes anywhere. No `---` lines in Markdown body text.
- The window keeps its zones; content scrolls inside them.

## Version and commits

The version lives in `VERSION` and `Directory.Build.props` feeds every assembly. Every part is one digit, carried like an odometer: `0.6.9.9` then `0.7.0.0`. `DemoAndVersionTests` guard the file.

Commit format: `v<VERSION> <Short description>`, one line, at most 72 characters, version bumped first. Agents do not push and do not open pull requests.

**A push to `product` is a release.** `release.yml` creates the GitHub release and its lightweight tag `v<VERSION>` itself, on the pushed commit, with notes from the matching `CHANGELOG.md` section. So every commit that bumps `VERSION` needs a `## <VERSION>` changelog section, or the release stops; `ReleaseTests` check both, and run `.github/scripts/release-info.ps1`. Never push a tag by hand. A push without a version bump releases nothing.

Vault notes go through the `obsidian-worker` agent: `docs/obsidianVault/VaultManagerDocs`.
