# BlindsDown design

How BlindsDown works, why it is built this way, and what comes next. For users, the [README](../README.md) is the better
starting point; for testing, see [TESTING.md](TESTING.md).

---

## 1. Summary

- **A small tray app (C#, .NET 10, WinForms)** with a hotkey that toggles a privacy lock.
- **Microphones are locked in layers:**
  1. **Mute.** No admin needed. Always on.
  2. **Windows' own endpoint disable**, the switch behind Sound settings → "Don't allow". No admin needed. Opt-in
     ("stronger microphone lock").
  3. **True hardware disable**, used only for devices that are *only* a microphone, such as a webcam's built-in mic or a
     USB mic. Needs the helper. Opt-in.

  The layering exists because disabling a microphone's hardware device would often take the speakers or headphones with
  it: a USB headset's microphone and headphones are usually one USB function.
- **Cameras are disabled at the PnP device level** by a small **helper Windows service**, which the app installs itself
  when the user turns the camera lock on (one admin prompt at install, none afterwards).
- **Live usage alerts come from documented APIs:** audio sessions for microphones and the Media Foundation *sensor activity
  monitor* for cameras. Windows' usage registry is only a fallback, because newer Windows builds are phasing it out.
- **Restore is journal-based and verified.** Every change is written down *before* it is made. Unlock undoes only what
  BlindsDown changed, checks the result, and shows a *restore report*.
- **"Never connects to the internet" is enforced in code:** zero third-party runtime packages, a compile-time ban on
  networking APIs, a CI test that scans the compiled program, a runtime tripwire, and instructions so anyone can verify it.

---

## 2. Design decisions

| Obvious approach | What BlindsDown does | Why |
|---|---|---|
| Disable every microphone in Device Manager | Mute always; Windows' **endpoint disable** for physical mics as an opt-in; a **hardware disable** (opt-in, helper) only when the hardware is mic-only | Disabling a mic's node under "Audio inputs and outputs" is reported **not to stop apps recording** (Microsoft Q&A, 2018 and 2024). Disabling the parent hardware also kills speakers and headphones on combined devices. The endpoint disable used by Sound settings does block apps, but it also takes the mic away from apps that have it open (like unplugging it), some of which then need it picked again, and virtual mixers like Sonar re-route. Mute alone keeps every app working and was what users liked in Stage 1, so the stronger layers are a choice, explained in Settings. |
| Read "who uses the mic" from Windows' usage registry | **Primary:** audio sessions (mic) and `IMFSensorActivityMonitor` (camera). **Fallback:** the usage registry | Several open-source projects reported in 2026 that Windows 11 builds 26220/26300 stopped updating the registry, behind a gradual feature flag. The live APIs are documented, name the process, and need no admin. |
| Run the whole app elevated | **Unelevated tray app + optional helper service**. The portable exe works without admin for microphones | See section 5. An always-elevated tray breaks under Windows 11's *Administrator protection* (it reads the wrong user's data), can't autostart from the Run key, and loses its tray icon when Explorer restarts. |
| Lock every device | Defaults: IR/Windows Hello cameras **excluded**, virtual cameras **excluded**, virtual mics **muted, not disabled**; all changeable | Locking the physical mic already silences virtual microphone chains (e.g. an app → a virtual mixer such as SteelSeries Sonar or Voicemeeter → the real mic). Disabling the virtual mic itself can make the mixer software re-route or change defaults. |
| Offer to restore after a crash | Also: a write-ahead journal, a `--restore` command, non-persistent camera disable (a restart always brings cameras back if the helper is gone or broken; while the helper is installed it re-applies a journaled lock at boot, even if the app's exe was deleted, so use `--restore`) | A crash, a deleted exe or a failed uninstall must never leave a device broken. |

Other features that follow from these choices:
- **Restore report** after every unlock: device state compared against the pre-lock snapshot.
- **Per-device lock strength** in the UI (Settings > Devices): "Muted in hardware", "Muted in software (exclusive-mode
  apps can bypass)", "Turned off in Windows (apps can't open it)", "Turned off (device disabled)", "Not locked: in
  use by an app; BlindsDown keeps trying".
- **"Always allow" list for alerts.** Some apps (virtual audio mixers) hold the mic open all the time; without this they
  would alert forever.
- **Virtual-chain attribution:** "Discord (via SteelSeries Sonar)", not just "SteelSeries Sonar".
- **Optional auto-lock** on Win+L or sleep (off by default; unlocking is never automatic).

---

## 3. Key research findings

✅ = checked against Microsoft documentation, source code, or a read-only probe on a Windows 11 25H2 PC.
🧪 = still needs a hands-on test.

| Topic | Finding | Verified |
|---|---|---|
| Mic endpoints | Every mic is an `SWD\MMDEVAPI\{0.0.1.00000000}.{guid}` node. Its `Parent` is the real hardware, and the endpoint property `{b3f8fa53-0004-438e-9003-51a46e139bfc},2` names the hardware in every state. | ✅ |
| Device Manager mic disable | Disabling the `SWD\MMDEVAPI` node only marks it disabled; apps keep recording (two independent Microsoft Q&A reports). | ✅ reports, 🧪 on 25H2 |
| Sound-settings disable | Undocumented `IPolicyConfig::SetEndpointVisibility`. CLSID `870AF99C-…` and IID `F8679F50-…` have been stable since Windows 10 1607; EarTrumpet and SoundSwitch use them. The state persists in HKLM. **Works for a normal, unelevated user:** tested on Windows 11 25H2 on an unused line-in (off → state Disabled → on → state as before; a full snapshot before and after was identical). | ✅ |
| Already-open streams | Microsoft's docs say an endpoint disable hides the device but **doesn't cut streams that are already open**. So: mute first, then disable. Right before turning a mic off, BlindsDown reads its mute again and only turns off a mic that is still muted; a mic unmuted at that moment is muted again (and alerted) first, so an app that had it open only ever hears silence. | ✅ |
| Software mute | On mics without hardware mute (`QueryHardwareSupport` lacks bit `0x2`), **exclusive-mode apps bypass mute**. Many USB headsets have hardware mute; virtual mics usually don't. | ✅ |
| Bypass paths | ASIO and direct kernel-streaming (WDM-KS) capture bypass endpoint disable and mute. Only a hardware disable stops them. | ✅ inferred from docs, 🧪 |
| Default devices | Windows picks a new default when the default mic is disabled, and doesn't switch back when it returns. BlindsDown records the defaults once the lock settled and, at unlock, puts back each role whose default is still the one Windows picked (`IPolicyConfig::SetDefaultEndpoint`). If another mic stays unprotected (excluded, or its mute failed), default mics are only muted, never turned off, so Windows can't move apps onto the unprotected mic. | ✅ API, 🧪 behaviour |
| PnP disable | `CM_Disable_DevNode` **without** `CM_DISABLE_PERSIST` lasts only until a restart, so a restart is a safety net (a real restart: Fast Startup's "Shut down" hibernates the kernel and keeps the disable). A device in use can veto the disable or need a restart, so **every disable is re-checked** (`CM_Get_DevNode_Status` → problem 22). Admin is required. x64/ARM64 only: 32-bit callers fail with ERROR_IN_WOW64. | ✅ |
| Cameras | Found by interface (`KSCATEGORY_VIDEO_CAMERA`, `KSCATEGORY_SENSOR_CAMERA`), not by class: the "Image" class also contains scanners. Disable the `MI_xx` function, **never** the USB composite parent (that also kills the webcam's mic). IR (Hello) cameras use `SENSOR_CAMERA`. Media Foundation virtual cameras are `SWD\VCAMDEVAPI\…`; DirectShow-only virtual cameras (e.g. OBS) have no device node at all. | ✅ |
| Mic usage, live | `IAudioSessionManager2` on every capture endpoint gives the process ID of every capturing app, including through virtual mics. No admin needed. | ✅ |
| Camera usage, live | `MFCreateSensorActivityMonitor` (mfsensorgroup.dll, Windows 10 1703+) gives per-process "streaming" state, no admin needed. Chromium uses it. | ✅ API runs, 🧪 with a real camera |
| Usage registry | `HKCU\…\CapabilityAccessManager\ConsentStore\{microphone,webcam}`: `LastUsedTimeStop == 0` means in use. Still written on many builds; reportedly frozen on 26220/26300. Can be stale after crashes. | ✅ |
| Windows' own history DB | `C:\ProgramData\Microsoft\Windows\CapabilityAccessManager\*.db` is admin-only. BlindsDown **never touches it** and keeps its own history. | ✅ |
| Elevation | Device enable/disable needs Administrators membership, so the helper must run as LocalSystem. LocalService and NetworkService can't do it. | ✅ |
| Administrator protection | New in Windows 11 (KB5120998, 2026, off by default). Elevated apps run as a hidden separate account with a **different HKCU**. Microsoft recommends a service-based design. | ✅ |
| .NET | .NET 8 and 9 reach end of support on 2026-11-10; **.NET 10 LTS** is supported until 2028-11-14. | ✅ |

Main sources: Microsoft Learn (Core Audio, CfgMgr32/SetupAPI, Media Foundation, service and named-pipe security pages),
the EarTrumpet and SoundSwitch sources (IPolicyConfig), microsoft/Windows-driver-samples (devcon), and public issue reports
from PowerToys Video Conference Mute, HASS.Agent and AutoKeyLight.

---

## 4. Architecture

```
┌──────────────── BlindsDown.exe (tray, runs as you, never elevated) ──────────────┐
│ Hotkey (RegisterHotKey)  •  Tray icon  •  On-screen lock indicator  •  Sounds    │
│ Lock engine: snapshot → plan → journal → apply → verify → restore report         │
│ Mic layer:  mute (IAudioEndpointVolume) + opt-in endpoint disable (IPolicyConfig)│
│ Guards: re-mute / re-disable if anything changes while locked (+ alert)          │
│ Usage monitor: audio sessions + camera sensor monitor + usage registry           │
│ History (local JSONL)  •  Settings  •  Exclusions  •  Alert popups               │
└──────────────┬───────────────────────────────────────────────────────────────────┘
               │ named pipe, local-only ACL, tiny fixed command set
┌──────────────▼──────── BlindsDown.exe --service (optional LocalSystem helper) ───┐
│ PnP layer: disable/enable cameras + mic-only audio hardware (CM_Disable_DevNode) │
│ Enforces while locked even before sign-in: new devices disabled on arrival       │
│ Own journal in %ProgramData% (admin-only ACL) • re-applies lock after reboot     │
│ Picks its own targets from a class allowlist; never acts on IDs sent by clients  │
└──────────────────────────────────────────────────────────────────────────────────┘
```

- **One exe, several modes:**
  - tray (default)
  - `--lock` / `--unlock` / `--toggle`, sent to the running tray app (which is started if needed). Unlocks that arrive
    this way always play the unlock sound and show a sticky red "Unlocked by another program" popup.
  - `--restore`: the emergency "put everything back" command. Works even after re-downloading the exe, because the journal
    lives in `%LocalAppData%\BlindsDown`; if the tray app is running it just unlocks it.
  - `--snapshot [file]`: dumps the state of every microphone, camera and microphone-hardware device to a text file
    for before/after comparison (read-only).
  - `--selftest [file]`: end-to-end check on the real microphones: lock, verify every mic is muted, unlock, verify the
    snapshot is identical to before.
  - `--service`: the helper (started by Windows only).
  - `--install-helper` / `--update-helper` / `--uninstall-helper`: run elevated, started by the app through one UAC prompt
    (Installed apps runs `--uninstall-helper`, which elevates itself).
- **Without the helper**, the app locks microphones only and lists cameras as "needs the camera helper". With the helper, it
  is the single authority for the PnP layer; each layer restores only what *it* changed.
- **Order:** lock = microphones first (mute, then the optional endpoint disable), then the helper; unlock = the helper first
  (so mic hardware is back), then the microphones (re-enable, unmute, defaults).

### 4.1 Lock algorithm

**Lock:**
1. Register device notifications *before* enumerating, so nothing slips through.
2. **Snapshot** every capture endpoint (state, mute, hardware-mute support, hardware parent, physical/virtual), the three
   default-mic roles and (in the helper) every camera and microphone-hardware node.
3. **Plan.** Only devices that are **working right now** (active, or a jack/Bluetooth mic that can be opened) and not
   excluded are in scope. Anything already disabled or missing is left alone, which is how "devices I disabled myself stay
   disabled" is guaranteed.
4. **Journal** (atomic write: temp file → flush → replace, plus a .bak) → apply → verify, one device at a time,
   non-default mics first and default ones last (so apps don't hop between mics).
5. **Verify.** The indicator says **Locked** only when every device is verified. Otherwise it shows **Partially locked**
   in amber, with the reason.

**While locked:**
- A new microphone → locked immediately, with an alert.
- Unmuted (or re-enabled) by anything → locked again immediately, with an alert. The guard never gives up; after 5 fights
  within a minute it raises a red "Something keeps undoing the microphone lock" alert (its text says what: "Something keeps unmuting …" or "… turning … back on"),
  and further fights update that alert instead of stacking new ones.
- Any app that opens a mic → a sticky popup, even for always-allowed apps and with popups turned off. It states whether that
  mic is protected ("only hears silence" when muted, with the exclusive-mode caveat for software-only mute) or warns in red
  when the mic is excluded or could not be locked.
- If the journal can't be written (disk full), BlindsDown does not touch a device it can't record, and the lock shows as
  partial.

**Unlock:** reverse order. Undo only what the journal says BlindsDown changed, and only if the device is still in the state
BlindsDown left it in; verify; show the **restore report**; delete the journal. Devices unplugged at unlock go into
`pending-restores.json` and are restored when they come back while the app runs. "Unlock and exit" explains anything that
couldn't be restored exactly and offers to keep running until the device is reconnected.

**Never:** change volume levels, touch per-app device preferences, write registry device state directly, or disable a USB
composite parent or an HD Audio controller.

**Crash / reboot:**

| Situation | What happens |
|---|---|
| App starts and finds a journal from a crash | It asks: "Restore my devices" or "Keep locked". Meanwhile: after a crash while locked, the guard keeps running; after a crash while locking, mics that were already muted are kept muted, and nothing else changes until the user chooses; after a crash while unlocking, nothing changes until then. No lock or unlock can run while the question is open. |
| Windows signs out or shuts down while locked | The lock simply continues at the next start (mute persists in Windows). |
| App deleted while locked | Re-download it and run `--restore` (it also asks the helper to unlock), or unmute in Sound settings. |
| Helper removed (or unable to start) while locked | A restart restores every camera: the helper never uses the persistent disable. A helper that is installed and merely stopped answering re-applies its lock at boot, so unlock (or remove it) first. |
| Restart while locked, helper working | The helper re-applies its lock at service start, before sign-in; the app resumes the mic lock after sign-in. So a restart never unlocks the cameras while the helper is installed: it has to be unlocked (app, `--restore`) or removed. |
| Helper removal | Settings or Installed apps: the helper unlocks first; if that fails, removal continues and says a restart will bring the devices back. |

---

## 5. Elevation

Disabling devices needs administrator rights. The options considered:

| | A. Always-elevated tray | B. UAC prompt per toggle | C. "Highest privileges" scheduled task | **D. Helper service (chosen)** |
|---|---|---|---|---|
| Prompts | None after setup (admins only) | **Every hotkey press** | None | **One, at install** |
| Standard (non-admin) users | Admin password every sign-in | Admin password every toggle | No | **Yes** |
| Windows 11 Administrator protection | **Breaks** (reads the wrong user's data) | Hello prompt each time | Undocumented | **Unaffected** (Microsoft's recommendation) |
| Locked before sign-in / after reboot | No | No | No | **Yes** |
| New devices while app closed | Missed | Missed | Missed | **Disabled on arrival** |
| Code running as admin | The whole UI | Tiny, short-lived | Helper; any same-user process can trigger it | Small service with a few pipe commands |
| Distribution | Portable exe | Portable exe | Portable (risky if exe is user-writable) | **Portable exe that installs its own helper** (one UAC prompt; copies itself to Program Files; Installed apps entry for removal) |

**Honest security note.** With A, C or D, malware running as *you* could unlock, for example by faking the hotkey or sending
"Unlock". Windows offers no way to fully stop that. Mitigations: every unlock flashes on screen and is logged; an unlock
from the command line or another program always plays the sound and leaves a sticky red popup; a lock/unlock timeline in
Activity records every lock and unlock; an unlock of the cameras through the helper by anything other than the app raises
the same red popup. An optional "Require admin approval to unlock" may follow.

**Service hardening:** the exe is copied to Program Files (admin-only) before it is registered, so the SYSTEM service never
runs a file a user could replace; the data folder in ProgramData is protected (SYSTEM and Administrators only); the pipe is
created with `PIPE_REJECT_REMOTE_CLIENTS`, denies network users and grants interactive users read/write without the right to
create pipe instances; the app talks to it only through `CreateFileW` on the local path with identification-level
impersonation, after checking that the pipe is owned by SYSTEM (it is created with `O:SY`; Administrators is accepted too)
and served from session 0 (`GetNamedPipeServerSessionId`; no squatting); message size limits and strict JSON parsing; the
service never accepts device IDs to act on: it picks cameras and mic-only hardware itself, and the request can only list
devices to leave alone (it can't be abused to disable a disk or network card); a request that changes what a lock covers and
turns devices back on raises an urgent alert; the helper keeps a log of every change to its lock (`lock-changes.json` in its
data folder, so it survives a restart of the helper) with a SHA-256 fingerprint of the request's ID, and refuses an ID it
has seen before. The file also keeps the last lock request (so a narrowing the engine finishes after a restart is still
that request's) and the highest ID handed out, alerts included, so each run of the helper continues above it and an
answer's run ID tells the app when its position came from an earlier run. Every Lock and Unlock the app sends carries a new random ID; the app keeps the fingerprints of its own
(with the command each was sent with), its position in the log and whether the helper holds this user's lock in
`helper-state.txt`, and it records the IDs of `--restore` and of the elevated setup runs it starts (which get theirs on
the command line, and only when they will unlock) before they unlock. So a change with any
other fingerprint is another program's: the app announces it (tone even with sounds off, on-screen message, red popup,
timeline) when it touches this user's lock, also when it happened while the app wasn't running (at the next start, with
the helper's time). A lock the helper held that is gone at the next start without a change in the log raises the same
alarm; a queued request whose client hung up is dropped, and
every Lock/Unlock gets one of three answers within two minutes (done, busy so not done, or still working); a data folder
that anyone but SYSTEM/Administrators could change is moved aside and recreated, never fixed in place; setup never follows
links and removes the install folder only if it created it; the service registration is always the Program Files copy with
`--service` and no failure command; no `OnCustomCommand`.

---

## 6. Usage alerts and history

**Sources:**
- **Microphone:** audio sessions on every capture endpoint, event-driven on a dedicated background thread, plus a 2 s safety
  poll. Only *active* sessions count; PID 0 and system sounds are ignored.
- **Camera:** `IMFSensorActivityMonitor`, live and per process. Before 1.0, camera alerts came only from the usage registry (best
  effort: about 10 s delay, and some newer Windows 11 builds stop updating it).
- **Fallback:** the usage registry, watched with `RegNotifyChangeKeyValue`. Stale entries are ignored (process not running,
  or started before boot). It also provides friendly names for Store apps and a "last used" backfill on first run.
- **Never open network paths:** for an exe on a network drive or share, its icon and version info are not read.

**App identity:** Store apps by package family name; desktop apps by exe path (version folders such as Discord's
`app-1.0.9xxx` are ignored, so updates don't look like a new app, while an impostor exe elsewhere doesn't inherit an "always
allow"); svchost by the hosted service's name. Browsers show the browser name and "Windows doesn't say which website."

**Alerts:** BlindsDown's own always-on-top popups that never steal focus (Windows 11 toasts are transient and swallowed by
Do Not Disturb). Device probes under 1 s are ignored. "Always allow" and the popup switch apply only while unlocked.

**History:** `%LocalAppData%\BlindsDown\history.jsonl`, stored in UTC and shown in local time ("Zoom used your mic
14:02–14:47"), covering microphone and camera use with the app's name and exe path. Gaps under 15 s are merged; retention
defaults to 30 days (pruned at startup and every 6 hours); history can be turned off (with an offer to delete what exists)
or cleared. The lock timeline (`lock-events.jsonl`) is always recorded, even with history off, so no unlock goes unseen; it
uses the same retention and is cleared with the history.

---

## 7. User interface

- **Tray icon:** drawn at the exact pixel size for the taskbar's DPI; light glyph on a dark taskbar; a different *shape* when
  locked, not just colour; a left click toggles the lock.
- **Hotkey:** default **Ctrl+Alt+Shift+M** (no Win key, which Windows reserves; Shift alone with a letter is rejected because
  it would steal capital letters). Configurable with a capture box that tests the combination for real; F13–F24 work for
  macro pads. The hotkey keeps working while Settings is open; only while the capture box has the focus does a press of
  it go into the box instead. Some elevated or fullscreen apps swallow application hotkeys, so every toggle is confirmed
  on screen.
- **Lock indicator:** a red "MIC LOCKED" pill in a screen corner (optionally thin red edges) on **every monitor**,
  click-through, never taking focus, correct on mixed-DPI setups. Hidden from screen sharing and recordings by default,
  together with the on-screen flash and the popups. Amber when only partially locked.
- **Sounds:** two short generated tones, falling for lock and rising for unlock. No audio files, nothing loaded from paths.
- **Windows:** Settings (hotkey, sounds, autostart, camera and helper, indicator, devices and exclusions, alerts allowlist,
  history), Activity, About. Settings shows each device's protection and notices when autostart was turned off in Task
  Manager. Popups stay usable while a modal window (Settings, About) is open. Dark mode follows Windows 11.

---

## 8. "Never connects to the internet": enforced, not promised

1. **No third-party runtime packages.** (The helper adds only Microsoft's own `System.ServiceProcess.ServiceController`.)
   Test packages never ship.
2. **Compile-time ban** ([BannedSymbols.txt](../BannedSymbols.txt), build errors): all of `System.Net`, `Windows.Networking`
   and `Windows.Web`, plus WinForms/.NET APIs that can fetch URLs or reach other machines (PictureBox, WebBrowser, Help,
   `SoundPlayer(url)`, file dialogs, remote pipes and registry, COM by name, code loading by path) and `Process.Start` except
   one audited opener for fixed `ms-settings:` pages.
3. **CI scan of the compiled program** (System.Reflection.Metadata): fails on any networking reference, any native DLL
   outside an allowlist, any COM class outside an allowlist, or `Process.Start` outside the audited opener.
4. **Runtime tripwire:** if networking code ever loads into the process, a red alert appears and the log records it.
5. **"Verify it yourself"** in the README: firewall rule, Resource Monitor, TCPView, `Get-NetTCPConnection`, SHA-256 sums and
   GitHub build attestations.
6. Honest wording: the self-contained exe *contains* .NET's networking libraries because it bundles the runtime; the app's
   own code never references or loads them, as points 2–4 check.

---

## 9. Technology and layout

- **.NET 10 (LTS), C#, WinForms:** built-in tray icon, small footprint, approachable code. x64 and ARM64 only, never 32-bit.
- **Hand-written COM and P/Invoke** (Core Audio, CfgMgr32): no NAudio, so the code stays small and auditable.
- **Release build:** a self-contained single-file exe of about 50 MB that needs nothing installed. Every .NET security patch
  means a new release, which the release workflow makes a one-tag job.

```
BlindsDown.slnx
global.json, Directory.Build.props/.targets, BannedSymbols.txt
src/BlindsDown.Core/      pure logic, no Windows calls: lock engine, journal, restore report, usage
                          sessions, history, settings, hotkeys -> fully unit-tested with fake devices
src/BlindsDown/           the app: tray UI, Core Audio interop, monitors, command-line tools
tests/BlindsDown.Tests/   xUnit: unit tests, crash and property tests, no-network scan, and opt-in
                          tests against real devices (BLINDSDOWN_DEVICE_TESTS=1)
.github/workflows/        ci.yml (build + test), release.yml (tag -> zips, SHA256SUMS, attestations)
docs/                     DESIGN.md (this file), TESTING.md (automated and manual tests)
```

---

## 10. Roadmap

**Stage 1 – microphone mute lock (done)**
- Tray app, configurable hotkey, single instance, autostart option, settings.
- Lock = mute every in-scope mic, including empty jacks and Bluetooth hands-free mics; journal, exact restore, crash
  recovery, restore report; guard that never gives up; new mics muted on arrival; per-device exclusions.
- Live mic usage alerts (audio sessions + usage registry), allow list, virtual-chain attribution; best-effort camera usage
  alerts from the usage registry; local history with retention.
- Indicator, on-screen flash and sounds; command line (`--lock/--unlock/--toggle/--restore/--snapshot/--selftest`).
- No-network enforcement, README, CI and release workflow.

**Stage 2 – camera lock and the final version (done, 1.0)**
- Optional LocalSystem helper, installed from Welcome or Settings with one UAC prompt: camera disable (IR and virtual
  cameras only on request), opt-in mic-only hardware disable, guard and new-device enforcement, boot re-lock, its own
  journal; `--restore` asks it to unlock too; removal from Settings or Installed apps unlocks first.
- Opt-in stronger microphone lock (`IPolicyConfig::SetEndpointVisibility`, verified to work unelevated) with the
  default-microphone rules above.
- Live camera usage alerts with the sensor activity monitor.
- Lock/unlock timeline in the Activity window; optional auto-lock on Win+L or sleep.

**Later**
- Code signing (free signing for open-source projects), so SmartScreen stops warning.
- An optional "Require admin approval to unlock".
- Real-device behaviour still worth watching across Windows builds: whether a streaming camera vetoes the disable, and
  whether the camera usage registry keeps updating.

---

## 11. Honest limits

- **Software can't beat malware with administrator rights.** Only a physical camera cover or a hardware microphone switch
  is certain.
- Malware running as *you* can fake the hotkey or unlock (also through the helper's pipe). You'll see it (on-screen flash,
  tray icon, lock timeline) and an unlock from the command line or another program always plays the sound and leaves a
  sticky red popup.
- **ASIO and kernel-streaming apps** bypass mute (and endpoint disable). Only mic-only hardware gets a true hardware disable;
  combined headsets and onboard audio can't be hardware-disabled without killing the speakers.
- Software mute can be bypassed by exclusive-mode apps on mics without hardware mute.
- A device plugged in while locked is usable for a fraction of a second before it's locked.
- A camera in use may refuse to be disabled until the app releases it; BlindsDown says so instead of pretending, and keeps trying.
- Without the helper, cameras are not locked (only reported).
- Hotkeys can be swallowed by some elevated or fullscreen apps; use the tray click.
- Usage alerts can't identify the website inside a browser, and can't see ASIO or kernel-streaming capture.
- A mic unplugged at unlock is only restored if it comes back while BlindsDown runs.
- The lock is machine-wide: other Windows users on the same PC are affected too.
- The exe isn't code-signed yet, so SmartScreen warns on first run.
- Windows 10 works, but .NET 10 officially supports only its LTSC/Enterprise editions, and consumer Windows 10 is out of
  support.
