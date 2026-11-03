---
type: decision
status: active
priority: high
tags: [adr, linux, release, unverified]
aliases: []
summary: "Vault Manager ships a linux-x64 file beside win-x64, with invariant globalization, the X11 libraries it still loads, and the window manager's frame."
related:
  - "[[Obsidian]]"
  - "[[Release]]"
  - "[[Release on push to product]]"
  - "[[v0.6.0.3 - Linux support]]"
created: 2026-11-03
updated: 2026-11-03
---

# Linux support

## Context

Until 0.6.0.3 the release published one file, the Windows exe, and the tests ran on Windows only. Obsidian also runs on Linux, from AppImage, deb, Flatpak or Snap. Its settings then sit in other folders, its process has another name, and a Linux file system tells `Notes` from `notes`. The Windows assumptions sat in three places: the settings folder, the process name and the case of paths.

The app's own data goes to `~/.local/share/TechTeaStudio/VaultManager` on Linux, as `Host.cs` notes.

## Decision

Vault Manager ships a `linux-x64` file next to `win-x64`, built from the same code and tested on both systems. The platform is a parameter in `PlatformTests`, so the Linux rules are checked on any machine, and CI runs the full suite on Ubuntu as well as on Windows.

### Invariant globalization for linux builds

`linux-*` builds set `InvariantGlobalization` and `PredefinedCulturesOnly=false` in `VaultManager.App.csproj`.

Reason: an Ubuntu without ICU crashed at the first database call. Invariant mode needs no ICU. `PredefinedCulturesOnly` stays false, so the Russian strings still load by name. Windows builds are unchanged.

### libICE and libSM stay required

Reason: Avalonia 11.3.14 loads libICE and libSM even with session management off. This was checked on Ubuntu 26.04 under WSL. The README lists both, and a minimal system may need `sudo apt install libice6 libsm6`.

### The window manager draws the frame

On Linux `MainWindow` does not extend the client area into the decorations, so the window manager keeps its own frame and buttons. `MainView` hides the app's three window buttons there, and the title row holds only the name and the version.

Reason: the code comment says Linux window managers keep their own frame. On Windows the app draws its own.

### Obsidian's settings are found per install

On Linux every place is tried: the standard one, Flatpak and Snap. The one whose `obsidian.json` was written last is used. See [[Obsidian]].

Reason: sandboxed installs keep their settings out of `~/.config`, and a second, forgotten install must not win.

### Process names match in any case

Reason: Linux names the process `obsidian`. A case-sensitive check for `Obsidian` made an open vault look closed. `CLAUDE.md` records that Obsidian rewrites its config on close, which would undo a change made while the vault was open. The match ignores case and compares the whole name.

### Paths compare by case on Linux only

Reason: on Linux `/home/a/Notes` and `/home/a/notes` can be two folders, and treating them as one would put a change into the wrong vault (`Files.cs`). Windows and macOS ignore case, as their usual file systems do.

### The Linux smoke check runs under xvfb-run

Reason: Skia and the font check need a display, so the Linux file's `--smoke` run uses a virtual one (`release.yml`). See [[Release]].

## Consequences

- The Ubuntu test run sets no runtime identifier, so it does not turn on invariant mode and uses ICU where the runner has it. The ICU-free build is covered only by the `--smoke` check of the published `linux-x64` file.
- Culture-aware formatting and sorting use the invariant culture in Linux builds. The Windows build is unchanged.
- The write-time rule for settings folders is a stand-in for "used last", not a record of use.
- Each release now has two files to check. The steps are in [[Release]].

## Not verified

- A full Linux desktop session. The libICE and libSM point was checked on Ubuntu 26.04 under WSL, where the Linux file passes its self-check, as the README says.
- Flatpak and Snap Obsidian. Their paths follow Obsidian's documented locations and have not been tried on a real install.
- Wayland without XWayland.
- The full test suite on Linux. Only CI runs it.

## Links

- [[Obsidian]]
- [[Release]]
- [[Release on push to product]]
- [[v0.6.0.3 - Linux support]]
