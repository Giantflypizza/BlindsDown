# Testing BlindsDown

Two kinds of tests: **automated tests** that anyone can run in a minute, and a **manual checklist** for the
things only a person at a real PC can check (hotkeys over other apps, a real Discord call, plugging in devices).

## Automated tests

From the repository folder (the one containing `BlindsDown.slnx`):

```powershell
dotnet build BlindsDown.slnx
dotnet test BlindsDown.slnx
```

What runs:

- **Logic tests** (`tests/BlindsDown.Tests`): the lock engines with fake microphones and cameras (crash at every step,
  devices unplugged or added while locked, restore report, journal recovery), settings, hotkey parsing,
  usage history. They never touch real devices and always run.
- **No-network scan** (`tests/BlindsDown.Tests/NoNetwork`): reads the compiled BlindsDown.dll and
  BlindsDown.Core.dll (without running them) and fails on any networking reference, any native DLL outside
  the allowlist, any COM class outside the allowlist, any `Process.Start` outside `SafeShell`, any way of
  loading or calling code by name that the scan couldn't follow (reflection, `dynamic`, the Visual Basic
  runtime), or any third-party package. It also proves the scanner works by scanning Microsoft's own
  networking assemblies and checking that it finds what they do. Run just this part with:

  ```powershell
  dotnet test BlindsDown.slnx --filter "FullyQualifiedName~BlindsDown.Tests.NoNetwork"
  ```

- **Real-device tests (opt-in, read-only).** Tests that look at your real microphones and Windows UI are
  skipped unless you ask for them. They only **read**: list microphones, read names and mute state, list
  which apps are capturing. They never mute, unmute, disable or change anything. To run them:

  ```powershell
  $env:BLINDSDOWN_DEVICE_TESTS = "1"
  dotnet test BlindsDown.slnx
  Remove-Item Env:BLINDSDOWN_DEVICE_TESTS
  ```

  (Command Prompt: `set BLINDSDOWN_DEVICE_TESTS=1`, then `dotnet test BlindsDown.slnx`.)

- **Pictures for a visual check (opt-in).** With `BLINDSDOWN_QA_DIR` set to a folder, the tray icon, badge and
  on-screen confirmation are drawn into PNG files there, including `readme-demo.png`, the picture at the top of
  the README (copy it to `docs/images/demo.png` when the look changes). With `BLINDSDOWN_DEVICE_TESTS=1`, every
  window and popup is also drawn into `artifacts\qa`.

To test exactly like CI does (the Release build is what ships; these are the commands in
`.github/workflows/ci.yml`):

```powershell
dotnet restore BlindsDown.slnx
dotnet build BlindsDown.slnx -c Release --no-restore
dotnet test BlindsDown.slnx -c Release --no-build --filter "FullyQualifiedName~BlindsDown.Tests.NoNetwork" --logger "console;verbosity=normal"
dotnet test BlindsDown.slnx -c Release --no-build --filter "FullyQualifiedName!~BlindsDown.Tests.NoNetwork" --logger "console;verbosity=normal"
```

Package versions are pinned exactly in the project files and Dependabot proposes updates. There are no NuGet
lock files: they broke on every .NET patch release because of a package the SDK adds by itself.

## Manual test checklist (microphones)

Do this before every release, on a real PC, with the exe you are about to release
(`dotnet publish src/BlindsDown/BlindsDown.csproj -c Release -r win-x64 -o publish/win-x64`).

Command-line steps below are for PowerShell, opened in the folder that contains BlindsDown.exe (for example
`publish\win-x64`). BlindsDown does command-line work in the background, so PowerShell returns straight away
and a file it writes appears a moment later.

### Before you start

- Close BlindsDown if it is running. To start completely fresh, rename `%LocalAppData%\BlindsDown` to
  `BlindsDown.bak` (rename it back afterwards).
- **Write down the current state of every microphone.** Press Win+R, type `mmsys.cpl`, open the
  **Recording** tab. For each device note its name, its status (Ready / Disabled / Not plugged in) and,
  under Properties > **Levels**, whether it is muted. Take a screenshot of each Levels tab. You need this
  "before" list for tests 15 and 16.
- **Also save a snapshot** right after test 1 (any command-line run creates `%LocalAppData%\BlindsDown`, and
  test 1 needs it to be missing): `.\BlindsDown.exe --snapshot start.txt`. It only reads. Test 23 and
  "After testing" compare against it.
- Make sure at least one microphone is **muted on purpose** before you start (for test 12), and that you
  have a second microphone you can plug in (USB mic, webcam with a mic, or a headset) for tests 10 and 11.
- Have Discord (or any voice app) and a second person or a second device to listen.

Expected results are written after the arrow (→). Tick each box when it passes; note anything else in the release notes or an issue.

- [ ] **1. First run.** Start `BlindsDown.exe` with no `%LocalAppData%\BlindsDown` folder.
  → A welcome window explains what BlindsDown does, shows the hotkey (Ctrl+Alt+Shift+M), says it never
  connects to the internet, and explains pinning the tray icon. The pin button opens Settings >
  Personalization > Taskbar. "Start BlindsDown when I sign in to Windows" can be ticked. After closing it, the
  BlindsDown icon is in the tray (or under the **^** arrow). Starting `BlindsDown.exe` a second time does not
  open a second copy.
- [ ] **2. Hotkey toggles.** Click into Notepad and press Ctrl+Alt+Shift+M.
  → Falling tone, a big "Microphones locked" message in the middle of the screen, red "MIC LOCKED" badge,
  tray icon changes shape (blinds down) and colour. In `mmsys.cpl` > Recording every microphone shows muted
  in Levels. Press the hotkey again → rising tone, "Unlocked", badge disappears, restore report popup.
- [ ] **3. Hotkey over an elevated window.** Open Task Manager (it runs as administrator), click into it, press
  the hotkey. → Either the lock toggles as in test 2, **or** nothing happens at all (Windows can block
  hotkeys over admin windows). It must never half-toggle. Note which one happened. Then click the tray
  icon → toggles as in test 2.
- [ ] **4. Tray click.** Left-click the tray icon → toggles lock exactly like the hotkey. Right-click → menu with
  Lock/Unlock (showing the hotkey), Activity..., Settings..., About BlindsDown, Exit. About shows the version,
  "BlindsDown never connects to the internet" and the repository address as selectable text, not a link.
- [ ] **5. Discord call.** Join a Discord voice call with another person (or your phone) and talk → they hear
  you. Lock → within a second they can't hear you, and Discord's own mic indicator stops reacting. Unlock →
  they hear you again, without restarting Discord.
- [ ] **6. Indicator on every monitor.** With more than one monitor (ideally with different scaling, e.g. 100%
  and 125%), lock. → A crisp, readable badge in the chosen corner of **every** monitor; amber
  "PARTIALLY LOCKED" only if a mic couldn't be muted. Clicking on the badge clicks the window under it.
  The badge is not in Alt+Tab or on the taskbar and never takes keyboard focus. Change the corner and turn on
  "screen edges" in Settings → updates on all monitors. Unplug or rearrange a monitor while locked → badges
  follow.
- [ ] **7. Hidden from screen shares.** With "Hide BlindsDown from screen sharing and recordings" on (the
  default), share your screen in Discord or Teams and start a screen recording (for example with Snipping
  Tool's Record button). Lock (the big on-screen message appears), unmute a mic as in test 9 so that a popup
  appears, then take a screenshot (Win+Shift+S). → The badge, the big on-screen message and BlindsDown's
  popups are visible to you but absent from the screenshot, the share and the recording. Turn the option
  off → they appear in all three.
- [ ] **8. Sounds.** Lock and unlock → short falling tone on lock, rising tone on unlock, at a comfortable
  volume. Turn "Play a sound when locking and unlocking" off in Settings → hotkey and tray toggles are
  silent (an unlock from the command line still plays the tone: see test 25). Turn it back on.
- [ ] **9. Unmute while locked.** Lock. In `mmsys.cpl` > Recording > a mic > Levels, click the speaker icon to
  unmute it. → Within about a second it is muted again, and a red alert popup says it was re-muted (it stays
  until closed). Unmute it 5 or more times within a minute → it is re-muted **every** time (BlindsDown never
  gives up), and a red "Something keeps undoing the microphone lock" popup ("Something keeps unmuting ...")
  appears and stays until closed. Further unmutes update that one popup instead of stacking new ones.
- [ ] **10. Plug in a new mic while locked.** Lock. Plug in a USB mic or webcam. → Within a second its
  microphone appears in `mmsys.cpl` already muted, with an alert naming it. Unlock → it goes back to how it
  arrived (normally unmuted) and the restore report mentions it.
- [ ] **11. Unplug while locked.** Lock. Unplug a microphone. Unlock → the report says it was not connected and
  will be restored when it comes back. With BlindsDown still running, plug it back in → it is restored
  (unmuted if it was unmuted before the lock) without you doing anything, and
  `%LocalAppData%\BlindsDown\pending-restores.json` is gone.
- [ ] **12. Already muted stays muted.** With a mic that you muted yourself before locking: lock, unlock. → It is
  still muted. The report counts it as left as it was.
- [ ] **13. Devices and exclusions.**
  - **a.** Unlocked, open Settings > Devices. → Every microphone shows whether it has hardware mute or
  software mute only. Lock and open it again → it still shows this for every microphone.
  - **b.** Unlocked, exclude one microphone (it is not possible while locked, and the window says why).
  Lock → that mic stays unmuted and the badge still says locked. While locked, start using that mic in an app
  (for example Voice Recorder or Discord's mic test) → a red popup warns that this microphone is not
  protected. Unlock → nothing changed for it. Remove the exclusion afterwards.
  - **c.** If you have two identical microphones (two of the same USB mic, or two of the same Bluetooth
  headset): exclude only one of them and lock. → Only the excluded one stays unmuted; the other is muted.
  Settings > Devices shows exactly one of them as excluded. Remove the exclusion afterwards.
- [ ] **14. Killed while locked.** Lock. In Task Manager, right-click BlindsDown > **End task**. → Microphones stay
  muted. Start BlindsDown again → a recovery window explains it closed unexpectedly while locked and offers
  **Restore my devices** (default) and **Keep locked**.
  - **a.** Choose **Keep locked** → locked, badge shown, mics muted; unlocking later restores normally.
  - **b.** Repeat the kill and choose **Restore my devices** → every mic is back to its "before" state and a
  restore report is shown.
- [ ] **15. Restore report is correct.** Lock, then while locked change one thing yourself (for example unplug a
  mic). Unlock. Compare every mic in `mmsys.cpl` with your "before" list. → Every mic BlindsDown muted is
  back to its before state; the report says exactly that, and names anything it deliberately left alone or
  couldn't restore. Nothing the report calls "restored exactly" differs from the before list.
- [ ] **16. Exit while locked.** Lock, then tray menu > Exit. → A question with **Unlock and exit**, **Exit and stay
  locked** and **Cancel**. Cancel → still running and locked. Exit and stay locked → app closes, mics stay
  muted; starting it again continues the lock without a crash-recovery question. Unlock and exit → mics
  restored (compare with the before list), app closes.
  - **a.** Lock, unplug a microphone, then Exit > **Unlock and exit**. → Before closing, BlindsDown shows that
  the unplugged mic could not be restored yet and offers to keep running until it is plugged back in.
  Choose to keep running and plug the mic back in → it is restored. (If you let BlindsDown close instead,
  the mic is only restored the next time BlindsDown runs while it is plugged in.)
- [ ] **17. Restart while locked.** With "Start BlindsDown when I sign in to Windows" on, lock, then restart
  Windows. → After signing in, BlindsDown starts by itself, the badge shows locked, all mics are still muted.
  Unlock → restored as usual.
  - **a.** Task Manager > Startup apps shows BlindsDown. Disable it there, then open BlindsDown Settings. →
  Settings says that start with Windows was turned off in Task Manager and has a button that opens that
  page. Click OK in Settings → Task Manager still shows BlindsDown as disabled (BlindsDown never overrides
  it). Enable it again in Task Manager.
- [ ] **18. Usage alerts.** With BlindsDown unlocked:
  - **a.** Join a Discord call → popup "Discord is using your microphone". If Discord uses the Sonar mic, the
  popup body reads "<device>, a virtual microphone from SteelSeries Sonar". Leave → no further popup unless
  "Also show a popup when it stops" is on.
  - **b.** SteelSeries Sonar (or any app that keeps the mic open all the time) → one popup when it starts, not a
  stream of them.
  - **c.** A website that uses the microphone in Edge, Chrome or Firefox (and Brave, Opera, Vivaldi or Arc if
  installed) → the popup names the browser and adds "Windows doesn't say which website."
  - **d.** Start using the mic in an app, then lock. → A single info popup lists the apps that had a microphone
  open and says they now only hear silence. Apps you always allow are left out of the list.
  - **e.** While locked, start using a microphone in an app (Voice Recorder, or join a Discord call). → A popup
  says the app opened your microphone and only hears silence (with a note that exclusive-mode apps can get
  around a software-only mute, if that mic has only software mute). It stays until closed. Do the same with
  an app you always allow, and again with "Show a popup when an app starts using a microphone" turned off
  in Settings → a popup still appears every time. Stop and start the same app on the same mic a few times →
  still one popup, updated, not a stack.
  - **f.** Camera: unlocked, open the Camera app. → Within a second or two a popup says the Camera app is using
  your camera, and it shows up in Activity. If it takes 10 to 20 seconds instead, the live camera monitor isn't
  available on this Windows and BlindsDown fell back to Windows' usage records (`blindsdown.log` says so); note
  the Windows build (Win+R, `winver`). Test 44 checks the live monitor in detail.
- [ ] **19. Always allow.** Click **Always allow** on a popup for an app. → While unlocked, no more popups for that
  app; it still appears in Activity history (while locked it still gets popups: see 18e). Settings > Alerts
  lists it; **Remove** brings its popups back.
- [ ] **20. Activity window.** Tray > Activity. → Shows who is using a microphone right now (with how long) and
  recent history grouped by day, in local time, with durations ("Discord used your microphone 14:02–14:47").
  Camera use from 18f is listed too. Short gaps are merged into one entry. Approximate entries (from before
  BlindsDown ran) are marked "≈".
- [ ] **21. Settings are kept.** Change the hotkey (try one that is already taken: Settings must say so), the
  badge corner, sounds and history retention. Exit and start BlindsDown again. → Every setting is as you
  left it and the new hotkey works. `%LocalAppData%\BlindsDown\settings.json` exists. "Clear history" empties
  the Activity history.
- [ ] **22. Hotkey with and after Settings.** Open Settings and click **Cancel**. Press the hotkey → the lock
  toggles. Press it again to undo. Open Settings and click **OK** without changing anything. Press the hotkey
  → the lock toggles. Press it again to undo.
  - **a.** Open Settings, then click into Notepad so Settings is in the background. Press the hotkey → the lock
  toggles while Settings stays open. Press it again to undo.
  - **b.** In Settings, click the hotkey box and press the current hotkey → the box shows that combination and
  the lock does **not** toggle. Press another combination → the box shows it. Click **Cancel**.
  - **c.** With a popup that stays on screen (for example the red one from test 9), open Settings, and then
  About from the tray menu. → The popup's close button and its action buttons still work while either window
  is open.
- [ ] **23. Exact restore, checked with snapshots.** This is the gold-standard restore check. Unlocked, run
  `.\BlindsDown.exe --snapshot before.txt`. Lock with the hotkey, then unlock. Run
  `.\BlindsDown.exe --snapshot after.txt`, wait a moment, then run `fc.exe before.txt after.txt` (in
  PowerShell plain `fc` means something else; in Command Prompt `fc` works). → The only difference is the
  "Taken:" line. Also compare `start.txt` with `after.txt` the same way → only "Taken:" differs, unless you
  plugged in or unplugged devices since.
- [ ] **24. History pruning.** With history on, exit BlindsDown. Open `%LocalAppData%\BlindsDown\history.jsonl` in
  Notepad, copy one line, paste it as a new last line, and in that copy change the year in both `"start"` and
  `"end"` to last year. Save. Start BlindsDown. → The copied old entry is gone from `history.jsonl` and does
  not appear in Activity; the other entries are still there. Then turn history off in Settings > History and
  click OK. → BlindsDown says it no longer records which apps use your devices, that locks and unlocks are
  still recorded in the lock timeline, and asks whether to also delete the history and timeline you already
  have. Choose to keep it → Activity still shows it. Lock and unlock → the lock timeline shows the new lock,
  but no new app use is recorded. "Keep entries for" stays editable while history is off. Turn history back on.
- [ ] **25. Unlocked by another program.** Turn sounds off in Settings. Lock with the hotkey. Run
  `.\BlindsDown.exe --unlock`. → BlindsDown unlocks, the unlock tone plays even though sounds are off, and a
  red "Unlocked by another program" popup appears and stays until you close it. Lock again and run
  `.\BlindsDown.exe --restore` → the same. Run `.\BlindsDown.exe --lock`, then `.\BlindsDown.exe --toggle` →
  locks, then unlocks with the same tone and red popup. `%LocalAppData%\BlindsDown\blindsdown.log` has a line
  for every one of these locks and unlocks. Turn sounds back on.
- [ ] **26. Emergency restore.** Lock, then tray menu > Exit > **Exit and stay locked**. Unzip the release zip again
  into a new, different folder and run `.\BlindsDown.exe --restore` from there. → A window lists what it
  restored, and every mic is back to its "before" state. Run it again → it says there is nothing to restore.
- [ ] **27. Self-test.** Exit BlindsDown (the self-test doesn't run while the tray app is running). Run
  `.\BlindsDown.exe --selftest selftest.txt`. → Your mics are muted for about a second. After a few seconds
  `selftest.txt` exists and ends with "OK: every microphone and default device is exactly as before." and
  "RESULT: PASS".
- [ ] **28. No network activity.** Open Resource Monitor (Win+R, `resmon`) > **Network** and keep it open during
  tests 2 to 27. → BlindsDown.exe never appears under "Processes with Network Activity", "Network Activity" or
  "TCP Connections". In PowerShell, `Get-NetTCPConnection -OwningProcess (Get-Process BlindsDown).Id` and
  `Get-NetUDPEndpoint -OwningProcess (Get-Process BlindsDown).Id` find nothing. No red "networking code
  loaded" alert ever appears.

## Manual test checklist (cameras, helper, stronger microphone lock)

Do these after the microphone list, with a USB webcam you can plug in and out (ideally one with a built-in
microphone). You need administrator rights for the helper's install and removal. Take
`.\BlindsDown.exe --snapshot start2.txt` first: it lists microphones **and** cameras.

- [ ] **29. Camera offer.** With the helper not installed and an existing settings file, start BlindsDown. → One
  "New: lock your cameras too" popup with **Set up**, shown only on this start. Fresh start (no settings folder) →
  the welcome window has an unchecked "Also lock my cameras" instead.
- [ ] **30. Install the helper.** Settings > Camera > **Turn on camera lock…** → an explanation, then one Windows
  administrator prompt. Say **No** → nothing changes and Settings says so. Try again and say **Yes** → after a few
  seconds Settings > Camera says the camera lock is on, with the helper's version. Check: `services.msc` lists
  "BlindsDown camera helper" (Automatic, Running); `C:\Program Files\BlindsDown\BlindsDown.exe` exists;
  Settings > Apps > Installed apps lists "BlindsDown camera helper".
- [ ] **31. Camera lock.** Open the Windows Camera app (unlocked) → picture. Close it. Lock → the badge says
  **MIC + CAMERA LOCKED** and the confirmation counts the camera. Open the Camera app → no camera found (or an
  error). Device Manager > Cameras shows the webcam as disabled. Tray menu > Exit → the question says "Your
  microphones and cameras are locked." and that the camera helper keeps the cameras off; click **Cancel**.
  Unlock → the restore report includes the camera, the Camera app shows a picture again, Device Manager shows it
  enabled.
- [ ] **32. Camera in use while locking.** Start the Camera app, then lock. → Either the camera turns off and the
  app loses the picture, or the lock is amber **PARTIALLY LOCKED** with "in use by an app; BlindsDown keeps
  trying"; then close the Camera app → within a few seconds the lock turns red. Unlock → everything back.
- [ ] **33. Camera turned back on while locked.** Lock. In Device Manager, right-click the webcam > **Enable
  device**. → Within a few seconds it is disabled again and a red "Device turned back on while locked" popup
  appears. Unlock → enabled.
- [ ] **34. Camera plugged in while locked.** Lock with the webcam unplugged, then plug it in. → It is turned off
  within a second or two with a "New device locked" popup. Unplug it while still locked, plug it back in → turned
  off again. Unlock → it works.
- [ ] **35. Infrared and virtual cameras.** On a PC with Windows Hello face sign-in: lock → face sign-in still
  works (the IR camera is left alone) and Settings > Devices says so. Tick "Also lock infrared cameras", lock →
  face sign-in is unavailable until you unlock. Same idea for a virtual camera (OBS, Phone Link) with "Also lock
  virtual cameras".
- [ ] **36. Camera exclusion.** Settings > Devices: exclude the webcam. Lock → it stays on and Settings shows it
  as excluded; using it while locked gives a red "camera you excluded" popup. Remove the exclusion.
- [ ] **37. Restart while locked (helper).** Lock, then Start > Power > **Restart** (not Shut down: with Fast
  Startup that keeps the devices as they are). → Before signing in the webcam is already disabled again (Device
  Manager after sign-in shows it disabled; `%ProgramData%\BlindsDown\helper.log` shows the lock was re-applied).
  After sign-in BlindsDown says it is still locked. Unlock → everything back.
- [ ] **38. Helper stopped while locked.** Lock. In `services.msc` stop "BlindsDown camera helper". → Within a few
  seconds the badge turns amber and Settings > Camera says the helper isn't answering; the webcam stays disabled.
  Start the service again → red again. Unlock → the webcam is back.
  - **a.** Lock, stop the service, then unlock. → The microphones unlock; a red popup says the cameras may still
  be off and does **not** promise that a restart fixes it. The tray menu offers **Lock microphones** (what a click
  does now) and its status line says the helper isn't answering and may still have the cameras off. Start the
  service again and wait until Settings > Camera says the helper answers → the tray says only cameras are locked.
  Unlock (tray click) → the webcam is back.
- [ ] **39. Unlock command while the app runs.** Lock. In PowerShell (not as administrator) run
  `.\BlindsDown.exe --restore` from a second copy of BlindsDown while the tray app keeps running → it is handed to
  the running app as an unlock command: "Unlocked by another program" (tone and red popup) and both microphones
  and cameras are back.
  - **a. Cameras unlocked through the helper by another program.** Turn sounds off. Lock. In PowerShell (not as
  administrator) send the helper an unlock the way any program could:
    ```powershell
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'BlindsDown.Helper', [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(5000); $pipe.ReadMode = 'Message'
    $id = [guid]::NewGuid().ToString('N')
    $request = [Text.Encoding]::UTF8.GetBytes("{`"v`":1,`"command`":`"Unlock`",`"source`":`"manual test`",`"requestId`":`"$id`"}")
    $pipe.Write($request, 0, $request.Length)
    $answer = [byte[]]::new(65536); $length = $pipe.Read($answer, 0, $answer.Length); $pipe.Dispose()
    [Text.Encoding]::UTF8.GetString($answer, 0, $length)
    ```
    → Within two seconds: the unlock tone (sounds are off), a red "Unlocked by another program" popup naming the
    webcam with a **Lock again** button, and the tray status says the cameras aren't locked. Activity's timeline
    keeps the lock going, marked with a warning. Click **Lock again** → the webcam is off again and the popup closes.
  - **b. While BlindsDown is closed.** Lock, tray menu > **Exit** > keep locked. Run the snippet above, then start
  BlindsDown → "Still locked" and a red popup that starts "While BlindsDown wasn't running" with the time of the
  unlock, saying BlindsDown locked the cameras again; the webcam is off.
  - **c. Your own changes are never "another program".** Lock and unlock with the hotkey a few times, run
  `.\BlindsDown.exe --restore` with the tray app closed and then start it, and remove the helper from Settings while
  locked (first part of step 45) → no "Unlocked by another program" or "Lock changed by another program" popup.
- [ ] **40. Stronger microphone lock.** Settings > Devices > tick "Turn microphones off in Windows while locked".
  With a voice app using a physical mic, lock. → In `mmsys.cpl` > Recording (right-click > Show Disabled Devices)
  every physical mic is **Disabled**; virtual ones (Sonar, Voicemeeter) are only muted; your default microphone
  is still the default (or, if a mic is excluded, the default mics are only muted and the restore says nothing
  changed). Unlock → every mic is Ready again, unmuted as before, and the default microphones are the same as
  before (`--snapshot` before and after: only "Taken:" differs). Untick the option afterwards if you don't want it.
- [ ] **41. Microphone hardware (helper).** Settings > Camera > tick "Also turn off microphone-only hardware".
  With the webcam (which has a microphone) plugged in, lock. → Device Manager shows the webcam's audio device
  disabled; a USB headset with headphones is **not** disabled (only muted). Unlock → the webcam microphone is back
  and listed in Sound settings, unmuted as before; the defaults are unchanged.
- [ ] **42. Automatic lock.** Settings > General: tick "Lock when I lock Windows". Press Win+L and sign in again →
  locked, and Activity's timeline says "automatic". Same for "Lock when the PC goes to sleep" (Start > Power >
  Sleep, then wake it). Unlocking is never automatic.
- [ ] **43. Lock timeline.** Activity shows each lock and unlock with time, how (hotkey, tray, other program,
  automatic, exit, or startup when you chose **Restore my devices** after a crash) and a warning mark when
  something wasn't exact. Hovering over a row shows the whole story in words (for example "unlocked by another
  program"). **Clear history** in Settings clears it too.
- [ ] **44. Live camera usage.** Unlocked, open the Camera app → within a second "Camera is using your camera"
  (no 10-second delay) naming the webcam; Activity shows it under "Using now".
- [ ] **45. Remove the helper.** While locked: Settings > Camera > **Remove helper…** → confirmation, one
  administrator prompt. → The webcam is turned back on first; the service, `C:\Program Files\BlindsDown` (or it is
  emptied at the next restart), `%ProgramData%\BlindsDown` and the Installed apps entry are gone; Settings > Camera
  offers **Turn on camera lock** again. Install again, lock, then remove it from Settings > Apps > Installed apps >
  "BlindsDown camera helper" > Uninstall while BlindsDown runs → same result, and BlindsDown shows a red "Camera
  helper removed" or "Unlocked by another program" popup (the removal didn't come from it). Install the helper
  again, lock, choose Exit > **Exit and stay locked**, remove the helper from Installed apps, then start
  BlindsDown → a red "Camera helper removed" popup that says it happened while BlindsDown wasn't running.
- [ ] **46. Snapshots match.** With the same devices plugged in as at the start: `.\BlindsDown.exe --snapshot
  end2.txt`, then `fc.exe start2.txt end2.txt` → only "Taken:" differs.

### After testing

- Compare every microphone in `mmsys.cpl` with your "before" list one last time: everything must match.
  Better still, with the same devices plugged in as at the start, run `.\BlindsDown.exe --snapshot end.txt`,
  wait a moment, then run `fc.exe start.txt end.txt`: only the "Taken:" line may differ.
- Restore your `%LocalAppData%\BlindsDown.bak` folder if you renamed it.
