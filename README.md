# Imperialism2_Patch

Community bugfix & modern multiplayer patch for **Imperialism II: The Age of Exploration** (v1.03 GOG release), addressing audio assertion crashes and broken legacy DirectPlay networking on modern Windows.

> **Note:** This repository does **not** include the game's data, music, or asset files. You **cannot** play the game with these files alone—a legitimate copy of *Imperialism II* must be purchased from the current rights holders (e.g., on [GOG.com](https://www.gog.com/en/game/imperialism_2_the_age_of_exploration)).

---

## What This Fixes

### 1. Modern Single-Port TCP/IP Multiplayer (`dplayx.dll` + `ImpII.exe`)
Original *Imperialism II* (1999) relied on Windows 98 DirectPlay 4 (`IDirectPlay4A` / `IDirectPlayLobby3A`), which is broken or painful to use on Windows 10/11:
* Required installing the deprecated Windows **Legacy DirectPlay** optional feature and global COM registry keys.
* Used DirectPlay's multi-port dynamic port range (`47624` + `2300–2400`), breaking NAT routers and firewalls.
* Restricted the in-game TCP/IP Join dialog (`TTCPIPDialog`) to **15 characters** and ran `StripIPAddressString`, stripping letters, hostnames, `:port`, and interface selectors.
* Bound blindly to Windows' highest-metric adapter (frequently picking WSL, Hyper-V, Docker, or inactive VPN adapters instead of LAN/Wi-Fi/Tailscale).
* Left `dpwsockx.dll` / `dplaysvr.exe` ports wedged on disconnect and popped a fatal `ASSERTION: D:\Epsilon\WNetMgr.cpp @927` dialog on unhandled DirectPlay system messages.

**What the new multiplayer stack provides:**
* **Zero Legacy DirectPlay or Registry Dependency:** Ships a standalone 32-bit `dplayx.dll` DirectPlay 4 replacement alongside a safe `CoCreateInstance` thunk in `ImpII.exe` (falls back to `ole32.dll!CoCreateInstance` if `dplayx.dll` is removed).
* **Single-Port Networking (`2350` default):** Uses a single configurable TCP port (`Port=2350` in `dplayx.ini`) with `TCP_NODELAY` for both session discovery and gameplay traffic over the Internet, plus UDP port `2350` for zero-configuration LAN/VPN broadcast discovery. Internet hosts only need to forward **1 TCP port (`2350`)**.
* **Full `IP`, `hostname`, and `hostname:port` Support:** Unlocks the in-game TCP/IP Join input box from `15` to `127` characters and bypasses `StripIPAddressString`, allowing:
  * Blank input (`""`) — scans across local/VPN subnets automatically
  * Direct IPv4 (`192.168.1.50` or `192.168.1.50:2350`)
  * DNS hostnames & custom ports (`myserver.example.com` or `play.example.com:2400`)
* **Multi-Interface Discovery OR Specific Network Interface Selection:**
  * **All Interfaces Mode (`BindInterface=All`, default):** Hosting listens on `0.0.0.0:2350` across every network adapter simultaneously and displays active local IPv4s in the Host Lounge; joining with a blank IP broadcasts discovery across **every** active IPv4 adapter's subnet (`Ethernet`, `Wi-Fi`, `Tailscale`, `ZeroTier`, `Radmin`, `Hamachi`, etc.) + loopback.
  * **Select a Specific Interface (3 ways):**
    1. **Hold `Shift`** while clicking **Host** or **Join** in-game to open the interactive **Network Interface Picker** dialog (`All Interfaces` vs. individual adapters).
    2. **Inline `@<AdapterOrIP>` suffix** in the in-game Join address box—for example `@Wi-Fi`, `@Tailscale`, `@192.168.1.50`, or `friend.ddns.net:2350@Ethernet`.
    3. **Persistent config** in `dplayx.ini` via `BindInterface=<AdapterName or IPv4>` or `PromptInterface=1`.
* **Clean Disconnect & Immediate Re-Host:** Uses `SO_REUSEADDR` and clean session state teardown so players can leave, re-host, or reconnect immediately without restarting the game or PC.

---

### World Synchronization (gameplay validation in progress)

Both host and clients must use the same updated EXE and DLL. The versioned join
handshake rejects older peers rather than allowing an incompatible transfer.

When the host sends initial game options, the shim captures the current world
on the game thread. Generated/scenario lobbies use the native map serializer;
already-loaded worlds use a full native save. It transfers that immutable
snapshot in 64 KiB chunks on the existing TCP connection. Clients verify
the total length and SHA-256, then atomically publish the file beneath
`Save\NetSync`. A patched client loader opens that exact snapshot; it does not
guess a `cli_*.imp` filename. A staging map is applied after native staging setup
and before its preview appears. Full snapshot capture uses the base great-power writer expected by remote client countries,
omitting host-only and AI extensions. Private virtual-table copies override
only that writer during the synchronous save and are restored on unwind. Full
loading preserves the live lobby phase and ownership; saved-player reconnect
messages are suppressed during ingestion. Protocol version 3 carries the host's
original world mode, so loading does not change a generated lobby into a saved-game reconnect.
Saved countries marked as disconnected can be claimed again in the lobby;
countries owned by a connected player retain the engine's existing protection.
Local host initialization keeps its ordinary message delivery and native file
loader; only remote clients consume downloaded snapshots.
After the host confirms a saved-country claim, the client reloads its committed
snapshot with that country configured as its local player and enters the native
saved-game screen. This runs on the game thread and preserves live ownership;
the saved host's country remains remote on the client.

The host holds initialization until the client has committed the file and holds
later messages for that client until native loading reports success. The native
StartGame entry is gated until every remote player has loaded its world. Each
client has an independent, bounded writer queue, so a stalled transfer does not
block other clients or network readers. Failed, corrupt, or timed-out transfers
disconnect the affected client; rejoining starts a fresh transfer. Ordinary TCP
packet loss is handled by TCP retransmission. Snapshots are limited to 32 MiB;
transfer/ingestion waits expire after 30 seconds without completion.

This work covers lobby joining and the host's existing world. It preserves the
engine's restrictions on joining a match already in progress. The 12-stage
automated suite covers transport, snapshot verification, concurrent and
cross-process transfers, load-result barriers, and native hook execution. Its
loader fixtures do **not** establish successful native world deserialization.
Separate VM checks verified saved-world ingestion, reclaiming a disconnected
player's country while protecting the host's occupied country, resuming France
against an England host and advancing the saved match from 1504 to 1506, and a
generated-world lobby, two-player launch, and advancement from 1502 to 1504.
Scenario, native three-player, longer-run state comparison, and extended saved-world
checks remain pending. See [MULTIPLAYER_TODO.md](MULTIPLAYER_TODO.md).

The test VMs' desktop `run.bat` files use [tools/run-vm-game.bat](tools/run-vm-game.bat).
Each launch copies and verifies the EXE, DLL, and configuration from
`\\VBOXSVR\Imperialism2_Patch\obj\vm-deploy` into `C:\Games\Imperialism2`.
`build.ps1` refreshes this bundle automatically and enables diagnostic logging
in its test configuration. Close the game before rerunning the launcher; it
refuses to update a running copy and stops if any copy or verification fails.
The first replaced game files and original desktop launchers are retained with
a `.before-worldsync.bak` suffix. `run.bat /deploy-only` verifies deployment
without starting the game.

### 2. `_winCdPlay` CD Audio Assertion Popup (`D:\Epsilon\win_cd.cpp @407`)
On modern Windows systems, *Imperialism II* (v1.03) can intermittently trigger a blocking debug assertion popup when changing music tracks:

```text
ASSERTION
D:\Epsilon\win_cd.cpp @407
_winCdPlay
```

* **Root cause:** In `_winCdPlay`, the game calls `_inmm.dll!mciSendCommandA` (`MCI_SET` with `MCI_FORMAT_TMSF`) before playing a track. If that MCI command returns a non-zero status code, the retail v1.03 binary triggers a leftover debug `ShowAssertDialog` call that freezes or crashes the game.
* **Fix:** Replaces the 5-byte `call ShowAssertDialog` instruction inside `_winCdPlay` with `NOP`s so normal OGG music playback (`MUSIC\Track02.ogg`–`Track14.ogg`) works untouched while transient MCI errors fail silently instead of crashing.

---

## Installation

1. Locate your **Imperialism II** (v1.03 / GOG) installation folder.
2. Back up your original `Imperialism II.exe` (e.g., rename it to `Imperialism II.exe.bak`).
3. Copy the following files from this repository into your game folder:
   * **`ImpII.exe`** — rename it to **`Imperialism II.exe`**
   * **`dplayx.dll`** — standalone modern DirectPlay 4 multiplayer wrapper
   * **`dplayx.ini`** — optional multiplayer port & network interface configuration (auto-generated with defaults if omitted)
4. Keep `_inmm.dll`, `libogg.dll`, `libvorbis.dll`, `libvorbisfile.dll`, and the `MUSIC` folder in the same directory so in-game music plays properly.

---

## Configuration (`dplayx.ini`)

```ini
[Multiplayer]
; Single port used for both UDP LAN discovery and TCP session traffic.
; For Internet hosting, forward only TCP port 2350 (UDP optional).
Port=2350

; Network interface selection:
;   All              = Listen on 0.0.0.0 when hosting; scan across ALL interfaces when joining.
;   <Adapter Name>   = Bind/scan a specific adapter (e.g., Ethernet, Wi-Fi, Tailscale).
;   <IPv4 Address>   = Bind/scan a specific local IPv4 (e.g., 192.168.1.50).
BindInterface=All

; Set PromptInterface=1 to always pop up the Network Interface Picker on Host/Join.
; (You can also hold SHIFT while clicking Host/Join at any time.)
PromptInterface=0

; Timeout in milliseconds when discovering games.
DiscoveryTimeoutMs=3000

; Set Log=1 to write diagnostic logs to dplayx.log.
Log=0
```

---

## Building & Testing from Source

No external SDKs or compilers are required—`build.ps1` uses the built-in 32-bit `.NET Framework 4.x` toolchain (`csc.exe` + `ilasm.exe`) included with Windows 10/11:

```powershell
# Build standalone dplayx.dll and apply binary patches
powershell -ExecutionPolicy Bypass -File .\build.ps1

# Compile and run the 12-stage x86 multiplayer/world-sync integration test suite
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /platform:x86 /out:.\obj\TestMultiplayer.exe .\tests\TestMultiplayer.cs
.\obj\TestMultiplayer.exe .\dplayx.dll
```

---

## Technical Patch Details

### Binary Hashes

| File | Size | SHA-256 |
| :--- | :--- | :--- |
| **Original `Imperialism II.exe` (v1.03 GOG)** | `4,024,320` B | `63346490C488D2A4EF528E3C0884D823E60AC59F602FA82657B39AA040253279` |
| **Patched `ImpII.exe`** | `4,024,320` B | `998384BB696F02D37A6725CEA33F98BC2CA711B803C2638BED07A91D687CA7CC` |
| **`dplayx.dll`** | `89,600` B | `C589356D3D07F8ABA7090C1B9D4433A8C44400E6461B85BCD5FCA7D295CED6A1` |

### `Imperialism II.exe` Offsets Modified (`src/PatchExe.ps1`)

| Patch | File Offset (`VA`) | Original Bytes -> Patched Bytes | Purpose |
| :--- | :--- | :--- | :--- |
| **`_winCdPlay` Assert** | `0x27B16C` (`0x0067BD6C`) | `E8 40 AB D8 FF` -> `90 90 90 90 90` | Suppresses modal CD audio MCI assert popup (`win_cd.cpp @407`) |
| **`.text` VirtualSize** | PE `.text` `+0x08` | `4F 78 2E 00` -> `C0 79 2E 00` | Extends `.text` `VirtualSize` within existing `0x2E7A00` raw section for COM and world-sync thunks |
| **`DPlayCoCreateThunk`** | `0x2E7C60` (`0x006E8860`) & `.rdata` `0x342500` (`0x00743700`) | Zero padding -> 39-byte x86 thunk + strings | Routes `CDirectPlay` `CoCreateInstance` to `DPLAYX.dll!Imp2CoCreateInstance` with fallback to `ole32.dll!CoCreateInstance` |
| **`CDirectPlay` Call Sites (×5)** | `0x08F0C8`, `0x08F26B`, `0x08F35C`, `0x08F683`, `0x0904A9` | `AC C7 76 00` -> `00 37 74 00` | Redirects `CDirectPlay` `CoCreateInstance` indirect calls to thunk pointer |
| **`TTCPIPDialog` Max Length** | `0x1961B3` (`0x00596DB3`) | `... 0F 00` -> `... 7F 00` | Expands TCP/IP Join `'addr'` input box limit from `15` to `127` chars |
| **`AskIPAddress` Filter** | `0x2781DD` (`0x00678DDD`) | `E8 FC E4 D8 FF` -> `90 90 90 90 90` | Bypasses `StripIPAddressString` so `hostname:port@interface` is preserved |
| **`CDirectPlay::Join` Timeout** | `0x08F831` (`0x00490431`) | `05 E8 03 00 00` -> `05 88 13 00 00` | Increases default `EnumSessions` join timeout from `1000 ms` to `5000 ms` |
| **`WNetMgr.cpp @927` Assert** | `0x27D463` (`0x0067E063`) | `E8 BA 71 D8 FF` -> `90 90 90 90 90` | Suppresses debug assert popup on unhandled `DPSYS_*` system messages |
| **Initial world loader** | `0x18B9AD` (`0x0058C5AD`) | Call `0x00408E0E` -> call `0x006E8890` | Loads the committed network snapshot through the cdecl `Imp2LoadWorld` export; retains native fallback when the DLL/export is absent |
| **Lobby continuation / map application** | `0x18B9B5`, `0x18B97B`; thunks `0x2E7D40`, `0x2E7D60` | Verified native branches -> guarded thunks | Preserves country selection after full loading; applies staging maps before the preview; retains original failure handling |
| **Native launch barrier** | `0x18A540` (`0x0058B140`) | First 10 bytes -> jump `0x006E8900` plus padding | Checks `Imp2CanStart`, preserves ECX and original instructions, and returns false while a remote player has not loaded |
| **Saved-country reclaim** | `0x18ABFC` (`0x0058B7FC`); thunk `0x2E7D90` (`0x006E8990`) | Verified 8-byte owner check -> jump plus padding | Allows disconnected saved reservations (`-2`) through the native claim path; preserves live-owner validation |
| **World hook thunks / names** | `0x2E7C90`, `0x2E7CC0`, `0x2E7D00`; names `0x342530`, `0x342540` | Verified padding -> thunks / export names | Resolves the new exports without adding an import dependency; rejects modified hook sites and serializer/loader signatures |
