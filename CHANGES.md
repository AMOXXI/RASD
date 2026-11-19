# RASD — Demo fixes (2026-11-16)

Baseline commit: **`6d4f111` "baseline before demo fixes"**. Every change below is its own commit on top of it.

| Commit | Item |
|---|---|
| `06278a5` | fix1 — Restart/Shutdown reboot loop and double execution |
| `d73c460` | fix2 — Launcher starts the relay when Node is already running |
| `f432e48` | fix3 — File monitoring works under the Windows service (+ Renamed event) |
| `4d6c7d0`, `6c97e14` | change5 — New logo everywhere (5b: small solid "R" for small sizes) |
| — | item 4 — Service recovery: verified, **no change** |

Build: VS 2022 Build Tools (MSBuild 17.14) + .NET Framework 4.8 / 4.8.1 targeting packs, README build order,
Release, `TargetFrameworkVersion=v4.8.1`. All 8 C# projects built with **0 errors**; the only 2 warnings
(CS1998 `NetworkClient.SendHttpHeartbeat`, CS0414 `SplashWindow._apiOptional`) exist in the baseline code and
were not introduced by these changes. Web client: Vite 5.4.21 on Node 26, 0 errors. `BUILD_PAYLOAD.ps1` passed
its stale-binary guard for all 7 EXEs. The new installer is `..\RASD_Setup.exe`; the previous one is kept as
`..\RASD_Setup_old.exe`.

---

## fix1 — Restart/Shutdown reboot loop and double execution

**Files:** `Client_Connector/RASD_Connector/NetworkClient.cs`, `Client_Connector/RASD_Connector/SystemController.cs`

**Problem.** The agent ran the command first and sent the HTTP ACK afterwards, asynchronously. `shutdown /t 0`
could take the machine down before the ACK landed, so the command stayed `PENDING` and came back in
`pending_commands` after boot (reboot loop). A command could also arrive twice: live via the relay and from
`pending_commands`.

**Change.**
- New execute-once record `C:\ProgramData\RASD\executed_cmds.txt` (last 200 cmdIds), loaded in
  `NetworkClient.Start()`, guarded by a lock.
- One helper, `HandleCommand`, is used by both the live path (`ProcessIncomingMessage`) and the queued path
  (`ExecutePendingCommands`; its JSON parsing is unchanged):
  - no cmdId → exactly the old behaviour;
  - cmdId already recorded → log `duplicate, skipped`, do **not** execute, still ACK;
  - new cmdId → add to the record and write the file **before** executing;
  - `RESTART`, `RESTART_NODE`, `SHUTDOWN`, `SHUTDOWN_NODE` → send the ACK and wait for it (max 5 s, enforced with
    `Task.Run(...).Wait(5000)` because the HTTP call blocks synchronously), then execute;
  - all other commands keep the old order (execute, then ACK).
- `SystemController.cs`: `shutdown /r /f /t 0` → `/t 5`, `shutdown /s /f /t 0` → `/t 5`.

**Side effect to expect:** a restart/shutdown now shows `DELIVERED` in the dashboard just *before* the client
goes down, not after it comes back.

## fix2 — Launcher skipped the relay when Node was already running

**File:** `RASD_Launcher/LoadingWindow.xaml.cs`

**Problem.** If `/api/health` answered, `RASD.exe` skipped launching `RASD_Relay.exe`. Node often outlives the
relay, so the dashboard opened without a relay and commands never reached agents.

**Change.** The launcher only skips when the API answers **and** a `RASD_Relay` process exists
(`IsRelayRunning()`). Otherwise it launches the relay exactly as before (`LaunchRelayProcess()`). Starting the
relay kills and restarts Node (`NetworkManager.Start()` → `RestartNodeApi()` → `KillOrphanedNode()`), so when the
API was already up the launcher first waits up to 15 s for the old API to go down (`WaitForApiDownAsync`), then
the existing 90 s wait for health. If the relay is already running, behaviour is unchanged. The launcher still
opens the browser as before.

## fix3 — File monitoring was dead under the Windows service

**File:** `Client_Connector/RASD_Connector/ForensicsMonitor.cs` (`Program.cs` unchanged — it already starts the monitor)

**Problem.** It watched `%UserProfile%\Downloads`. Under LocalSystem that is
`C:\Windows\System32\config\systemprofile\Downloads`, which does not exist, so nothing was ever watched. Its
`Console.WriteLine` logging is invisible in a service.

**Change.**
- One `FileSystemWatcher` per `C:\Users\<name>\Downloads` that exists. Skips `Default`, `Default User`, `Public`,
  `All Users` and any profile folder that is Hidden, System or a junction (ReparsePoint).
- Re-scans every 60 s (new first-time logons are picked up); watchers for deleted folders are removed.
- **Approved scope change:** the `Renamed` event uses the same handler as `Created`, so finished browser
  downloads (temp name → final name) are detected. Names ending in `.crdownload`, `.tmp`, `.partial`, `.part`
  (case-insensitive) and starting with `~` are ignored.
- Hashing retries up to 3 times, 1 s apart, if the file is still locked. MD5, the hash list and the API POST are
  unchanged.
- All logging goes to `C:\ProgramData\RASD\connector.log` through the existing `NetworkClient.Log`.

**Privacy note:** file names, paths and MD5 hashes from every local user's Downloads folder are reported to the
admin. Appropriate on lab machines you administer; say so in your report.

## Item 4 — Service recovery (verified only, no change)

`RASD_Setup/MainWindow.xaml.cs` runs
`sc failure "<service>" reset= 0 actions= restart/5000/restart/5000/restart/5000` for the connector and the
protector. This is valid `sc failure` syntax: every failure restarts the service after 5 s. Caveat (pre-existing,
unchanged): `RASD_Protector` is not a real Windows service (`ServiceBase`), so its recovery setting has no effect;
it runs as a child process of the connector.

## change5 — New logo everywhere

Source artwork: `..\RASD LOGO.jpg` (2048×2048). Assets were generated with System.Drawing (high-quality bicubic,
two-step downscale). The original artwork is untouched; the small-size variant is a solid bold Segoe UI "R" in
the artwork's gold (`#EEB059`) on its dark green (`#031E19`), same full-bleed square.

**New / replaced files**
- `Assets/rasd.ico` — 16, 20, 24, 32, 40 px = small "R"; 48, 64, 128 = artwork (classic 32-bit frames);
  256 = artwork (PNG-compressed). Old icon backed up as `Assets/_backup/rasd_old.ico` (not copied into the
  payload: `BUILD_PAYLOAD.ps1` copies `Assets\` without subfolders).
- `Assets/rasd-logo.png` (512, artwork) and `Assets/rasd-logo-small.png` (256, small "R") — WPF resources.
- `Web_Client/public/`: `favicon.ico` (16 & 32 small "R", 48 artwork), `favicon-32.png` (small "R"),
  `apple-touch-icon.png` (180, artwork), `rasd-logo-192.png`, `rasd-logo-512.png` (artwork),
  `rasd-logo-small.png` (256, small "R").

**References updated**
- `RASD_Setup.csproj`, `RASD_Uninstaller.csproj` — added `ApplicationIcon` (they had none). Admin_Watcher,
  ClientTray, Launcher and Shell already pointed at `..\Assets\rasd.ico` (new file, same path). Tray icons, the
  uninstall-list `DisplayIcon` and all shortcuts use the installed `assets\rasd.ico`, so they pick it up
  automatically.
- WPF marks (layout, text and sizes unchanged; only the mark replaced):
  - `RASD_Shell/MainWindow.xaml` title bar, 20 px, radius 6 → small "R" (`RASD_Shell.csproj` resource).
  - `Admin_Watcher/SplashWindow.xaml` relay splash, 60 px circle (radius 30) → artwork.
  - `RASD_Launcher/LoadingWindow.xaml` and `RASD_Setup/MainWindow.xaml` shield → 44×44, radius 10, artwork.
  - `Admin_Watcher.csproj` and `RASD_Launcher.csproj` gained the `rasd-logo.png` resource. (`RASD_Setup.csproj` was
  missed here; that caused the installer crash fixed at the end of this file.)
- React marks: `Sidebar.jsx` (32 px) and `LandingPage.jsx` (30 px) → `/rasd-logo-small.png`;
  `LoginPage.jsx` (44 px) → `/rasd-logo-192.png`. Container styles unchanged.
- `Web_Client/index.html` — favicon and apple-touch-icon links (title unchanged).
- Not changed (by agreement): design prototypes in `Web_Client/design/`, `CREATE_ICON.ps1` (only runs when
  `rasd.ico` is missing).

**Icon cache.** Windows caches shortcut and taskbar icons. After reinstalling, run:

```
ie4uinit.exe -show
```

If an old icon still shows, sign out and back in.

---

## How to test on the two laptops (admin 192.168.50.10, client 192.168.50.20)

Install `..\RASD_Setup.exe` as Administrator on both (Administrator role on .10, Endpoint User on .20).
Logs: client `C:\ProgramData\RASD\connector.log`; admin relay window / `C:\Program Files\RASD\Logs\RASD_errors.log`.

**1. Restart command (fix1).** Dashboard → Remote Admin (or the endpoint's page) → Restart.
- Client reboots **once**, about 5 s after the command; it re-appears online in the dashboard after boot.
- The command shows **DELIVERED**.
- `connector.log` shows `ACK sent for RESTART (cmdId …)` then `RESTART (cmdId …): ACK step finished — executing now.`
- No second reboot within 5 minutes. If the server re-sends it after boot, the log shows
  `already executed — duplicate, skipped.` and nothing happens.
- `C:\ProgramData\RASD\executed_cmds.txt` contains the cmdId.

**2. Shutdown command (fix1).** Same checks; the client shuts down once and stays off. After you power it on,
there is no second shutdown.

**3. Relay restart (fix2).** On the admin laptop: exit the relay from its tray icon (Exit), leaving Node
running (`http://localhost:3000` still answers). Launch `RASD.exe`.
- The launcher starts `RASD_Relay.exe`; the relay window shows Node being restarted; the launcher waits for the
  API, then opens the dashboard.
- Send Block USB / Allow USB to the client: it is executed and the command shows DELIVERED.

**4. File monitoring (fix3).**
- Restart the connector service on the client, or reboot it. `connector.log` shows
  `[FORENSICS] Monitor started …` and one `[FORENSICS] Watching: C:\Users\<name>\Downloads` line per profile.
- Copy a file into the logged-in user's Downloads, and also download one with Edge or Chrome.
- **Live check:** open that endpoint's page → **Forensics** tab; the event appears within seconds (it listens
  for live updates).
- The **Security** page does not update live — press F5 / re-open it to see the event.
- `connector.log` shows `New file detected`, `MD5`, `Reported to API: OK`.
- Known: Firefox first creates an empty placeholder with the final name, so one Firefox download can produce two
  events (empty file + finished file). Chrome and Edge produce one.

**5. Logo (change5).** After install + `ie4uinit.exe -show`: new icon on the desktop / Start-menu shortcuts,
taskbar, the relay and client tray icons, the installer exe, Apps & Features, the browser tab (favicon) and the
dashboard (sidebar, login, landing page); new mark in the relay splash, launcher, installer and dashboard
window's title bar.

---

## Rollback

```
git log --oneline
git checkout 6d4f111            # whole tree back to the baseline (detached HEAD)
git checkout master             # return to the fixed version
git revert <commit>             # undo a single item, e.g. git revert d73c460
```

Installer rollback: delete `..\RASD_Setup.exe` and rename `..\RASD_Setup_old.exe` back to `RASD_Setup.exe`.
A rolled-back source tree needs the build steps above (C# projects, `BUILD_PAYLOAD.ps1`, `RASD_Setup`) to
produce a matching installer.

---

## Known issues found during this work (NOT changed — outside the approved scope)

- **WebView2 "Install Missing" cannot find the bundled bootstrapper.** `Payload.zip` is created by Windows
  PowerShell with backslash entry names (`assets\MicrosoftEdgeWebview2Setup.exe`), but `Preflight.cs` looks it up
  as `assets/MicrosoftEdgeWebview2Setup.exe`, so the lookup fails (verified on the new payload). Normal
  extraction is unaffected. Windows 11 ships the WebView2 runtime, so the demo laptops are probably fine; check
  the installer's preflight list shows WebView2 as present on the admin laptop.
- The relay still sends every command to every connected agent (it ignores `nodeId`). Fine with one client;
  matters if the admin laptop also runs the client service.
- Other known limitations (unauthenticated agent-facing API routes, shared secrets in the installer, no TLS) are
  listed in the README's *Security considerations* and in `docs/INDUSTRY_COMPARISON.md` (section 3B).

---

# Credits removal and dashboard redesign (2026-11-16)

Tag **`pre-redesign`** (`52be587`) marks the state before these changes.

| Commit | Item |
|---|---|
| `7915332` | task1 — remove author credits |
| `dc22e88` | task2 — Liquid Glass dashboard redesign (presentation only) |

**task1.** Removed the "Developer" row from the dashboard Settings → About section, the "Published by" line from the
uninstaller banner, and the Apps & Features support URL. The Apps & Features **Publisher** is now `RASD`, and
`RASD_Launcher` AssemblyInfo Company/Copyright are `RASD` / `Copyright 2026 RASD`. README's Author row was removed.
(An existing install keeps the old Publisher value in Apps & Features until it is reinstalled.)

**task2.** `Web_Client` only; routes, API calls, socket code and data logic are unchanged.
- One token set in `src/index.css` (type, radii, spacing, three blur levels, elevation, motion) for dark and light.
- System font stack; the Google Fonts links were removed from `index.css` and `index.html` (works offline).
- New `src/components/PageHeader.jsx`: one large title per page that collapses into a compact title in the
  floating glass nav bar on scroll (`Layout.jsx`). The separate header title is gone.
- The nav bar's fill and blur fade in over the first 80 px of scroll; cards ease in on enter (transform + opacity,
  IntersectionObserver). Everything is disabled under "reduce motion".
- Floating glass sidebar with a clear active state and 18 px icons; glass login card; glass landing nav and hero.
- Emoji replaced with lucide icons; glows, the gradient text and the decorative landing blobs removed.
- At most three blur layers on screen (sidebar, nav bar, and a modal when one is open); cards are not blurred.

**Build.** `RASD_Launcher`, `RASD_Uninstaller`, the payload and `RASD_Setup` were rebuilt with 0 errors and 0
warnings. New installer: `..\RASD_Setup.exe`; the previous one is `..\RASD_Setup_prev.exe`.

**Rollback.** `git checkout pre-redesign` (or `git revert dc22e88` / `git revert 7915332` for one item), then rebuild.
Do **not** restore `..\RASD_Setup_prev.exe`: it has the installer crash described below.

---

# Fix: installer crashed on start (2026-11-16)

**Symptom.** `RASD_Setup.exe` closed immediately; Event Viewer showed Application Error 1000 (KERNELBASE.dll,
exception `0xe0434352`) and .NET Runtime 1026 with `System.IO.IOException`.

**Cause.** change5 put the logo on the installer's sign-in panel (`RASD_Setup/MainWindow.xaml`,
`pack://application:,,,/rasd-logo.png`) but `RASD_Setup.csproj` never received the matching `Resource` entry
(the change5 notes above wrongly said it did). WPF threw "Cannot locate resource 'rasd-logo.png'" while loading the
window. Every installer built since change5 had this bug, including `..\RASD_Setup_prev.exe`.

**Fix.** Added `<Resource Include="..\Assets\rasd-logo.png">` to `RASD_Setup.csproj` and rebuilt the installer
(0 errors, 0 warnings). Verified by resolving each window's logo URI inside its own exe: `RASD_Setup.exe`, `RASD.exe`,
`RASD_Relay.exe` (`rasd-logo.png`, 512×512) and `RASD_Shell.exe` (`rasd-logo-small.png`, 256×256) all resolve; the
crashing build reproduces the exact error. `..\RASD_Setup.exe` has been replaced with the fixed build.

---

# Reliability updates A1–A4 (2026-11-19)

Adopted from production systems; see `docs/INDUSTRY_COMPARISON.md` for the comparison and sources.

| Commit | Item | Change | Files |
|---|---|---|---|
| `77b737d` | **A1** | The relay forwards every command to every connected agent, so the agent now runs only commands whose `nodeId` is its own (other nodes' commands are logged as ignored; commands that arrive before the agent has registered are left for the queued path) | `Client_Connector/RASD_Connector/NetworkClient.cs` |
| `77b737d` | **A4** | Agent and relay keep an unfinished line between TCP reads (with a stateful UTF-8 decoder), so a message split across two reads is no longer lost; an unfinished line is capped at 65,536 characters | `NetworkClient.cs`, `Admin_Watcher/RelayEngine.cs` |
| `04116c6` | **A2** | Only one relay can run (named mutex `Global\RASD_Relay_SingleInstance`; a second copy shows "RASD Relay is already running" and exits). The launcher never starts a relay while one is running; if the API is down it waits for the running relay and tells you to press RESTART API | `Admin_Watcher/App.xaml.cs`, `RASD_Launcher/LoadingWindow.xaml.cs` |
| `a419ccf` | **A3** | When (re)starting the API, the relay stops only `node.exe` processes whose command line contains RASD's `API_Server\server.js` (looked up through WMI), instead of every Node process | `Admin_Watcher/NetworkManager.cs` |

**Verified.** All projects built with 0 errors; the only warnings are the two that already existed. A1 was tested
against the built agent (a no-op command type): a command for another node was ignored, a command before
registration was deferred, commands for its own node or with no target ran. A3: WMI returns full Node command lines
and an unrelated Node process is not matched. The installer's logo resource still resolves. A2 and A4 were reviewed
but not run (they need the elevated relay and a wired agent) — they are covered by the two-laptop tests below.

**Test on the laptops.**
- A1 — with the Endpoint connected, send Restart to the Endpoint only: only that laptop restarts. Its
  `connector.log` shows `COMMAND received: RESTART`.
- A2 — start `RASD.exe` twice, or open `RASD_Relay.exe` while the relay runs: the second copy says it is already
  running; `(Get-Process RASD_Relay).Count` stays `1`; the relay log has no `port 3001 … only one usage` error.
- A3 — run any other Node.js program on the Admin, press RESTART API: the other program keeps running.
- A4 — normal operation: every command sent shows in `connector.log` and gets DELIVERED.

**Important:** this installer build contains a new discovery key. Install **both** laptops from this same
`..\RASD_Setup.exe`.

**Rollback.** `git revert a419ccf 04116c6 77b737d`, then rebuild.
