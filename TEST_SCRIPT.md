# RASD — Manual Verification Script (runtime, unverified in code phase)

Steps **0, 2, 3** require a real install on real hardware and cannot be verified
from source. Run these before treating this as a validated submission build.
Record the proving log line for each. Logs: `C:\ProgramData\RASD\install.log`,
`C:\ProgramData\RASD\connector.log`, admin `C:\Program Files\RASD\Logs\RASD_errors.log`.

## Step 0 — Shell dashboard render (close the V13 gap)
1. Install as **Admin**. Ensure Node API + PostgreSQL are running (installer self-test reports this).
2. Admin Operations Center → **⧉ Open Dashboard** (or the "RASD Dashboard" shortcut).
3. In the WebView2 window: log in; open **Overview, Nodes, Alerts, Commands, Forensics**.
4. **PASS** = every view renders, zero console errors, and a node status change appears
   live (Socket.IO) **inside the shell**, not just in a browser.
   - Prove: relay log shows `[EIO RECV] 2 (server ping)` / `[EIO SENT] 3 (our pong)` with no drop.

## Step 2 — WebView2-runtime-missing path
1. On a machine without the WebView2 runtime (or rename the Evergreen client key
   `HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-...}` to simulate).
2. Launch `RASD_Shell.exe`.
3. **PASS** = the overlay ("WebView2 runtime required" + **Install WebView2 runtime**
   + **Open in browser instead**) appears — **never a blank window**. Clicking install
   runs the bundled `assets\MicrosoftEdgeWebview2Setup.exe`.

## Step 3 — Full regression, both roles
| Case | Steps | PASS proof (log line) |
|---|---|---|
| ADMIN | clean install → shell opens → dashboard loads → an agent appears | `install.log: SelfTest(ADMIN) -> PASS`; relay `OK: [TCP] Agent connected` |
| CLIENT | clean install → services auto-register → HMAC discovery → tray "Connected" → adapter "Ethernet" | `connector.log: [DISCOVERY] Authenticated admin reply … admin IP …`; `SelfTest(CLIENT) -> PASS` |
| END-TO-END | issue **Block USB** → row PENDING → agent executes → acks → row DELIVERED live | `connector.log: [NET] ACK sent … (cmdId …)`; dashboard row flips DELIVERED |
| OFFLINE QUEUE | stop agent → issue command → restart agent → command delivers from queue | `connector.log: [NET] QUEUED command delivered: …` |

## Preflight / auto-install (Part A) spot checks
- Run installer on a box missing a dependency → preflight shows ❌ and blocks **Continue**.
- **Install Missing** → winget used if present; else signed download. Confirm
  `install.log` shows `Authenticode OK … publisher CN 'OpenJS Foundation'|'Node.js'|'EnterpriseDB Corporation'`
  and **rejects** on any bad/absent/unexpected signature (never executes it).

## Uninstall residue (Part B)
- Run `RASD_Uninstaller.exe` → console prints `No residue detected.`
- Confirm gone: both services, task "RASD Endpoint Tray", all 6 firewall rules,
  `C:\ProgramData\RASD` (incl. **node.key**), `C:\Program Files\RASD`.
