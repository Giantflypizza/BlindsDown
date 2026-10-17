# BlindsDown design

How BlindsDown works, why it is built this way, and what comes next. For users, the [README](../README.md) is the better
starting point; for testing, see [TESTING.md](TESTING.md).

---

## 1. Summary

- **A small tray app (C#, .NET 10, WinForms)** with a hotkey that toggles a privacy lock.
- **Microphones are locked in layers:**
  1. **Mute.** No admin needed. *(Stage 1, available now.)*
  2. **Windows' own endpoint disable**, the switch behind Sound settings → "Don't allow". *(Stage 2a.)*
  3. **True hardware disable**, used only for devices that are *only* a microphone, such as a webcam's built-in mic or a
     USB mic. *(Stage 2b.)*

  The layering exists because disabling a microphone's hardware device would often take the speakers or headphones with
  it: a USB headset's microphone and headphones are usually one USB function.
- **Cameras will be disabled at the PnP device level** by a small **helper Windows service**, installed once with a normal
  installer (one admin prompt at install, none afterwards). *(Stage 2b.)*
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
| Disable every microphone in Device Manager | Mute (Stage 1), then Windows' **endpoint disable** for every mic; a **hardware disable** only when the hardware is mic-only | Disabling a mic's node under "Audio inputs and outputs" is reported **not to stop apps recording** (Microsoft Q&A, 2018 and 2024). Disabling the parent hardware also kills speakers and headphones on combined devices. The endpoint disable used by Sound settings does block apps. |
| Read "who uses the mic" from Windows' usage registry | **Primary:** audio sessions (mic) and `IMFSensorActivityMonitor` (camera). **Fallback:** the usage registry | Several open-source projects reported in 2026 that Windows 11 builds 26220/26300 stopped updating the registry, behind a gradual feature flag. The live APIs are documented, name the process, and need no admin. |
| Run the whole app elevated | **Unelevated tray app + helper service** (Stage 2b). The portable exe works without admin for microphones | See section 5. An always-elevated tray breaks under Windows 11's *Administrator protection* (it reads the wrong user's data), can't autostart from the Run key, and loses its tray icon when Explorer restarts. |
| Lock every device | Defaults: IR/Windows Hello cameras **excluded**, virtual cameras **excluded**, virtual mics **muted, not disabled**; all changeable | Locking the physical mic already silences virtual microphone chains (e.g. an app → a virtual mixer such as SteelSeries Sonar or Voicemeeter → the real mic). Disabling the virtual mic itself can make the mixer software re-route or change defaults. |
| Offer to restore after a crash | Also: a write-ahead journal, a `--restore` command, non-persistent camera disable (a reboot always brings cameras back if the app is gone), and the service re-applying the lock at boot | A crash, a deleted exe or a failed uninstall must never leave a device broken. |

Other features that follow from these choices:
- **Restore report** after every unlock: device state compared against the pre-lock snapshot.
- **Per-device lock strength** in the UI: "hardware mute", "software mute (exclusive-mode apps can bypass)", and later
  "endpoint disabled", "hardware disabled", "NOT locked: in use by …".
- **"Always allow" list for alerts.** Some apps (virtual audio mixers) hold the mic open all the time; without this they
  would alert forever.
- **Virtual-chain attribution:** "Discord (via SteelSeries Sonar)", not just "SteelSeries Sonar".
- **Optional auto-lock** on Win+L or sleep (planned, off by default).

---

## 3. Key research findings

✅ = checked against Microsoft documentation, source code, or a read-only probe on a Windows 11 25H2 PC.
🧪 = still needs a hands-on test.

| Topic | Finding | Verified |
|---|---|---|
| Mic endpoints | Every mic is an `SWD\MMDEVAPI\{0.0.1.00000000}.{guid}` node. Its `Parent` is the real hardware, and the endpoint property `{b3f8fa53-0004-438e-9003-51a46e139bfc},2` names the hardware in every state. | ✅ |
| Device Manager mic disable | Disabling the `SWD\MMDEVAPI` node only marks it disabled; apps keep recording (two independent Microsoft Q&A reports). | ✅ reports, 🧪 on 25H2 |
| Sound-settings disable | Undocumented `IPolicyConfig::SetEndpointVisibility`. CLSID `870AF99C-…` and IID `F8679F50-…` have been stable since Windows 10 1607; EarTrumpet and SoundSwitch use them. The state persists in HKLM. | ✅ interface, 🧪 whether it needs admin |
| Already-open streams | Microsoft's docs say an endpoint disable hides the device but **doesn't cut streams that are already open**. So: mute first, then disable, then verify that no capture session is still active. | ✅ |
| Software mute | On mics without hardware mute (`QueryHardwareSupport` lacks bit `0x2`), **exclusive-mode apps bypass mute**. Many USB headsets have hardware mute; virtual mics usually don't. | ✅ |
| Bypass paths | ASIO and direct kernel-streaming (WDM-KS) capture bypass endpoint disable and mute. Only a hardware disable stops them. | ✅ inferred from docs, 🧪 |
| Default devices | Windows picks a new default when the default mic is disabled. It can be restored with `IPolicyConfig::SetDefaultEndpoint`, only if it actually changed. | ✅ API, 🧪 behaviour |
| PnP disable | `CM_Disable_DevNode` **without** `CM_DISABLE_PERSIST` lasts only until reboot, so a reboot is a safety net. A device in use can veto the disable or need a restart, so **every disable is re-checked** (`CM_Get_DevNode_Status` → problem 22). Admin is required. x64/ARM64 only: 32-bit callers fail with ERROR_IN_WOW64. | ✅ |
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
│ Mic layer:  mute (IAudioEndpointVolume) + endpoint disable (IPolicyConfig, 2a)   │
│ Guards: re-mute / re-disable if anything changes while locked (+ alert)          │
│ Usage monitor: audio sessions + camera sensor monitor (2a) + usage registry      │
│ History (local JSONL)  •  Settings  •  Exclusions  •  Alert popups               │
└──────────────┬───────────────────────────────────────────────────────────────────┘
               │ named pipe, local-only ACL, tiny fixed command set (Stage 2b)
┌──────────────▼──────── BlindsDown.exe --service (LocalSystem helper, 2b) ────────┐
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
  - `--snapshot [file]`: dumps every microphone's state to a text file for before/after comparison (read-only).
  - `--selftest [file]`: end-to-end check on the real microphones: lock, verify every mic is muted, unlock, verify the
    snapshot is identical to before.
  - Stage 2b: `--service`, `--install-service` / `--uninstall-service` (called by the installer).
- **Without the service** (portable zip), the app locks microphones only. With the service, it is the single authority for
  the PnP layer; each layer restores only what *it* changed.

### 4.1 Lock algorithm

**Lock:**
1. Register device notifications *before* enumerating, so nothing slips through.
2. **Snapshot** every capture endpoint (state, mute, hardware-mute support, hardware parent, physical/virtual), the three
   default-mic roles and, later, every camera node.
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
  within a minute it raises a red "Something keeps unmuting…" alert, and further fights update that alert instead of
  stacking new ones.
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
| App starts and finds a journal from a crash | It asks: "Restore my devices" or "Keep locked". Meanwhile the devices stay protected. |
| Windows signs out or shuts down while locked | The lock simply continues at the next start (mute persists in Windows). |
| App deleted while locked | Re-download it and run `--restore`, or unmute in Sound settings. From Stage 2b a reboot also restores cameras. |
| Uninstall (Stage 2b) | The installer runs restore first and aborts if restore fails. |

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
| Distribution | Portable exe | Portable exe | Portable (risky if exe is user-writable) | **Installer** (Inno Setup) + portable zip |

**Honest security note.** With A, C or D, malware running as *you* could unlock, for example by faking the hotkey or sending
"Unlock". Windows offers no way to fully stop that. Mitigations: every unlock flashes on screen and is logged; an unlock
from the command line or another program always plays the sound and leaves a sticky red popup; a lock/unlock timeline in
Activity is planned; an optional "Require admin approval to unlock" may follow.

**Service hardening (Stage 2b):** installed under Program Files; pipe ACL denies network access and grants only interactive
users read/write; clients require a SYSTEM-owned pipe (no squatting); message size limits and strict JSON parsing; the
service picks targets itself from a class allowlist (it can't be abused to disable a disk or network card); journal folder
ACL checked on every start; no `OnCustomCommand`; trimmed privileges.

---

## 6. Usage alerts and history

**Sources:**
- **Microphone:** audio sessions on every capture endpoint, event-driven on a dedicated background thread, plus a 2 s safety
  poll. Only *active* sessions count; PID 0 and system sounds are ignored.
- **Camera:** `IMFSensorActivityMonitor` *(Stage 2a)*. Until then, camera alerts come only from the usage registry (best
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
or cleared.

---

## 7. User interface

- **Tray icon:** drawn at the exact pixel size for the taskbar's DPI; light glyph on a dark taskbar; a different *shape* when
  locked, not just colour; a left click toggles the lock.
- **Hotkey:** default **Ctrl+Alt+Shift+M** (no Win key, which Windows reserves; Shift alone with a letter is rejected because
  it would steal capital letters). Configurable with a capture box that tests the combination for real; F13–F24 work for
  macro pads. Some elevated or fullscreen apps swallow application hotkeys, so every toggle is confirmed on screen.
- **Lock indicator:** a red "MIC LOCKED" pill in a screen corner (optionally thin red edges) on **every monitor**,
  click-through, never taking focus, correct on mixed-DPI setups. Hidden from screen sharing and recordings by default,
  together with the on-screen flash and the popups. Amber when only partially locked.
- **Sounds:** two short generated tones, falling for lock and rising for unlock. No audio files, nothing loaded from paths.
- **Windows:** Settings (hotkey, sounds, autostart, indicator, devices and exclusions, alerts allowlist, history), Activity,
  About. Settings shows each mic's hardware/software mute capability and notices when autostart was turned off in Task
  Manager. Dark mode follows Windows 11.

---

## 8. "Never connects to the internet": enforced, not promised

1. **No third-party runtime packages.** (Stage 2b adds only Microsoft's own `System.ServiceProcess.ServiceController`.)
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
- **Hand-written COM and P/Invoke** (Core Audio, later CfgMgr32): no NAudio, so the code stays small and auditable.
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

**Stage 2a – stronger mic lock, live camera monitoring (no admin)**
- Endpoint disable (`IPolicyConfig::SetEndpointVisibility`) as the main mic lock, with default-mic restore, after a
  careful test of whether it needs admin, how it treats live streams, and how virtual mixers react.
- Live camera usage alerts with the sensor activity monitor.
- Lock/unlock timeline in the Activity window; optional auto-lock on Win+L or sleep.

**Stage 2b – camera lock (helper service + installer)**
- LocalSystem helper service: camera disable, mic-only hardware disable, new-device enforcement, boot re-lock, `--restore`
  extended to the service's changes.
- Inno Setup installer that runs restore before uninstall; the release workflow builds it.
- Device-disable code tested first on a throwaway virtual test device (`SwDeviceCreate`), then on a real webcam.

**Open questions for Stage 2:** Does `SetEndpointVisibility` need admin for a standard user? Does a camera disable get vetoed
while an app is streaming? Does Windows restore the default mic by itself after re-enabling it? Does the camera usage
registry still update on current builds?

---

## 11. Honest limits

- **Software can't beat malware with administrator rights.** Only a physical camera cover or a hardware microphone switch
  is certain.
- Malware running as *you* can fake the hotkey or unlock. You'll see it (on-screen flash, tray icon, log) and an unlock from
  the command line or another program always plays the sound and leaves a sticky red popup.
- **ASIO and kernel-streaming apps** bypass mute (and endpoint disable). Only mic-only hardware gets a true hardware disable;
  combined headsets and onboard audio can't be hardware-disabled without killing the speakers.
- Software mute can be bypassed by exclusive-mode apps on mics without hardware mute.
- A device plugged in while locked is usable for a fraction of a second before it's locked.
- A camera in use may refuse to be disabled until the app releases it; BlindsDown will say so instead of pretending.
- Hotkeys can be swallowed by some elevated or fullscreen apps; use the tray click.
- Usage alerts can't identify the website inside a browser, and can't see ASIO or kernel-streaming capture.
- A mic unplugged at unlock is only restored if it comes back while BlindsDown runs.
- The lock is machine-wide: other Windows users on the same PC are affected too.
- The exe isn't code-signed yet, so SmartScreen warns on first run.
- Windows 10 works, but .NET 10 officially supports only its LTSC/Enterprise editions, and consumer Windows 10 is out of
  support.
