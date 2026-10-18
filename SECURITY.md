# Security policy

## Reporting a vulnerability

Please report security problems **privately**, not in a public issue:

1. Open this repository on GitHub and go to the **Security** tab.
2. Click **Report a vulnerability** (GitHub private vulnerability reporting).
3. Describe what you found, the BlindsDown version (tray menu > **About BlindsDown**; if you use the camera
   helper, also its version from Settings > Camera, which can differ), your Windows version, and the steps
   to reproduce it. A log excerpt from `%LocalAppData%\BlindsDown\blindsdown.log` (or, for the camera helper,
   `%ProgramData%\BlindsDown\helper.log`) often helps; please check it for anything private first.

BlindsDown is a free open-source project that I maintain on my own in my spare time. I aim to acknowledge a
report within 7 days, agree on a fix and a disclosure date with you, and credit you in the release notes if
you'd like. Only the latest release gets security fixes.

Examples of what I want to hear about: a way to make BlindsDown reach the network; a microphone or camera
that stays usable while the lock shows red; a way to make BlindsDown change devices it should leave alone or
fail to restore them; a way to make the app or the helper start a program; any way to use the camera helper
(which runs with system rights) to do anything other than lock and unlock cameras and microphone-only
hardware; a way for another program to unlock it without the tone and the red "Unlocked by another program"
popup; or a release file that doesn't match its checksum or attestation.

## Threat model in brief

- **What BlindsDown protects against:** apps, websites and people using your microphone or camera when you
  don't expect it (a hot mic in a background app, a forgotten call, an app that watches or listens all the
  time), and not knowing which app is doing it. It locks every microphone and, with the helper, every camera,
  re-locks any that get turned back on, and tells you who is using them.
- **The app runs as you, never elevated.** It can't stop malware that has administrator rights: such malware
  can undo anything any app does. Only a physical camera cover or a hardware microphone switch is certain.
- **The optional camera helper runs as SYSTEM, so it is kept small and strict:**
  - Its program file is copied to `C:\Program Files\BlindsDown`, which only administrators can change (a
    service that ran from a folder you can write to would let any program get system rights by replacing it).
  - Its data folder (`%ProgramData%\BlindsDown`) is readable and writable only by SYSTEM and administrators.
  - It accepts exactly four requests over a local named pipe: hello, status, lock (with options such as
    "include infrared cameras" and lists of devices to leave alone) and unlock. It never accepts a device to
    turn off: it picks cameras and microphone-only hardware itself, and the lists it accepts can only make it
    do less. Requests are size- and length-checked and strictly parsed.
  - The pipe rejects remote clients (Windows' `PIPE_REJECT_REMOTE_CLIENTS` flag plus an access rule that
    denies network users), lets only signed-in (interactive) users read and write it, and doesn't let them
    create instances of it, so no program can take its name.
  - The app checks before every request that the pipe really belongs to the helper (owned by SYSTEM and served
    from the service session), and identifies itself so the helper can't act as you.
  - It turns devices off without the "persistent" flag: a restart always brings every device back if the
    helper is gone or broken (a real restart: with Windows' Fast Startup, "Shut down" keeps the devices as
    they were). While it works, it re-applies an active lock after a restart, so a restart is not a way to
    unlock.
  - It is installed, updated and removed only through one Windows administrator prompt that you start
    yourself, and it shows up in Installed apps.
- **Malware running as your user can unlock BlindsDown**, for example by faking the hotkey or asking the
  helper to unlock. It can't do so unnoticed: every unlock shows an on-screen message, changes the tray icon
  and is recorded in the local lock timeline. An unlock by hotkey or tray click plays the unlock tone if sounds
  are on. An unlock that comes from the command line (`--unlock`, `--toggle`, `--restore`), from another
  program, or of the cameras through the helper by anything other than the app always plays the tone, even with
  sounds off, and shows a red popup that stays until it is closed ("Unlocked by another program", or "Lock
  changed by another program" for a lock that only covers less). The helper records every change to its lock
  with a fingerprint of the ID of the request that made it (it never hands out the IDs themselves, and refuses
  an ID it has seen before), and only the app knows which IDs are its own, so an unlock (or a lock that covers
  less) by another program is reported even when it is undone again between two checks, or happened while the
  app wasn't running. This guards against other programs' requests. It can't stop malware that has taken over
  your account from ending BlindsDown (the tray icon disappears) or editing its files in
  `%LocalAppData%\BlindsDown`, which any program running as you can do.
- **While locked, every microphone and camera use is shown.** Any app that opens one gets a popup that stays
  until it is closed, even apps on the "always allow" list and even with popups turned off. It says whether
  that device is actually protected, and warns in red when it is excluded or could not be locked. The guard
  re-locks a device every time something turns it back on, and never gives up.
- **Software mute has known bypasses:** apps that open a microphone in exclusive mode (on mics without
  hardware mute) and pro-audio apps using ASIO or kernel streaming. The optional stronger microphone lock
  (Windows' "Don't allow" switch) stops exclusive mode; turning microphone-only hardware off through the
  helper stops the rest. The app labels each device's protection honestly.
- **No network, enforced:** BlindsDown (app and helper, the same exe) contains no networking code, so it can't
  leak data or be controlled remotely. This is enforced by a compile-time API ban, a CI scan of the compiled
  program (networking references, native DLL allowlist, COM class allowlist, process starts, service
  registration, computer-name parameters, pipe clients), a runtime
  tripwire (a red popup that stays until closed, plus a log entry, if networking code is ever loaded), and no
  third-party code (only this repository and Microsoft's .NET packages). It also never opens network paths:
  for apps that run from a network drive it skips their icon and version details, because Windows would fetch
  those over SMB. See the README for how to verify it yourself.
- **It starts nothing but Windows Settings pages and itself.** One audited place in the code can start
  anything: it can only open a fixed list of `ms-settings:` pages, or restart BlindsDown.exe itself with
  administrator rights and one of three fixed options (install, update or remove the helper). No URLs, no
  other paths. Apart from that, only Windows starts BlindsDown.exe, in two fixed ways: the helper service
  (`--service`, registered in one place, always from Program Files and never with a "run a program on failure"
  setting; the CI scan checks this) and the optional start at sign-in (`--autostart`, checked by code review).
  COM objects can only be created from a two-entry allowlist (the Windows audio device enumerator and the Sound
  settings policy object).
- **Your devices are never left broken:** every change is journaled before it is made, unlock only undoes
  what BlindsDown changed (devices you disabled or muted yourself stay that way, and your default microphones
  are put back if turning a mic off made Windows pick another), and a crash or kill is recovered on the next
  start. `BlindsDown.exe --restore` undoes every recorded change even after the exe was deleted and downloaded
  again, because the journal lives in `%LocalAppData%\BlindsDown`. A microphone that was unplugged at unlock is
  restored when it is plugged back in while BlindsDown runs. It never changes volume levels.
- **Local data is local:** settings, history, the lock timeline and logs live in `%LocalAppData%\BlindsDown`,
  readable by your Windows account and administrators; the helper's journal and logs live in
  `%ProgramData%\BlindsDown`, readable only by administrators and the system. Nothing is ever sent anywhere.
  History records which app used a microphone or camera, and when, with the app's name and exe path; entries
  older than the retention period (30 days by default) are deleted automatically, and history can be turned
  off or cleared. The lock timeline keeps recording locks and unlocks even with history off (so no unlock
  goes unseen); it uses the same retention and is cleared with the history. Logs contain device names, app
  names and exe paths (which include your Windows user name), and every lock and unlock; never keystrokes,
  window titles, audio or video.
- **Supply chain:** release zips are built by GitHub Actions from a tagged commit, with actions pinned by
  commit SHA, NuGet package versions pinned exactly in the project files (Dependabot proposes updates),
  SHA-256 checksums and a signed build provenance attestation (`gh attestation verify`). The exe is not
  code-signed yet, so verify downloads as the README describes.
- **Out of scope:** physical access to an unlocked PC, malicious drivers or firmware, and a compromised
  Windows installation. Note that microphone mute and turning devices off are machine-wide in Windows, so the
  lock also affects other users of the same PC.
