# RASD compared with production endpoint-management systems

This document compares RASD's design with open-source and commercial systems that manage Windows endpoints in
production today, and lists what RASD should adopt. Sources are at the end.

## 1. Systems studied

| System | Type | Server | Agent | Agent ↔ server channel |
|---|---|---|---|---|
| **Tactical RMM** | Open source RMM | Django + PostgreSQL, Vue UI | Go binary, Windows service | NATS messaging (over WebSocket on 443 since v0.14) for live commands; HTTP API for check-ins |
| **MeshCentral** | Open source remote management | Node.js, installs itself as a Windows service | Small C agent, Windows service | WebSocket over TLS; LAN mode finds the server by UDP multicast (port 16990) |
| **NetLock RMM** | Open-core RMM | ASP.NET Core + SignalR, MySQL | C#/.NET agents | HTTPS for sync and health, SignalR for real-time remote access |
| **Wazuh** | Open source security platform | Wazuh manager | C agent, Windows service | Enrollment on TCP 1515, events and keep-alives on TCP 1514 |
| **osquery / Fleet** | Open source endpoint visibility | Fleet (Go) + MySQL | osquery / fleetd | TLS; agent polls for config and queries on an interval |
| **Salt** | Open source configuration management | Salt master | Salt minion | ZeroMQ publish/subscribe (4505) + return channel (4506) |
| **Microsoft Configuration Manager** | Commercial | Site server + management point | ConfigMgr client | Client polls for policy; a persistent "fast channel" (TCP 10123) pushes urgent actions |
| **Datto RMM** | Commercial RMM | Cloud platform | Datto agent | Permanent control channel on 443; UDP 13300 for agent discovery on the LAN |

## 2. What every one of them does — and where RASD stands

| # | Proven pattern | Who does it | RASD today |
|---|---|---|---|
| 1 | **The agent opens the connection to the server** (outbound), and keeps it open | All eight | **Same.** The connector connects out to the relay on TCP 3001 and keeps the socket alive |
| 2 | **Push for speed, polling as a safety net** | ConfigMgr (fast channel + policy polling), Tactical RMM (NATS + check-ins) | **Same.** Live commands over TCP; queued commands returned on the 60 s heartbeat |
| 3 | **Find the server on the LAN by broadcast or multicast** | MeshCentral (multicast, UDP 16990), Datto (UDP 13300) | **Same.** Signed UDP broadcast on 3002 |
| 4 | **Prove the server is genuine before trusting it** | MeshCentral checks the server's certificate hash; Salt exchanges keys | **Partly.** Discovery is signed with HMAC-SHA256; the command channel itself is not authenticated |
| 5 | **Every agent has its own identity** | MeshCentral (hash of the agent's own key), osquery/Fleet (per-host `node_key` after enrolling with a shared secret), Datto (per-agent key), Salt (accepted minion key) | **No.** The agent is identified by the MAC address of its first network card, and all agents share one installer secret |
| 6 | **A command reaches only the agent it targets** | Tactical RMM (per-agent messaging), Salt (the minion checks the target and ignores others), ConfigMgr (per-client notification) | **No — real bug.** The relay sends every command to every connected agent, and the agent runs whatever it receives |
| 7 | **Commands are acknowledged and never run twice** | Queue + acknowledgement in all of them | **Yes since fix1.** Execute-once record + ACK before restart/shutdown |
| 8 | **The server runs as a Windows service, one instance** | MeshCentral (`--install` / Windows installer), Tactical RMM (systemd services) | **No.** Node is a child of the relay window; two relay windows stop each other's Node → "API timeout" |
| 9 | **Use the OS service manager for recovery** | Microsoft guidance: `sc failure` restart actions first; a watchdog only for hangs | **Mixed.** SCM recovery is configured (good); the extra connector ↔ protector process pair cannot restart a service process and is unnecessary |
| 10 | **Encrypted transport** | TLS everywhere (MeshCentral, Fleet, Tactical RMM, Datto) | **No.** Plain TCP and HTTP (acceptable on a closed lab LAN, not elsewhere) |
| 11 | **Framed messages** | NATS, ZeroMQ, WebSocket and SignalR all frame messages | **Partly.** Newline-delimited text, but a line split across two reads is lost |
| 12 | **USB storage control** | Microsoft's Removable Storage Access policies (Deny read/write/all) as the main control; `USBSTOR Start=4` as a blunt fallback | **Both layers used** — matches common practice |

**Conclusion:** RASD's overall architecture — outbound agent connection, LAN discovery, push plus queued
fallback, acknowledged commands, Windows service agent — is the same architecture that MeshCentral, Tactical RMM,
ConfigMgr and Datto use. It does not need a redesign. What separates it from those systems is a small number of
specific gaps (rows 5, 6, 8, 10, 11). Rows 6, 8 and 11 directly affect whether the two-laptop demo works.

## 3. Recommended updates

### A. Updates that make the demo reliable (small, low-risk)

| ID | Update | Pattern adopted | Fixes | Files |
|---|---|---|---|---|
| **A1** | The agent ignores commands whose `nodeId` is not its own (Salt-style targeting); commands that arrive before the agent knows its id are left for the queued path | Row 6 | "Restart PC A" also restarting PC B | `Client_Connector/RASD_Connector/NetworkClient.cs` |
| **A2** | Only one relay can run (named mutex); the launcher never starts a relay while one is running | Row 8 | Two relays stopping each other's Node → API timeout (seen in the relay log) | `Admin_Watcher/App.xaml.cs`, `RASD_Launcher/LoadingWindow.xaml.cs` |
| **A3** | The relay stops only the `node.exe` that runs RASD's `server.js`, not every Node process | Row 8 | Killing unrelated Node apps; part of the timeout loop | `Admin_Watcher/NetworkManager.cs` |
| **A4** | Keep a partial line between reads on both ends of the TCP channel | Row 11 | Commands lost when a message is split across two reads | `NetworkClient.cs`, `Admin_Watcher/RelayEngine.cs` |

None of these change ports, the wire format, discovery, the database or the UI.

### B. Production hardening (beyond the demo; larger changes)

| ID | Update | Pattern adopted |
|---|---|---|
| B1 | Per-agent enrollment: the agent enrolls once with the installer secret and receives its own token (like osquery's `node_key`); the agent-facing API routes require it | Rows 4, 5 |
| B2 | TLS on the API and the agent channel, with the server certificate hash pinned in the agent (like MeshCentral) | Rows 4, 10 |
| B3 | Route commands to the agent's own socket on the relay (register `nodeId` on connect) instead of broadcasting | Row 6 |
| B4 | Run the Node API as a Windows service (as MeshCentral does) instead of a child of the relay window | Row 8 |
| B5 | Replace the MAC-based identity with a generated device ID stored on the endpoint | Row 5 |
| B6 | Remove the connector ↔ protector restart pair and rely on SCM recovery | Row 9 |
| B7 | JWT checks on every dashboard route, and role checks for operator/viewer | — |

## 4. Honest limits

- No system — commercial or open source — can promise a connection that never drops. They all assume drops will
  happen and make reconnection automatic and commands idempotent. RASD already does both.
- The updates in section 3A remove the causes of failure found in this project's own logs and code review. Whether
  the demo then works end-to-end can only be confirmed by the two-laptop test in `CHANGES.md` and
  `NETWORK_SETUP_GUIDE.md`.

## Sources

- Tactical RMM — [How it all works](https://docs.tacticalrmm.com/howitallworks), [releases (NATS over WebSocket, v0.14)](https://github.com/amidaware/tacticalrmm/releases?page=4), [documentation](https://docs.tacticalrmm.com/)
- MeshCentral — [design and architecture](https://docs.meshcentral.com/design/), [WAN/LAN modes](https://meshcentral2.blogspot.com/2018/11/meshcentral2-wanlan-modes-intel-amt-cira.html), [MeshCentralAssistant (ServerID hash)](https://www.github.com/Ylianst/MeshCentralAssistant), [Windows installation](https://docs.meshcentral.com/install/windows/), [quick start](https://docs.meshcentral.com/install/quickstart/)
- NetLock RMM — [GitHub](https://github.com/0x101-Cyber-Security/NetLock-RMM), [deployment](https://netlockrmm.com/features/deployment), [documentation](https://docs.netlockrmm.com/)
- Wazuh — [agent client configuration](https://documentation.wazuh.com/4.12/user-manual/reference/ossec-conf/client.html)
- osquery / Fleet — [Remotely interfacing osquery](https://osquery.readthedocs.io/en/stable/deployment/remote/), [Fleet: migrate to fleetd](https://fleetdm.com/guides/migrate-from-osquery-to-fleetd)
- Salt — [Understanding SaltStack communication](https://docs.saltproject.io/en/getstarted/system/communication.html), [execution architecture](https://docs.saltproject.io/salt/user-guide/en/latest/topics/execution-architecture.html)
- Microsoft Configuration Manager — [Client notification](https://learn.microsoft.com/en-us/MEM/configmgr/core/clients/manage/client-notification)
- Datto RMM — [Agent encryption](https://rmm.datto.com/help/en/Content/1INTRODUCTION/Infrastructure/AgentEncryption.htm?cshid=5135), [allowlist / ports](https://rmm.datto.com/help/en/Content/1INTRODUCTION/Requirements/AllowListRequirements.htm)
- Windows service recovery — [sc failure](https://technet.microsoft.com/en-us/cc742019), [recovery actions](https://technet.microsoft.com/en-us/library/cc753662), [Restart Manager guidelines for services](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/RstMgr/guidelines-for-services.md)
- USB storage control — [Microsoft: prevent users from connecting USB storage](https://learn.microsoft.com/en-au/previous-versions/troubleshoot/windows-client/prevent-users-connect-usb), [NinjaOne: blocking USB storage](https://www.ninjaone.com/blog/how-to-block-usb-storage-devices/)
- TCP framing — [Reading from TCP streams](https://www.openmymind.net/2012/1/12/Reading-From-TCP-Streams/), [System.IO.Pipelines](https://devblogs.microsoft.com/dotnet/?p=18285)
