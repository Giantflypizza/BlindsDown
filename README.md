# BlindsDown

**One hotkey locks every microphone and camera on your Windows PC, keeps them locked, and puts them back exactly as they were. It never connects to the internet.**

![BlindsDown while locked: the red "MIC + CAMERA LOCKED" badge in the corner, the on-screen confirmation "Microphones and cameras locked" in the middle, and two popups in the bottom-right corner saying that Zoom Workplace opened the microphone but only hears silence, and tried the camera but gets no picture](docs/images/demo.png)

*What you see while locked: the badge, the confirmation after pressing the hotkey, and a popup for every app that tries a locked microphone or camera. Drawn by BlindsDown's own rendering code.*

## What it does

BlindsDown is a small app that lives in the notification area (the system tray). Press
**Ctrl+Alt+Shift+M** and every microphone is muted: built-in, USB, Bluetooth, and virtual ones such as
SteelSeries Sonar or Voicemeeter. With the optional camera helper, your cameras are turned off too. While the
lock is on, BlindsDown watches: if anything unmutes a mic or turns a camera back on, or a new one is plugged
in, it is locked again straight away and you get an alert. Press the hotkey again and everything goes back to
exactly how it was before, and BlindsDown tells you so.

It also shows you, live, which app is using your microphone or camera ("Discord is using your microphone"),
and keeps a private history on your PC of who used them and when you locked and unlocked.

## Features

- **Hotkey lock:** one key combination (default Ctrl+Alt+Shift+M) locks every microphone, including virtual ones. You can change the hotkey, click the tray icon instead, or use the [command line](#command-line).
- **Camera lock (optional, one admin prompt):** turns every camera off at the device level, so no app can use it. It needs a small helper service, which you can turn on in the welcome window or in Settings > Camera. Windows asks for administrator permission once. Without it, BlindsDown still tells you when an app uses your camera. See [The camera helper](#the-camera-helper).
- **Guard:** while locked, a mic that gets unmuted or a camera that gets turned back on (by an app, by you, by Windows) is locked again immediately, with an alert. It never gives up. If something keeps doing it, a red warning tells you so.
- **New devices are locked on arrival:** plug in a USB mic or webcam while locked and it is locked as soon as Windows sees it.
- **Exact restore with a restore report:** unlocking only undoes what BlindsDown changed. A mic you had muted yourself stays muted, a camera you had disabled stays disabled, and your default microphones stay your defaults. Afterwards you see a short report, e.g. "All 5 devices restored exactly as before."
- **Crash recovery and emergency restore:** every change is written down before it is made. If BlindsDown is closed unexpectedly while locked, it asks on the next start whether to keep the lock or restore your devices. `BlindsDown.exe --restore` puts everything back, even after you deleted BlindsDown and downloaded it again. And if the camera helper is ever removed (or can't start at all), restarting the PC turns every camera back on; while it is installed, a restart keeps a lock you left on.
- **Live "which app uses my mic or camera" alerts:** a popup when an app starts using a microphone or camera, straight from Windows' audio and camera systems, including apps hiding behind virtual devices (Discord using the SteelSeries Sonar virtual microphone). "Always allow" silences apps you trust while unlocked. While locked, every app that tries shows a popup, so nothing listens or watches unnoticed.
- **Stronger microphone lock (optional):** besides muting, turn microphones off in Windows (the Sound settings "Don't allow" switch), so apps can't open them at all. No admin rights needed. Off by default, because apps that have a microphone open lose it, like unplugging it. With the camera helper you can also turn microphone-only hardware, like a webcam's microphone or a USB microphone, off at the device level.
- **Automatic lock (optional):** lock when you lock Windows (Win+L) or when the PC goes to sleep. Unlocking is always up to you.
- **Unmissable indicator and sounds:** a red "MIC LOCKED" (or "MIC + CAMERA LOCKED") badge on every monitor, a different tray icon (the blinds are down), a big on-screen confirmation, and a short tone on lock and unlock. By default the badge, the on-screen confirmation and BlindsDown's popups are hidden from screen sharing and recordings.
- **Honest protection labels:** Settings > Devices shows for every microphone and camera how it is protected, e.g. "Muted in hardware", "Muted in software", "Turned off in Windows", "Turned off (device disabled)", or why it isn't.
- **Exclusions:** leave chosen microphones or cameras out of the lock. Exclusions are per device: if you have two identical ones, excluding one leaves the other one locked. Infrared (Windows Hello) and virtual cameras are left out unless you include them.
- **Local history and lock timeline:** which app used your microphone or camera, and when, plus every lock and unlock (and how it was triggered). Stored only on your PC. Entries older than 30 days are deleted automatically (you can change that), and you can clear both. You can also turn off the history of app use; the lock timeline keeps recording locks and unlocks, so nothing can unlock without you seeing it.

## This app never connects to the internet

**BlindsDown has no network code at all.** No update checks, no telemetry, no crash reports, no ads,
no accounts. This is not just a promise; it is enforced, and you can check it yourself. The camera helper is
the same BlindsDown.exe running as a Windows service, so all of this covers it too.

How it is enforced:

1. **Compile-time ban.** Every API that can reach the network (all of `System.Net`, network pipe clients,
   web browser controls, image boxes that can load URLs, help viewers, sound players that accept URLs,
   starting other programs, creating COM objects by name) is listed in [`BannedSymbols.txt`](BannedSymbols.txt).
   Using one is a **build error**, so such code can't even be compiled.
2. **CI scan of the built program.** On every change, an automated test
   ([`tests/BlindsDown.Tests/NoNetwork`](tests/BlindsDown.Tests/NoNetwork)) reads the compiled program and
   fails if it references any networking code, calls into any Windows DLL outside a short list of
   non-networking system DLLs, creates any COM object outside a two-entry list, starts a program from
   anywhere except one audited place (which can only open Windows Settings pages or restart BlindsDown itself
   with administrator rights to install the helper), registers a Windows service anywhere except the one place
   that registers the helper (always as `BlindsDown.exe --service` from Program Files, never with a "run this
   program on failure" command), passes a computer name to a Windows function that could reach another PC,
   opens a pipe anywhere except the helper's local client, or uses tricks that would hide code from the scan
   (loading code or calling methods by name while running, or calling native functions from another assembly).
   Apart from you, only Windows starts BlindsDown.exe, in two fixed ways: the helper service (`--service`) and,
   if you choose it, at sign-in (`--autostart`). Those two registrations are checked by code review, not by
   the scan.
3. **Runtime tripwire.** If networking code is ever loaded into BlindsDown while it runs, it shows a red
   alert that stays until you close it, and writes it to the log file.
4. **No third-party code.** Everything in BlindsDown.exe is either this repository's code or Microsoft's
   .NET (the runtime, plus Microsoft's Windows service package for the helper). The CI scan checks that too.

Honest details:

- The downloadable BlindsDown.exe contains the whole .NET runtime so that you don't have to install
  anything, and that runtime includes .NET's own networking libraries. They are on disk inside the exe but
  BlindsDown never uses or loads them, which is exactly what points 1 to 3 check.
- The app and its helper talk to each other through a local "named pipe". Windows can in principle reach
  named pipes from other PCs, so the helper's pipe refuses remote connections twice over (a Windows flag that
  rejects them, and access rules that deny network users), and the app only ever opens the local one.
- Windows itself may contact Microsoft *about* BlindsDown.exe: SmartScreen checks a newly downloaded program
  the first time you run it, Microsoft Defender may look it up, and if it ever crashed, Windows Error
  Reporting could offer to send a report. Those connections are made by Windows' own programs (depending on
  your Windows privacy settings), not by BlindsDown.exe, so the checks below don't show them under
  BlindsDown.exe and a firewall rule for BlindsDown.exe doesn't affect them.

### Verify it yourself

You don't have to trust me. Any of these works:

- **Block it in Windows Firewall.** BlindsDown works exactly the same with all network access blocked.
  In PowerShell **as administrator**, with the real path to your BlindsDown.exe (in File Explorer,
  Shift+right-click BlindsDown.exe > **Copy as path**):

  ```powershell
  New-NetFirewallRule -DisplayName "Block BlindsDown" -Direction Outbound -Action Block -Program "C:\Users\YourName\AppData\Local\Programs\BlindsDown\BlindsDown.exe"
  ```

  If you use the camera helper, block its copy too:

  ```powershell
  New-NetFirewallRule -DisplayName "Block BlindsDown helper" -Direction Outbound -Action Block -Program "C:\Program Files\BlindsDown\BlindsDown.exe"
  ```

  To remove the rules later:

  ```powershell
  Remove-NetFirewallRule -DisplayName "Block BlindsDown", "Block BlindsDown helper"
  ```

- **Resource Monitor.** Press Win+R, type `resmon`, press Enter, open the **Network** tab. BlindsDown.exe
  never appears under "Processes with Network Activity" or "TCP Connections".
- **TCPView** (free, from Microsoft Sysinternals). Type `BlindsDown` in its filter box: no rows, ever.
- **PowerShell.** While BlindsDown runs, these print nothing (or "No matching ... objects found"):

  ```powershell
  Get-NetTCPConnection -OwningProcess (Get-Process BlindsDown).Id
  Get-NetUDPEndpoint -OwningProcess (Get-Process BlindsDown).Id
  ```

- **A note on file shares.** Files on network drives and shares (SMB) are fetched by Windows' own "System"
  process, not by the program that asked for them, so the per-process checks above would never show such
  traffic under BlindsDown.exe. That's why BlindsDown never opens network paths at all: for an app that
  runs from a network drive, it skips reading that app's icon and version details.
- **Check that your download is the real one.** Every release lists SHA-256 checksums in `SHA256SUMS.txt`.
  In PowerShell, in your Downloads folder:

  ```powershell
  Get-FileHash .\BlindsDown-1.0.0-win-x64.zip -Algorithm SHA256
  ```

  The hash must match the line for that file in `SHA256SUMS.txt` (use your version number).
- **Check that it was built from this source code.** Every release zip has a signed GitHub build
  attestation that proves it was built by this repository's release workflow, not uploaded by hand. With the
  [GitHub CLI](https://cli.github.com/) installed:

  ```powershell
  gh attestation verify .\BlindsDown-1.0.0-win-x64.zip --repo Giantflypizza/BlindsDown
  ```

- **Read and build the code yourself.** See [Build from source](#build-from-source). The rules above live in
  [`BannedSymbols.txt`](BannedSymbols.txt) and [`tests/BlindsDown.Tests/NoNetwork`](tests/BlindsDown.Tests/NoNetwork).

## Install

1. Open the [latest release](../../releases/latest) and download the zip for your PC:
   - `BlindsDown-<version>-win-x64.zip` for almost all Windows PCs (Intel or AMD).
   - `BlindsDown-<version>-win-arm64.zip` for Windows on ARM (for example Snapdragon laptops).
2. Unzip it into a folder you'll keep, for example `%LocalAppData%\Programs\BlindsDown` (paste that into
   the File Explorer address bar). Don't run it from inside the zip or from Downloads: the option to start
   BlindsDown when you sign in remembers where the exe is.
3. Double-click **BlindsDown.exe**. Nothing else needs to be installed; .NET is built in. No admin rights
   needed, except once if you want the camera lock.

**"Windows protected your PC" (SmartScreen).** BlindsDown is not code-signed yet (signing certificates cost
money; free signing for open-source projects is planned). Windows warns about any new unsigned app. Click
**More info**, then **Run anyway**. If you'd like to be sure first, check the download as described in
[Verify it yourself](#verify-it-yourself).

**Pin the tray icon (Windows 11).** Windows 11 hides new tray icons behind the **^** arrow. To keep the
BlindsDown icon visible: Settings > Personalization > Taskbar > **Other system tray icons** > turn
**BlindsDown** on. Or drag the icon from the **^** menu onto the taskbar. The welcome window has a button
that opens this page.

**Start with Windows.** Tick "Start BlindsDown when I sign in to Windows" in the welcome window or in
Settings (General > Sounds and startup). If you turned BlindsDown off in Task Manager > Startup apps,
BlindsDown never overrides that: Settings tells you and has a button that opens that page, so you can turn it
back on there.

**Updating.** Exit BlindsDown (tray menu > **Exit**), replace BlindsDown.exe with the new one and start it.
If you use the camera helper, BlindsDown offers to update it too (one admin prompt). Delete old copies of
BlindsDown.exe: an older copy started by mistake would offer to put its own, older helper in place of the
newer one (Settings > Camera then says the helper is from a newer BlindsDown).

Requirements: Windows 11, x64 or ARM64. Windows 10 (version 1903 or later) works but is no longer supported by
Microsoft for most editions.

## The camera helper

Windows has no "mute" for cameras. The only reliable way to stop a camera is to turn the device off, the same
as Device Manager's **Disable device**, and that needs administrator rights. Asking for them every time you
press the hotkey would be useless, so BlindsDown can install a small helper once:

- **What it is:** the same BlindsDown.exe, copied to `C:\Program Files\BlindsDown` and registered as a Windows
  service named "BlindsDown camera helper". It runs in the background with system rights, has no window, and
  never connects to the internet.
- **What it can do:** only two things, asked by the BlindsDown app: lock (turn off cameras and, if you chose
  it, microphone-only hardware) and unlock (turn back on exactly what it turned off). It decides itself which
  devices those are, so no program can use it to turn off anything else.
- **How it keeps your devices safe:** the same rules as the microphone lock: it writes down every change
  before making it, never touches devices you disabled yourself, checks every change, and undoes exactly what
  it did. Its "off" is the kind Windows forgets at a restart: if the helper is ever removed or can't start at
  all, restarting the PC brings every camera back. (It has to be **Restart**: with Windows' Fast Startup, **Shut
  down** keeps devices as they were.) While the helper works and you were locked, it locks them again right
  after the restart, so the lock holds from before you sign in. So a restart doesn't unlock anything while
  the helper is installed; unlock in BlindsDown first, or remove the helper.
- **Turn it on:** in the welcome window ("Also lock my cameras"), or in Settings > Camera > **Turn on camera
  lock**. Windows asks for administrator permission once.
- **Remove it:** Settings > Camera > **Remove helper**, or Windows Settings > Apps > Installed apps >
  "BlindsDown camera helper" > Uninstall. Locked cameras are turned back on first.
- **Which cameras:** every normal webcam. Infrared cameras (used by Windows Hello face sign-in) and virtual
  cameras (OBS, Phone Link, NVIDIA Broadcast...) are left alone unless you include them in Settings > Camera.
  A camera that is in use by an app can sometimes refuse to turn off; BlindsDown then says so and keeps trying.

## Usage

| To | Do this |
|---|---|
| Lock all microphones (and cameras, with the helper) | Press **Ctrl+Alt+Shift+M**, or left-click the tray icon |
| Unlock | Press the hotkey again, or left-click the tray icon |
| See who is using the mic or camera, and the lock timeline | Right-click the tray icon > **Activity** |
| Change hotkey, indicator, sounds, camera lock, exclusions | Right-click the tray icon > **Settings** |
| Lock or unlock from a script or shortcut | See [Command line](#command-line) |

Every lock and unlock is confirmed by a big message in the middle of the screen and by the tray icon, and
with a short tone (falling = locked, rising = unlocked) unless you turned sounds off in Settings. Every lock
and unlock is also recorded in Activity's lock timeline.

**Unlocks that don't come from you.** If BlindsDown is unlocked from the command line or by another
program (not by your hotkey or tray click), it always plays the unlock tone, even with sounds off, and shows
a red "Unlocked by another program" popup that stays until you close it. The same goes for cameras unlocked
through the helper by anything other than BlindsDown, or locked less ("Lock changed by another program"): the
helper keeps a log of which request made each change, and only BlindsDown knows which requests are its own. So it
notices even an unlock and re-lock in quick succession, or one made while BlindsDown wasn't running (it tells you at
its next start, with the time it happened). While your microphones are still locked, the popup has a **Lock again**
button. So nothing can unlock your devices quietly.

What the indicator means:

| You see | Meaning |
|---|---|
| Red **MIC LOCKED** badge on every monitor, tray icon with the blinds down | Every microphone (except any you excluded) is locked, and BlindsDown checked it. Cameras are not locked (no helper, or you turned camera locking off). |
| Red **MIC + CAMERA LOCKED** badge | Microphones and cameras are locked. |
| Amber **PARTIALLY LOCKED** badge | At least one device could not be locked. The alert and Settings > Devices say which one and why. Don't rely on the lock until it's red. |
| No badge, tray icon with the blinds up | Unlocked. Your devices are as you left them. |

**Popups.** When an app starts using a microphone or a camera, you get a small popup in the bottom-right
corner that never steals focus from what you're doing. Click **Always allow** for apps you trust (no more
popups for them while unlocked; they still appear in the history), or turn popups off in Settings. For
browsers (Edge, Chrome, Firefox, Brave, Opera, Vivaldi, Arc) the popup adds "Windows doesn't say which
website."

**While locked, popups work differently,** so nothing listens or watches unnoticed:

- Every app that opens a microphone (or a camera, if you lock cameras) shows a popup, even apps you always
  allow and even if you turned popups off.
- The popup says whether that device is protected. If it is, the app "only hears silence" or "gets no
  picture" (with a note when a mic has only software mute, which apps using exclusive mode can get around).
  If the device is excluded, could not be locked, or is a camera without the helper, you get a red warning instead.
- These popups stay until you close them. If the same app opens the same device again, the popup that's
  already there is updated instead of a new one piling up.
- If apps already had a microphone or camera open when you locked, one popup lists them. Apps you always
  allow are left out of that list.

**Exiting while locked.** Tray menu > **Exit** asks whether to **Unlock and exit** or **Exit and stay
locked** (the microphone lock continues the next time BlindsDown starts; the camera helper keeps cameras off
by itself, also across a restart). If something can't be restored exactly when you unlock and exit (a mic
was unplugged, or unmuting it failed), BlindsDown shows you what happened before it closes and offers to keep
running until the mic is plugged back in. A mic that was unplugged when you unlocked is only restored while
BlindsDown is running. A mic that failed to unmute isn't retried: once BlindsDown is unlocked, clicking the
tray icon locks again, so unmute it yourself in Sound settings (see [If something looks wrong](#if-something-looks-wrong)).

## Command line

You can also control BlindsDown from PowerShell, a script or a shortcut. In PowerShell, go to the folder
with BlindsDown.exe and run it with one option, for example:

```powershell
cd "$env:LOCALAPPDATA\Programs\BlindsDown"
.\BlindsDown.exe --lock
```

| Option | What it does |
|---|---|
| `--lock`, `--unlock`, `--toggle` | Lock, unlock, or switch between the two, in the BlindsDown that is running. If BlindsDown isn't running, it starts and then does it. |
| `--restore` | Emergency restore: undoes every change BlindsDown recorded (and asks the camera helper to turn its devices back on) and shows you what it did. It works even with a freshly downloaded BlindsDown.exe, because the record lives in `%LocalAppData%\BlindsDown`, not next to the exe. If BlindsDown is running, this simply unlocks it. |
| `--snapshot [file]` | Writes the state of every microphone and camera to a text file. It only reads; nothing is changed. Handy for comparing before and after a lock. |
| `--selftest [file]` | Locks the microphones, checks that every one is really locked, unlocks, and checks that everything is exactly as before. It mutes your mics for about a second (cameras aren't touched). Close the tray app first (tray menu > **Exit**). Writes a report that ends with "RESULT: PASS" or "RESULT: FAIL". |

Without a file name, `--snapshot` and `--selftest` save their file in `%LocalAppData%\BlindsDown`.
BlindsDown does its work in the background, so the file appears a moment after PowerShell returns.

An unlock with `--unlock`, `--toggle` or `--restore` while BlindsDown is running counts as an unlock by
another program: tone and red popup, as described in [Usage](#usage).

## If something looks wrong

- **A microphone is still muted after unlocking.** If the restore report said it was not connected, plug it
  back in while BlindsDown is running: it is restored by itself. Otherwise, unmute it in Windows: press
  Win+R, type `mmsys.cpl` and press Enter (or Settings > System > Sound > **More sound settings**). Open the
  **Recording** tab, double-click the microphone, open **Levels** and click the crossed-out speaker icon next
  to the slider. The restore report after unlocking tells you if BlindsDown left something alone on purpose
  (for example because you changed it yourself while locked).
- **A microphone is still turned off after unlocking** (only with the stronger microphone lock). In the same
  **Recording** tab, right-click an empty area, tick **Show Disabled Devices**, then right-click the
  microphone > **Enable**. Or Settings > System > Sound > the microphone > **Allow**.
- **A camera is still off after unlocking.** First check that BlindsDown really is unlocked: no badge, and
  the tray icon shows the blinds up. If the badge says PARTIALLY LOCKED and the tray menu says "Only cameras
  are locked", the camera helper didn't answer the unlock and still holds its lock. Then a restart doesn't
  help, because the helper locks the cameras again when Windows starts. Unlock again (tray click or hotkey)
  once it answers, use **Repair** in Settings > Camera, or remove the helper. Once BlindsDown is unlocked,
  **restart** the PC (Restart, not Shut down): the helper's "off" never survives a restart. Or open Device
  Manager (right-click Start > **Device Manager**), find the camera under **Cameras**, right-click > **Enable
  device**.
- **BlindsDown closed or crashed while locked.** Start it again. It notices the unfinished lock and asks
  whether to **Restore my devices** or **Keep locked**.
- **BlindsDown is gone (deleted) and a mic is still muted.** Download it again, unzip it anywhere, open
  PowerShell in that folder and run `.\BlindsDown.exe --restore`. It finds its record in
  `%LocalAppData%\BlindsDown`, undoes exactly what it changed and shows you what it did. If you deleted that
  folder too, unmute the mic in Sound settings as described above.
- **You see "Unlocked by another program".** Something ran `BlindsDown.exe --unlock`, `--toggle` or
  `--restore`, or another program told BlindsDown (or its helper) to unlock. If that wasn't you (or a script
  or shortcut you set up), check what's running on your PC. Activity's lock timeline shows when it happened.
- **The camera helper isn't answering.** Settings > Camera offers **Repair** (one admin prompt). Restarting the
  PC also restarts the helper. If you were locked, it keeps your cameras locked after the restart; unlock
  once it answers again.
- **The hotkey does nothing.** Some fullscreen games and apps running as administrator swallow hotkeys.
  Click the tray icon instead. If another app already uses the same combination, Settings tells you; pick
  a different one. While you are entering a new hotkey in Settings (the hotkey box has the focus), pressing
  the current one puts it into the box instead of locking.
- **Logs for a bug report.** BlindsDown keeps a small log at `%LocalAppData%\BlindsDown\blindsdown.log`
  (paste `%LocalAppData%\BlindsDown` into the File Explorer address bar); the camera helper's log is in
  `%ProgramData%\BlindsDown`. They contain what BlindsDown did (including every lock and unlock), device
  names, app names and the paths of their exe files. Those paths usually include your Windows user name. They
  never contain keystrokes, window titles, audio or video. Please read them before attaching them to an issue.

## Limits

BlindsDown makes accidental and sneaky microphone and camera use much harder and much more visible. It is
not magic. Please read these:

- **Software can't beat malware with administrator rights.** Malware like that can undo anything any app does.
- **Malware running as you can unlock BlindsDown**, for example by faking the hotkey or asking the helper. You
  would still notice: every unlock shows the on-screen message and changes the tray icon, and plays the unlock
  tone if sounds are on. An unlock from the command line or another program always plays the tone and leaves
  a red popup until you close it. Every lock and unlock is recorded in the lock timeline.
- **Only a physical camera cover or a hardware microphone switch is certain.** If it really matters, use one.
- **Without the camera helper, cameras aren't locked.** BlindsDown still tells you when an app uses one.
- **A camera that an app is using can refuse to turn off.** BlindsDown then shows the lock as partial, names
  the camera and keeps trying until it works.
- **Software mute can be bypassed** by apps that open a microphone in "exclusive mode", on microphones that
  have no hardware mute. Settings > Devices shows which of your mics have only software mute. The stronger
  microphone lock closes this gap.
- **Pro-audio apps that use ASIO or kernel streaming** talk to the sound card directly and bypass mute (and
  Windows' "Don't allow"). Only turning the hardware off stops them: with the camera helper, choose "Also turn
  off microphone-only hardware" (headsets with speakers are never turned off, so they stay muted only).
- **A newly plugged-in microphone or camera is usable for a split second** before BlindsDown locks it.
- **A mic that was unplugged when you unlocked** is only restored if you plug it back in while BlindsDown
  is running. Otherwise unmute it in Sound settings.
- **Hotkeys can be swallowed** by some fullscreen games and apps running as administrator. Use the tray
  click; every toggle is confirmed by an on-screen message and the tray icon, so you always know whether it worked.
- **Browsers:** Windows only tells BlindsDown that, say, Microsoft Edge uses the mic or camera, not which
  website. The popup says so.
- **The lock affects all users of the PC.** Microphone mute and turning devices off are machine-wide in Windows.

## Build from source

You only need this if you want to check or change the code. No C# experience is needed to build it.

1. **Install the .NET 10 SDK** (Microsoft's free C# tools). In PowerShell:

   ```powershell
   winget install Microsoft.DotNet.SDK.10
   ```

   Close and reopen PowerShell afterwards.
2. **Get the code:** `git clone` this repository, or use the green **Code** button > **Download ZIP** and unzip it.
3. **Open PowerShell in the project folder** (the one containing `BlindsDown.slnx`) and run these commands:
   - `dotnet build` compiles everything (warnings count as errors).
   - `dotnet test` runs the automated tests, including the no-network scan.
   - `dotnet run` starts BlindsDown from the source code.

   ```powershell
   dotnet build BlindsDown.slnx
   dotnet test BlindsDown.slnx
   dotnet run --project src/BlindsDown
   ```

4. **Make your own BlindsDown.exe** (one self-contained file, like the release):

   ```powershell
   dotnet publish src/BlindsDown/BlindsDown.csproj -c Release -r win-x64 -o publish/win-x64
   ```

   The exe appears in `publish\win-x64`. Use `-r win-arm64` for ARM PCs.

New to C#? `src/BlindsDown.Core` is the plain logic (locking, restoring, history, the helper's lock) and is the
easiest place to start reading. `src/BlindsDown` is the Windows app around it, including the helper service in
`src/BlindsDown/Helper`. Any editor works; Visual Studio Code with the "C# Dev Kit" extension or the free
Visual Studio Community add debugging. The design is explained in [docs/DESIGN.md](docs/DESIGN.md), and
[docs/TESTING.md](docs/TESTING.md) explains the tests and the manual test checklist.

## Privacy of BlindsDown's own data

Everything the app stores is a plain file in `%LocalAppData%\BlindsDown` on your PC, inside your own
Windows user profile (other standard users of the PC can't read it; administrators can). Nothing is ever
sent anywhere (there is no code that could send it).

| File | What's in it |
|---|---|
| `settings.json` | Your settings: hotkey, indicator, camera options, excluded devices, always-allowed apps |
| `history.jsonl` | Which app used a microphone or camera, and when, with the app's name and exe path. Only if history is on. Entries older than the retention period (30 days by default) are deleted automatically when BlindsDown starts and every 6 hours. |
| `lock-events.jsonl` | The lock timeline: when you locked and unlocked, how (hotkey, tray, automatic...) and the result. Always recorded, even with history off, so nothing can unlock unseen. Same retention and "Clear history" as the history. |
| `blindsdown.log` (and `.log.old`) | What BlindsDown did, including every lock and unlock, for troubleshooting (about 1 MB at most). Contains device names, app names and exe paths. |
| `lock-journal.json` | Only while locked: what to restore on unlock |
| `pending-restores.json` | Only after an unlock while a microphone was unplugged: what to restore when it's plugged back in while BlindsDown runs. Deleted once that's done. |
| `helper-state.txt` | Only with the camera helper: how far BlindsDown has read the helper's change log, whether the helper holds your lock, and one-way fingerprints of BlindsDown's own requests, so an unlock by another program is noticed even after BlindsDown was closed. No device or personal data. |
| `.bak` and `.tmp` copies of the files above | Safety copies made while saving, so a crash or power cut can't damage them |
| `snapshot-*.txt`, `selftest-*.txt` | Only if you ran `--snapshot` or `--selftest` without a file name: your devices' names and states |

The camera helper keeps its own files in `%ProgramData%\BlindsDown`, readable only by administrators and the
system: `device-lock-journal.json` (only while locked: which devices it turned off), `lock-changes.json` (the
last 100 changes to its lock: when, which devices were turned back on and which it no longer covers, the name the
requesting program gave, and a fingerprint of its request; also the fingerprint and name of the last lock request until
the next unlock, and the highest number it has given an alert or change), `helper.log` and `setup.log` (what it did,
with device names). Removing the helper deletes them.

BlindsDown never records or listens to audio, and never sees a camera picture. It only sees *that* an app uses
a microphone or camera.

To delete your data: **Clear history** in Settings > History deletes the history and the lock timeline.
Turning history off stops recording which apps use your microphone or camera (locks and unlocks are still
recorded in the lock timeline) and asks whether to also delete the history and timeline you already have.
To remove everything:

1. Unlock.
2. If the last restore report said a microphone was not connected, plug it back in with BlindsDown running
   (or unmute it in Sound settings), so nothing is left muted.
3. If you use the camera helper: Settings > Camera > **Remove helper**.
4. Turn off "Start BlindsDown when I sign in to Windows" (Settings > General > Sounds and startup) and exit
   BlindsDown.
5. Delete the `%LocalAppData%\BlindsDown` folder and the folder where you put BlindsDown.exe.

## License

[MIT](LICENSE). Free to use, change and share. Security issues: see [SECURITY.md](SECURITY.md).
