# RASD — Network, API and Relay Setup Guide

A step-by-step guide to set up the two-laptop RASD lab so the API, the relay and the agent stay connected.
Every step says which laptop it is for:

- **Admin** — the administrator laptop (runs PostgreSQL, the Node.js API, the relay and the dashboard).
- **Endpoint User** — the managed laptop (runs only the RASD agent service and the tray app).

Run every PowerShell command in **PowerShell as Administrator**.

---

## 0. Why you see "API timeout"

The API itself is healthy: started on its own on the Admin laptop it connects to PostgreSQL and answers
`/api/health` in about 15 ms. The timeout comes from how the laptop is set up. These were found on the Admin laptop:

| # | Finding | Effect | Fixed in step |
|---|---|---|---|
| 1 | **No wired Ethernet adapter** — only Wi-Fi is connected | RASD refuses Wi-Fi by design; the relay logs `no wired Ethernet adapter is up` and agents can never connect | 2, 4 |
| 2 | **A VPN (ZoogVPN) is connected** | VPN tunnels can capture LAN traffic and drop sockets; RASD refuses VPN adapters | 4.2 |
| 3 | **Two relay windows were running at once** (`port 3001 already in use` in the relay log) | When a relay starts or restarts the API it stops **every** `node.exe`, so two relays keep killing each other's API → timeouts | 7.1 |
| 4 | **The Endpoint role was installed on the Admin laptop** (it failed half-way) | The two roles must never share a laptop | 1, 8 |

"Never disconnect" cannot be guaranteed on any network (a cable can be pulled, a laptop can sleep). What this
guide does is remove every avoidable cause, and RASD reconnects by itself within seconds when something does
drop (see section 6.3 for the exact timings).

---

## 1. Lab plan

| Item | Admin | Endpoint User |
|---|---|---|
| Role chosen in `RASD_Setup.exe` | **Administrator** | **Endpoint User** |
| IP address | `192.168.50.10` | `192.168.50.20` |
| Subnet mask / prefix | `255.255.255.0` (`/24`) | `255.255.255.0` (`/24`) |
| Default gateway / DNS | `192.168.50.1` (the router) | `192.168.50.1` (the router) |
| Connection | **Ethernet cable** to a router LAN port | **Ethernet cable** to a router LAN port |
| Wi-Fi / VPN | Off | Off |
| Runs | PostgreSQL 16, Node.js, relay, dashboard | Agent service + tray app only |
| Ports used | TCP 3000, TCP 3001, UDP 3002 (inbound) | UDP 3002 (inbound), outbound to Admin 3000/3001 |

Rules for both laptops:

- **Admin:** install only the Administrator role. Never install the Endpoint role on this laptop.
- **Endpoint User:** install only the Endpoint User role. Never install the Administrator role on this laptop.
- **Both:** install from the **same `RASD_Setup.exe` file**. Every installer build contains its own discovery key
  (`node.key`); if the two laptops were installed from different builds they will reject each other's discovery
  messages. If you rebuild the installer, reinstall **both** laptops from the new file.

---

## 2. Hardware

| Item | Admin | Endpoint User |
|---|---|---|
| Ethernet port | Built-in RJ-45, or a **USB-to-Ethernet adapter** if the laptop has none (the current Admin laptop has none) | Same |
| Cable | Cat5e or Cat6 to a router **LAN** port (not the WAN/Internet port) | Same |
| Power | Keep the charger connected during the demo | Same |

A plain unmanaged switch between the router and the laptops also works. Avoid "travel routers" in client or
repeater mode.

---

## 3. Router configuration

Do these once on the router's web page (usually `http://192.168.1.1` or `http://192.168.0.1`; the address and
password are on the router label).

### 3.1 LAN addressing — Admin and Endpoint User

| Setting | Value |
|---|---|
| LAN IP address | `192.168.50.1` |
| Subnet mask | `255.255.255.0` |
| DHCP server | On |
| DHCP pool | `192.168.50.100` – `192.168.50.199` (keeps .10 and .20 outside the pool) |
| DHCP lease time | 1 day or longer |

After changing the LAN IP, reconnect to the router at `http://192.168.50.1`.

### 3.2 Fixed addresses (DHCP reservation)

Reservations are the most reliable option: the laptops keep using DHCP, but the router always gives them the
same address. Find each laptop's MAC address with:

```powershell
Get-NetAdapter -Physical | Where-Object MediaType -eq '802.3' | Select-Object Name, MacAddress, Status
```

| Reservation | Admin | Endpoint User |
|---|---|---|
| MAC address | Admin's Ethernet MAC | Endpoint's Ethernet MAC |
| Reserved IP | `192.168.50.10` | `192.168.50.20` |

If your router has no reservation feature, use the static IP steps in 4.4 instead.

### 3.3 Settings that break RASD — turn them off

| Router setting (names vary by brand) | Set to | Why |
|---|---|---|
| AP isolation / Client isolation / Port isolation / "Guest network" for the LAN ports | **Off** | The laptops must be able to talk to each other directly |
| VLAN / port-based VLAN that separates LAN ports | **Off** (both ports in the same LAN) | Discovery uses broadcast, which does not cross VLANs |
| Broadcast storm control / broadcast filtering | **Off** (or a high threshold) | Discovery is a UDP broadcast on port 3002 |
| Green Ethernet / Energy-Efficient Ethernet on LAN ports | **Off** | Prevents link drops when traffic is idle |
| Port forwarding for 3000, 3001, 3002 | **None** | RASD is LAN-only; never expose these ports to the Internet |

### 3.4 Internet access

Internet is **not** needed for RASD to run. It is needed only for:

- **Admin:** the first install, if Node.js or PostgreSQL must be downloaded.
- **Endpoint User:** the *Update Windows* command (it downloads updates and the PSWindowsUpdate module).

---

## 4. Windows network settings on each laptop

### 4.1 Use the Ethernet cable, turn Wi-Fi off — Admin and Endpoint User

1. Plug the Ethernet cable in.
2. Turn Wi-Fi off (Quick Settings → Wi-Fi) or run:
   ```powershell
   Get-NetAdapter | Where-Object { $_.InterfaceDescription -match 'Wi-?Fi|Wireless|802\.11' } | Disable-NetAdapter -Confirm:$false
   ```
   (Re-enable later with `Enable-NetAdapter -Name "Wi-Fi"`.)

### 4.2 Disconnect every VPN — Admin and Endpoint User

1. Disconnect ZoogVPN (or any VPN) and turn off its "start with Windows" / "auto-connect" option.
2. Check that no tunnel adapter is up:
   ```powershell
   Get-NetAdapter | Where-Object Status -eq 'Up' | Select-Object Name, InterfaceDescription
   ```
   Only the Ethernet adapter should be listed as `Up`.

### 4.3 Check that RASD will accept the Ethernet adapter — Admin and Endpoint User

RASD refuses any adapter whose name or description contains: `virtual`, `vmware`, `hyper-v`, `vbox`,
`virtualbox`, `tap`, `tun`, `bluetooth`, `wi-fi`, `wifi`, `wireless`, `wwan`, `hotspot`, `loopback`, `pseudo`.

```powershell
Get-NetAdapter | Where-Object Status -eq 'Up' | Select-Object Name, InterfaceDescription, MediaType
```

- The Ethernet adapter must show `MediaType 802.3` and none of those words.
- If you see `vEthernet (...)` carrying the laptop's LAN IP, a Hyper-V virtual switch is bound to the network card.
  Remove that external virtual switch (Hyper-V Manager → Virtual Switch Manager) or use a separate USB-Ethernet
  adapter for RASD.

### 4.4 Static IP address (only if you did not use DHCP reservation in 3.2)

**Admin:**

```powershell
$nic = "Ethernet"   # the name shown by Get-NetAdapter
Set-NetIPInterface -InterfaceAlias $nic -Dhcp Disabled
Get-NetIPAddress -InterfaceAlias $nic -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false
Remove-NetRoute -InterfaceAlias $nic -DestinationPrefix 0.0.0.0/0 -Confirm:$false -ErrorAction SilentlyContinue
New-NetIPAddress -InterfaceAlias $nic -IPAddress 192.168.50.10 -PrefixLength 24 -DefaultGateway 192.168.50.1
Set-DnsClientServerAddress -InterfaceAlias $nic -ServerAddresses 192.168.50.1
```

**Endpoint User:** the same commands with `-IPAddress 192.168.50.20`.

To go back to automatic addressing: `Set-NetIPInterface -InterfaceAlias "Ethernet" -Dhcp Enabled` and
`Set-DnsClientServerAddress -InterfaceAlias "Ethernet" -ResetServerAddresses`.

### 4.5 Network profile = Private — Admin and Endpoint User

```powershell
Set-NetConnectionProfile -InterfaceAlias "Ethernet" -NetworkCategory Private
```

The RASD firewall rules work on every profile, but Private avoids other Windows features blocking LAN traffic.

### 4.6 Stop the network card from sleeping — Admin and Endpoint User

```powershell
$nic = "Ethernet"
Disable-NetAdapterPowerManagement -Name $nic
# Show power-saving options of this card, then disable each one that exists:
Get-NetAdapterAdvancedProperty -Name $nic | Where-Object DisplayName -match 'Energy|Green|EEE|Power Saving|Selective Suspend' |
  Select-Object DisplayName, DisplayValue
Get-NetAdapterAdvancedProperty -Name $nic | Where-Object DisplayName -match 'Energy|Green|EEE|Power Saving' |
  ForEach-Object { Set-NetAdapterAdvancedProperty -Name $nic -DisplayName $_.DisplayName -DisplayValue 'Disabled' -ErrorAction SilentlyContinue }
```

Also in Device Manager → Network adapters → your Ethernet adapter → Power Management: untick
**"Allow the computer to turn off this device to save power"**.

### 4.7 Never sleep while plugged in — Admin and Endpoint User

```powershell
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
powercfg /change disk-timeout-ac 0
# USB selective suspend off (important for USB-to-Ethernet adapters):
powercfg /setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0
powercfg /setactive SCHEME_CURRENT
```

Keep the lid open (or set *Closing the lid* → *Do nothing* when plugged in).

---

## 5. Firewall

The installer creates the rules. Check them:

```powershell
Get-NetFirewallRule -DisplayName "RASD*" | Select-Object DisplayName, Enabled, Direction, Action
```

| Expected rules | Admin | Endpoint User |
|---|---|---|
| `RASD API TCP In` (TCP 3000) | Yes | — |
| `RASD Relay TCP In` (TCP 3001) | Yes | — |
| `RASD Discovery UDP In` (UDP 3002) | Yes | Yes |
| `RASD Relay Program In` | Yes | — |
| `RASD Connector UDP In`, `RASD Connector Program In` | — | Yes |

All must show `Enabled True`, `Action Allow`. If a rule is missing, run the installer again for that role.
If you use a third-party antivirus with its own firewall (Kaspersky, ESET, Avast…), allow
`C:\Program Files\RASD\RASD_Relay.exe` and `node.exe` on the **Admin**, and
`C:\Program Files\RASD\RASD_Connector.exe` on the **Endpoint User**, or switch that firewall off for the demo.

Optional — allow ping so you can test the cable (Admin and Endpoint User):

```powershell
Enable-NetFirewallRule -DisplayName "File and Printer Sharing (Echo Request - ICMPv4-In)"
```

---

## 6. Node.js API configuration

### 6.1 Admin

**Node.js version.** Use a Node.js **LTS** release. The installer installs Node.js 20 LTS when Node.js is missing;
the API was also verified on the Node.js 26 already installed on the Admin laptop.

```powershell
node --version
```

**PostgreSQL must start with Windows:**

```powershell
Set-Service postgresql-x64-16 -StartupType Automatic
Start-Service postgresql-x64-16
Get-Service postgresql-x64-16
```

**API settings file** — `C:\Program Files\RASD\API_Server\.env` (edit with Notepad as Administrator; do not share
this file, it contains passwords):

| Key | Value | Notes |
|---|---|---|
| `PORT` | `3000` | **Do not change.** The agent, relay, launcher and dashboard all expect port 3000 |
| `DB_HOST` | `localhost` | PostgreSQL runs on the Admin laptop |
| `DB_PORT` | `5432` | |
| `DB_NAME` | `rasd_security` | Created by the installer |
| `DB_USER` | `postgres` | |
| `DB_PASS` | the PostgreSQL `postgres` password | The installer sets the `postgres` password to match this file. If you change one, change the other (`ALTER USER postgres WITH PASSWORD '...'` in psql) |
| `NODE_ENV` | `production` | |
| `JWT_SECRET` | a long random string | Changing it signs everyone out of the dashboard |

After editing `.env`, click **↺ RESTART API** in the relay window (do not start `node` yourself).

**Do not run Node by hand.** The relay starts the API and restarts it if it stops. Starting `node server.js`
manually, or a second relay, makes the relay stop it again (see 7.1).

**Check the API:**

```powershell
curl.exe -s http://127.0.0.1:3000/api/health
```

Expected: `"status":"OPERATIONAL","database":"CONNECTED"`. Use `127.0.0.1` rather than `localhost` in tests; it
answers faster on Windows.

### 6.2 Endpoint User

Nothing to install or configure: Node.js and PostgreSQL are **not** used on the Endpoint. The agent talks to the
Admin's API on `http://192.168.50.10:3000`. Check that it is reachable from the Endpoint:

```powershell
Test-NetConnection 192.168.50.10 -Port 3000
curl.exe -s http://192.168.50.10:3000/api/health
```

### 6.3 What keeps the connection alive (built in, no setting to change)

| Mechanism | Admin | Endpoint User |
|---|---|---|
| Real-time channel to the API | Relay ↔ API WebSocket: server ping every 25 s, 20 s timeout, reconnect after 1 → 30 s | — |
| Agent ↔ relay | Answers every PING with PONG; TCP keep-alive probe after 30 s idle, every 5 s | Sends PING every 20 s; reconnects if no PONG for 60 s; TCP keep-alive 30 s / 5 s |
| Heartbeat | Marks an endpoint offline after 100 s without a heartbeat | Heartbeat every 60 s over TCP and HTTP |
| Reconnect | Relay watchdog checks the API every 100 s and restarts it after 2 failed checks | Back-off 1, 2, 4, 8, 16, 30 s (±20 %); a cable plug-in triggers an immediate retry; re-discovers the Admin after 2 failed attempts |

### 6.4 What a short drop looks like

- **Endpoint User:** the tray shows *Disconnected*, then *Connected* again within about 30 s.
- **Admin:** the endpoint stays *Online* in the dashboard unless it is silent for more than 100 s.

---

## 7. Relay engine and agent

### 7.1 Admin — run exactly one relay

1. Start RASD only from the **RASD** desktop shortcut (`RASD.exe`). It starts the relay and the API.
2. Check that exactly one relay is running:
   ```powershell
   (Get-Process RASD_Relay -ErrorAction SilentlyContinue).Count
   ```
   The answer must be `1`. If it is `2` or more, close all of them and start once:
   ```powershell
   Get-Process RASD_Relay, RASD -ErrorAction SilentlyContinue | Stop-Process -Force
   Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force
   Start-Process "C:\Program Files\RASD\RASD.exe"
   ```
3. To hide the relay, use its **—** or **TRAY** button (keep **Minimize to tray** ticked). The tray icon's **Exit**
   menu stops the relay; open it again from the shortcut, not from a second window. Do not press
   **■ STOP ENGINE** during the demo.
4. Do not press **↺ RESTART API** repeatedly; one press is enough (the API is back in a few seconds).
5. **◎ SCAN NETWORK** cannot find endpoints (agents connect to the Admin; they do not listen on 3001). Use the
   dashboard's Endpoints page instead.

### 7.2 Admin — start automatically at sign-in

The relay needs administrator rights, and Windows does not start administrator apps from the normal "Run at
startup" list. Use a scheduled task instead, and turn the relay's own option off so it is not started twice:

1. In the relay window untick **Launch on Windows startup**.
2. Create the task:
   ```powershell
   schtasks /create /tn "RASD Admin" /tr "\"C:\Program Files\RASD\RASD.exe\"" /sc onlogon /rl highest /f
   ```
   Remove it later with `schtasks /delete /tn "RASD Admin" /f`.

### 7.3 Admin — what a healthy relay looks like

| Relay card / log line | Expected |
|---|---|
| DATABASE | `ONLINE` |
| API SERVER | `ONLINE` |
| LAN ADDRESS | `192.168.50.10` (if it shows a Wi-Fi or VPN address, redo section 4) |
| RELAY ENGINE | `LIVE (1 agents)` once the Endpoint is connected |
| Red banner `NO WIRED LAN — AGENTS CANNOT CONNECT` | Must **not** be shown |
| Log | `TCP agent listener started on 0.0.0.0:3001`, `DiscoveryResponder: Listening for agent probes`, `OK: [TCP] Agent connected from 192.168.50.20` |
| Log, every 25 s | `[EIO RECV] 2 (server ping)` followed by `[EIO SENT] 3 (our pong)` |

### 7.4 Endpoint User — the agent service

1. Check the service:
   ```powershell
   Get-Service RASD_Enterprise_Connector | Select-Object Name, Status, StartType
   ```
   Expected: `Running`, `Automatic`. Start it with `Start-Service RASD_Enterprise_Connector` if needed.
2. Pin the Admin address (optional, makes reconnects faster and skips discovery):
   ```powershell
   Set-Content -Path C:\ProgramData\RASD\admin_ip.txt -Value 192.168.50.10 -Encoding Ascii
   Restart-Service RASD_Enterprise_Connector
   ```
   If the pinned address stops working, the agent finds the Admin again by itself.
3. Open **RASD Endpoint Status** (tray icon): it must show **Connected**, Admin IP `192.168.50.10`, adapter
   `Ethernet`. **Reconnect now** forces a new connection after a network change.
4. Healthy lines in `C:\ProgramData\RASD\connector.log`:
   - `[DISCOVERY] Authenticated admin reply … admin IP 192.168.50.10` (or `Using cached/override admin IP`)
   - `[NET] CONNECTED local=192.168.50.20:… remote=192.168.50.10:3001 adapter=Ethernet`
   - `[NET] Registered with API. Node ID: …`
   - `[FORENSICS] Watching: C:\Users\<name>\Downloads`

---

## 8. Clean up the current Admin laptop (one time)

**Admin:**

1. Close everything RASD:
   ```powershell
   Get-Process RASD_Relay, RASD, RASD_Shell, node -ErrorAction SilentlyContinue | Stop-Process -Force
   ```
2. If the Endpoint role was ever attempted on this laptop, remove its leftovers:
   ```powershell
   sc.exe stop RASD_Enterprise_Connector; sc.exe delete RASD_Enterprise_Connector
   sc.exe stop RASD_Enterprise_Protector; sc.exe delete RASD_Enterprise_Protector
   schtasks /delete /tn "RASD Endpoint Tray" /f
   ```
   (Errors like "service does not exist" are fine.)
3. Run `RASD_Setup.exe` as Administrator and choose **Administrator** again, to repair the installation.
4. Do sections 2, 4, 5, 6.1 and 7.1–7.3.

**Endpoint User:** nothing to clean up; install it fresh with the Endpoint User role (section 9).

---

## 9. Start-up order for the demo

| Step | Admin | Endpoint User |
|---|---|---|
| 1 | Ethernet in, Wi-Fi and VPN off, charger in | Ethernet in, Wi-Fi and VPN off, charger in |
| 2 | Sign in; RASD starts (or open the **RASD** shortcut) | — (wait) |
| 3 | Wait until the relay shows DATABASE, API SERVER and RELAY ENGINE ready | — |
| 4 | Open **RASD Dashboard**, sign in | Turn on / sign in; the agent service starts by itself |
| 5 | The Endpoint appears as Online within ~10 s | Tray shows **Connected** |
| 6 | Run commands from **Remote Admin** | — |

---

## 10. Connection tests

**From the Endpoint User laptop:**

```powershell
ping 192.168.50.10                              # needs the ping rule from section 5
Test-NetConnection 192.168.50.10 -Port 3001     # TcpTestSucceeded : True
Test-NetConnection 192.168.50.10 -Port 3000     # TcpTestSucceeded : True
curl.exe -s http://192.168.50.10:3000/api/health
```

**From the Admin laptop:**

```powershell
ping 192.168.50.20
curl.exe -s http://127.0.0.1:3000/api/health
Get-NetTCPConnection -State Listen -LocalPort 3000,3001 | Select-Object LocalAddress, LocalPort
Get-NetTCPConnection -State Established -LocalPort 3001 | Select-Object RemoteAddress   # shows 192.168.50.20
```

---

## 11. Troubleshooting

| Symptom | Where | Check / fix |
|---|---|---|
| Launcher: *RASD Connector did not start within 90 seconds* (API timeout) | Admin | 1) `Get-Service postgresql-x64-16` is Running. 2) Only one relay (7.1). 3) `node --version` works. 4) `curl.exe http://127.0.0.1:3000/api/health`. 5) Relay log `C:\Program Files\RASD\Logs\RASD_errors.log` — look for `[NODE][ERR]` lines (wrong `DB_PASS` shows as a database login error) |
| Relay log: `port 3001 … only one usage of each socket address` | Admin | Two relays are running — section 7.1 |
| Red banner **NO WIRED LAN** | Admin | No usable Ethernet adapter — sections 2 and 4 |
| API timeouts every few minutes | Admin | Two relays, laptop sleeping, or a VPN — sections 7.1, 4.7, 4.2 |
| Tray: *No LAN — connect Ethernet cable* | Endpoint User | Sections 2, 4.1, 4.3 |
| Tray stays *Searching* | Endpoint User | Same installer build on both laptops (section 1); UDP 3002 firewall rule (section 5); router isolation / VLAN off (3.3); or pin the Admin IP (7.4 step 2) |
| Log: `REJECTED … invalid HMAC` | Both | The laptops were installed from different installer builds — reinstall both from the same `RASD_Setup.exe` |
| Tray alternates *Connected* / *Disconnected* | Endpoint User | NIC power saving (4.6), sleep (4.7), loose cable, VPN (4.2) |
| Tray: *Stale (service may be stopped)* | Endpoint User | `Start-Service RASD_Enterprise_Connector` |
| Endpoint shows Offline but tray says Connected | Both | Endpoint: `Test-NetConnection 192.168.50.10 -Port 3000` must succeed (heartbeats use port 3000). Admin: rule `RASD API TCP In` enabled |
| Commands stay *PENDING* | Both | Admin: relay shows `LIVE (1 agents)`. Endpoint: tray *Connected*. The command is delivered automatically at the next heartbeat (≤ 60 s) |

---

## 12. Pre-demo checklist

**Admin**

- [ ] Ethernet cable in; Wi-Fi off; VPN disconnected; charger in
- [ ] IP is `192.168.50.10` (`ipconfig`)
- [ ] Sleep disabled on AC; NIC power saving off
- [ ] `postgresql-x64-16` Running, Automatic
- [ ] Exactly one `RASD_Relay` process
- [ ] Relay: DATABASE, API SERVER online; no NO WIRED LAN banner
- [ ] `curl.exe http://127.0.0.1:3000/api/health` → OPERATIONAL

**Endpoint User**

- [ ] Ethernet cable in; Wi-Fi off; VPN disconnected; charger in
- [ ] IP is `192.168.50.20` (`ipconfig`)
- [ ] Sleep disabled on AC; NIC power saving off
- [ ] Installed from the same `RASD_Setup.exe` as the Admin
- [ ] `RASD_Enterprise_Connector` Running, Automatic
- [ ] `Test-NetConnection 192.168.50.10 -Port 3001` and `-Port 3000` succeed
- [ ] Tray shows **Connected** to `192.168.50.10`
