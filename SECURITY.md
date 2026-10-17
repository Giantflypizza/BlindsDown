# Security policy

## Reporting a vulnerability

Please report security problems **privately**, not in a public issue:

1. Open this repository on GitHub and go to the **Security** tab.
2. Click **Report a vulnerability** (GitHub private vulnerability reporting).
3. Describe what you found, the BlindsDown version (Settings or About), your Windows version, and the steps
   to reproduce it. A log excerpt from `%LocalAppData%\BlindsDown\blindsdown.log` often helps; please check
   it for anything private first.

BlindsDown is a volunteer open-source project. We aim to acknowledge a report within 7 days, agree on a fix
and a disclosure date with you, and credit you in the release notes if you'd like. Only the latest release
gets security fixes.

Examples of what we want to hear about: a way to make BlindsDown reach the network, a microphone that stays
usable while the lock shows red "MIC LOCKED", a way to make BlindsDown change devices it should leave alone
or fail to restore them, a way to make it start a program, a way for another program to unlock it without
the tone and the red "Unlocked by another program" popup, or a release file that doesn't match its checksum
or attestation.

## Threat model in brief

- **What BlindsDown protects against:** apps, websites and people using your microphone when you don't
  expect it (a hot mic in a background app, a forgotten call, an app that listens all the time), and not
  knowing which app is listening. It mutes every microphone, re-mutes any that get unmuted, and tells you
  who is using a microphone.
- **It runs as you, never elevated.** It can't stop malware that has administrator rights: such malware can
  undo anything any app does. Only a physical camera cover or a hardware microphone switch is certain.
- **Malware running as your user can unlock it**, for example by faking the hotkey. It can't do so
  unnoticed: every unlock shows an on-screen message, changes the tray icon and is written to the local log
  file. An unlock by hotkey or tray click plays the unlock tone if sounds are on. An unlock that comes from
  the command line (`--unlock`, `--toggle`, `--restore`) or from another program always plays the tone, even
  with sounds off, and shows a red "Unlocked by another program" popup that stays until it is closed.
  Locks and unlocks are not in the Activity history yet; a lock and unlock timeline there is planned.
- **While locked, every microphone use is shown.** Any app that opens a microphone gets a popup that stays
  until it is closed, even apps on the "always allow" list and even with popups turned off. It says whether
  that microphone is actually protected, and warns in red when it is excluded or could not be locked. The
  guard re-mutes a microphone every time something unmutes it, and never gives up.
- **Software mute has known bypasses:** apps that open a microphone in exclusive mode (on mics without
  hardware mute) and pro-audio apps using ASIO or kernel streaming. Device-level disable in a later stage
  narrows this. The app labels each mic's protection strength honestly.
- **No network, enforced:** BlindsDown contains no networking code, so it can't leak data or be controlled
  remotely. This is enforced by a compile-time API ban, a CI scan of the compiled program (networking
  references, native DLL allowlist, COM class allowlist, process starts), a runtime tripwire (a red popup
  that stays until closed, plus a log entry, if networking code is ever loaded), and zero third-party
  runtime packages. It also never opens network paths: for apps that run from a network drive it skips
  their icon and version details, because Windows would fetch those over SMB. See the README for how to
  verify it yourself.
- **It only opens Windows Settings pages.** One audited place in the code can start anything, and it can
  only open a fixed list of `ms-settings:` pages; no URLs, no paths. COM objects can only be created from a
  two-entry allowlist (the Windows audio device enumerator and, from Stage 2, the Sound settings policy object).
- **Your devices are never left broken:** every change is journaled before it is made, unlock only undoes
  what BlindsDown changed (devices you disabled or muted yourself stay that way), and a crash or kill is
  recovered on the next start. `BlindsDown.exe --restore` undoes every recorded change even after the exe
  was deleted and downloaded again, because the journal lives in `%LocalAppData%\BlindsDown`. A microphone
  that was unplugged at unlock is restored when it is plugged back in while BlindsDown runs. It never
  changes volume levels or default devices.
- **Local data is local:** settings, history and logs live in `%LocalAppData%\BlindsDown`, readable by your
  Windows account and administrators. They are never sent anywhere. History records which app used a
  microphone or camera, and when, with the app's name and exe path; entries older than the retention period
  (30 days by default) are deleted automatically, and history can be turned off or cleared. Logs contain
  device names, app names and exe paths (which include your Windows user name), and every lock and unlock;
  never keystrokes, window titles or audio.
- **Supply chain:** release zips are built by GitHub Actions from a tagged commit, with actions pinned by
  commit SHA, NuGet package versions pinned exactly in the project files (Dependabot proposes updates),
  SHA-256 checksums and a signed build provenance attestation (`gh attestation verify`). The exe is not
  code-signed yet, so verify downloads as the README describes.
- **Out of scope:** physical access to an unlocked PC, malicious drivers or firmware, and a compromised
  Windows installation. Note that microphone mute is machine-wide in Windows, so the lock also affects other
  users of the same PC.
