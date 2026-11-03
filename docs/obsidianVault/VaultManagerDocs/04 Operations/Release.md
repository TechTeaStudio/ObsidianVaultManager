---
type: runbook
status: active
priority: high
tags: [runbook, release, ci]
aliases: []
summary: "How a push to product publishes the self-contained Windows exe: build, test, smoke test, then a GitHub release with its own tag."
related:
  - "[[Demo Mode]]"
  - "[[Release on push to product]]"
  - "[[v0.6.0.0 - Rebuild into Vault Manager]]"
created: 2026-11-02
updated: 2026-11-03
---

# Release

## Purpose

Publish Vault Manager as one self-contained Windows x64 exe on the GitHub Releases page of `TechTeaStudio/ObsidianVaultManager`. The release branch is `product`, and a push to `product` is the release. The reason is in [[Release on push to product]].

## Preconditions

- `VERSION` holds the version, one digit per part. `Directory.Build.props` reads it and feeds every assembly.
- Every part of a version is one digit, carried like an odometer: `0.6.9.9` then `0.7.0.0`. `DemoAndVersionTests` guard the file.
- `CHANGELOG.md` has a `## <VERSION>` section at the top, with what changed. `ReleaseTests` check it.
- Agents commit only. They do not push, and that includes tags. Pushing is the owner's step.

## Steps

1. Bump `VERSION`.
2. Add a `## <VERSION>` section at the top of `CHANGELOG.md`.
3. Commit both as `v<VERSION> <Short description>`: one line, at most 72 characters.
4. A pull request to `product` runs `ci.yml`. Fix what it reports before merging.
5. The owner pushes to `product`, or merges the pull request. That push runs `release.yml`, which releases the version. No tag is pushed by hand.

## What the workflows do

### ci.yml

Runs on pull requests to `product`, and by hand. A push to `product` does not run it, because `release.yml` runs the same build and tests on that push.

- **Test** on `windows-latest` with .NET 10. It includes `ReleaseTests`, so a pull request whose `VERSION` has no changelog section fails here.
- **Build** of `VaultManager.App` on `ubuntu-latest` for each of net6.0, net8.0, net9.0 and net10.0. Each job also installs SDK 10, because the sources use C# 12.

### release.yml

Runs on every push to `product`, and by hand from the Actions tab. It has no tag trigger and no inputs. In order:

1. **Build** `VaultManager.App` for net6.0, net8.0, net9.0 and net10.0 on `ubuntu-latest`. This job does not read the changelog, so it runs even when the section is missing.
2. **Read version and notes** on `windows-latest`, after all four builds pass. `.github/scripts/release-info.ps1` reads `VERSION`, requires four single-digit parts and copies the body of the matching `## <VERSION>` section of `CHANGELOG.md` into `release-notes.md`. A missing or empty section stops the job here, so nothing below runs.
3. **Check whether this version is already released.** `gh release view v<VERSION>` decides it. If a release exists, the publish, smoke test, naming and release steps are skipped, and the push releases nothing.
4. **Test:** `dotnet test tests/VaultManager.Tests/VaultManager.Tests.csproj -c Release`. It runs on every push, released or not.
5. **Publish** net10.0, win-x64, self-contained, single file, compressed, with native libraries inside, into `publish/`.
6. **Smoke test the exe:** start `publish/VaultManager.exe --smoke` and fail on a non-zero exit code. See the next section for what it checks.
7. **Name the exe and write its checksum:** rename it to `VaultManager-v<VERSION>-win-x64.exe` and write a `.sha256` file beside it, holding the lowercase SHA-256 hash, two spaces and the file name.
8. **Create GitHub Release:** `gh release create v<VERSION>` with the exe and the `.sha256`, the notes file as the body, `--target` set to the pushed commit, and `--latest`. The release makes the tag: a lightweight tag `v<VERSION>` on the pushed commit.

### The --smoke run

A published build has no window to look at in a check, so `Program.Smoke` in `src/VaultManager.App/Program.cs` proves the parts a single file can lose. It opens no window and touches nothing of the user's. It works in `vault-manager-smoke-<guid>` under the system temp folder and removes it afterwards. `--smoke` wins if `--demo` is given too.

| Exit code | Meaning |
| :-- | :-- |
| 0 | All checks passed |
| 2 | A value written to a temp SQLite database did not read back |
| 3 | The English or Russian text of one key is missing, or both are the same, so the Russian satellite is not inside |
| 4 | After Avalonia setup, `Font.Label` is not found or has no glyph for the Cyrillic letter U+0416 |

Exceptions are not caught, so a native library that fails to load ends the process with a non-zero code too.

## Verification

- The Release page has `VaultManager-v<VERSION>-win-x64.exe` and its `.sha256`, and the tag `v<VERSION>` sits on the pushed commit.
- The SHA-256 of the downloaded exe matches the hash in the `.sha256` file.
- The exe starts with no .NET installed, and `--demo` runs. See [[Demo Mode]].

## If It Fails

- **The job stops at Read version and notes:** `CHANGELOG.md` has no `## <VERSION>` section, the section is empty, or `VERSION` is not four single-digit parts. Nothing was published and no tag was made. Fix the file, not the check, and push again.
- **Tests, publish or smoke test fail:** nothing is published and no tag is made, because the release is created last. Fix the cause and push again with the same `VERSION`. The retry releases, because the check looks for a release, not for a new number.
- **The log says the version is already released:** the push ran the checks and released nothing. To release again, bump `VERSION` and add its changelog section.

## Worth checking

- A tag `v<VERSION>` that already exists stays where it is. `gh release create` uses the existing tag and does not move it, so the release would point at that older commit. Do not push tags by hand.
- `workflow_dispatch` runs `release.yml` from whichever branch is picked in the Actions tab, and that run takes that branch's `VERSION` and commit. Start it on `product` only.
- Only one run waits in the `release` group, so a newer push can replace a queued one before it starts. A replaced push never runs, and its version is not released unless a later push carries it.

## Links

- [[Release on push to product]]
- [[Demo Mode]]
- [[v0.6.0.0 - Rebuild into Vault Manager]]
