---
type: decision
status: active
priority: high
tags: [adr, release, ci]
aliases: []
summary: "A push to product that brings a new VERSION creates the GitHub release and its tag v<VERSION>, so no tag is ever pushed by hand."
related:
  - "[[Release]]"
  - "[[06 Decisions]]"
created: 2026-11-03
updated: 2026-11-03
---

# Release on push to product

## Context

Before 0.6.0.2 the owner released by pushing a `v*` tag by hand. The tag started `release.yml`, and the job checked that the tag matched `VERSION`. The owner wanted the same flow as the studio's WodenPyrosisMod repo, where a push to `product` creates the tag and the release.

## Decision

A push to `product` releases. `release.yml` builds the four target frameworks, then tests and publishes each platform's file: `win-x64` as an exe, and `linux-x64`. It smoke-tests each file and creates the GitHub release. The release makes its own lightweight tag `v<VERSION>` on the pushed commit, and attaches both files, each with its `.sha256` checksum, and the notes from the matching `CHANGELOG.md` section.

A push whose `VERSION` already has a release runs the build and the tests, and releases nothing. A missing changelog section stops the release. `ci.yml` runs only for pull requests to `product`.

## Alternatives Considered

| Option | Why rejected |
| :-- | :-- |
| Push a `v*` tag by hand, as before 0.6.0.2 | The owner wanted the WodenPyrosisMod flow, where the push makes the tag and the release. Nothing needs a hand-made tag any more. |
| Keep `ci.yml` on push as well | The push would run the same checks twice, once in `ci.yml` and again in `release.yml`. |

## Consequences

- **Trade-off:** every push to `product` that brings a new `VERSION` publishes. A merge into `product` is a push too, so a version bump has to be ready before it reaches `product`.
- The changelog section is part of the release. A `VERSION` without one stops the release before tests and publish. `ReleaseTests` fail the pull request first, so the mistake shows before the merge.
- A push that keeps a released `VERSION` still runs the build and the tests, and stops before publishing. A broken `product` shows on that push even though nothing is released.
- A failed release leaves nothing behind, because the release and its tag come last. The retry is a push with the same `VERSION`, since the check looks for a release, not for a new number.
- A tag or release created with the workflow's `GITHUB_TOKEN` starts no other workflow. Nothing in the repository listens for the new tag or the published release. Anything that must react has to be a job in `release.yml`, or use another token.
- Two pushes in a row queue in one `release` group, so they do not race to create the same release. A newer push can still replace a queued one, see [[Release]].

## Links

- [[Release]]
- [[06 Decisions]]
