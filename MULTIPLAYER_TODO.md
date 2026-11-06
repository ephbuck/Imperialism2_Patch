# Multiplayer world synchronization

Working branch: `tranfer-network-map`.

This is the persistent checklist for the work discussed in this chat. Update it
as implementation and verification progress; a passing transport test does not
establish that the game can ingest the transferred world.

## Completed investigation

- [x] Scan current networking, patcher, tests, binaries, and historical VM logs.
- [x] Establish that current source has no FileSync implementation.
- [x] Identify the native initial-options request/reply and saved-world loader.
- [x] Build current source in isolation and pass all eight existing test stages.

## Implementation

- [x] Implement fresh native snapshot capture and a deterministic client load contract.
- [x] Add negotiated, bounded, SHA-256-verified transfers on the existing TCP port.
- [x] Publish complete snapshots atomically beneath the game's Save directory.
- [x] Hold dependent initialization until storage is complete and require a load-result acknowledgment.
- [x] Confirm native ingestion and lounge refresh in actual gameplay (saved and generated worlds).
- [x] Capture the VM2 join crash and identify incompatible host country records.
- [x] Write client-compatible country records during capture and test host restoration.
- [x] Verify the corrected saved-world snapshot against the actual VM2 native loader.
- [x] Preserve lobby phase/ownership and suppress native saved-player reconnect traffic.
- [x] Verify the saved-world lobby preview and successful client country selection on VM2.
- [x] Allow reclaiming disconnected saved-country reservations while protecting connected players' countries.
- [x] Execute regression coverage for empty, reserved, live, and other special country-owner values.
- [x] Preserve local host options delivery and the host's native loading path; cover host map handling and cdecl prefix forwarding.
- [x] Verify VM2 reclaims saved France and cannot take the host's occupied England.
- [x] Bind the reclaimed saved country as the client's local country before entering gameplay.
- [x] Exercise native country-role construction, argument forwarding, and cleanup after load failure.
- [x] Implement a separate native staging-map capture/apply path for generated/scenario lobbies.
- [x] Verify the generated staging-map path on the test VMs.
- [x] Isolate simultaneous transfers and prevent a slow client from blocking others.
- [x] Reject invalid data and handle deadlines, storage errors, and disconnects.
- [x] Scope readers/listeners to their connections and exercise repeated same-object rehosting.
- [x] Add signature-checked, idempotent native hooks and execution tests.
- [x] Preserve adapter selection, discovery, routing, and existing engine join restrictions.
- [x] Extend separate-process and multi-client regression coverage (12 stages).
- [x] Build and deploy consistent EXE/DLL/config outputs to the repo and working game folder.
- [x] Document the protocol, deployment requirements, limits, hooks, and remaining gameplay checks.
- [x] Verify deployment on both VMs and launch both games with the updated desktop scripts.
- [x] Verify two-player generated-world launch and one complete turn on both VMs.
- [x] Update both desktop run.bat launchers, back them up, and verify deployment hashes.

## Acceptance

- [x] Existing and new automated regression stages pass (all 12; latest build).
- [ ] WindowsVM1 host / WindowsVM2Clone1 client: generated, scenario, and saved worlds.
- [ ] At least three players: sequential and simultaneous joins.
- [x] Confirm matching preview/world identifier, successful two-player launch, and first turn advancement.
- [ ] Complete longer-run state comparison and three-player turn synchronization.
- [x] Resume the saved match after country reclaim and verify turn advancement on both VMs.
- [x] Verify saved-country reclaim after a fresh client launch using permanent VM disk builds.
- [ ] Exercise interrupted transfer and reconnect without loading partial/stale data.

## Direction / open evidence

- Default scope: reliable lobby joins, including an already-loaded host world.
  Midgame joining is a separate behavior decision; preserve engine restrictions
  unless the user explicitly requests extending them. User confirmed this scope.
- Historical FileSync/Cave9 logs are evidence of an older prototype, not current code.
- The bundled linker MAP is from an older build; validate hooks against v1.03 bytes.
- Before this work, the game-folder EXE lacked current network patches and the
  folder lacked dplayx.dll. Do not confuse that deployment with the tested build.
- Prepared VM deployment files are in `obj/vm-deploy/`, refreshed by `build.ps1`.
  Both VM desktop launchers now copy and verify all three files from that bundle
  before starting the game. Their originals are backed up beside them as
  `run.bat.before-worldsync.bak`; the reusable source is `tools/run-vm-game.bat`.
  Deployment hashes were verified on WindowsVM1 and WindowsVM2Clone1. Guest
  command access is now available; credentials are not stored in the project.
  The launcher also correctly refuses deployment while a game is running.
  A saved-world snapshot now loads on VM2, appears in its lobby preview, and
  permits country selection. A generated staging map also loads on both VMs;
  both players selected countries, launched, and advanced from 1502 to 1504.
  Host and client acknowledge the same transfer and share world ID 0x6AECE6F7
  and native turn index 2. Neither crash capture recorded a failure in that run.
- Automated ingestion tests use an explicit loader fixture. They verify the
  transfer/barrier contract, not the native engine's ability to deserialize worlds.
- VM2's captured join failure reached a committed, checksum-verified snapshot,
  then raised an archive EOF while loading country/unit records. Painting the
  partially loaded world subsequently faulted at VA 0x004AAB05. The host's
  TAutoGreatPower and THostGreatPower writers append data that the client's
  TRemoteGreatPower reader does not consume. Capture now scopes just their
  WriteTo slots to TGreatPower::WriteTo (VA 0x00504620); the host's other virtual
  methods and original tables are preserved. Automated restoration checks pass;
  native saved-world loading and country selection were verified on both VMs.
- The original full-saver approach also crashed the host in a generated lobby:
  the planning state is not initialized there. Generated/scenario options now
  retain their native setup path and transfer TMapMgr via THandleStream, with
  application immediately before the shared lounge refresh. Full snapshots
  remain reserved for the native 'load' options mode. Protocol 3 preserves the
  original mode in the transfer manifest. Generated staging-map ingestion,
  country selection, launch, and a complete two-player turn are now verified.
- The live tile arrays agree at 32 of 40 byte positions across all 6,480 tiles
  after the first turn. Remaining differing fields include native unit pointers;
  this is not a complete simulation-state checksum. Classify other differences
  before claiming longer-run state equivalence. Scenario and three-player native
  checks remain open. The saved-world lounge also needs a visual pass for its
  flag/title controls after loading the full document.
- Additional verified checks: patching twice produces identical EXE bytes;
  an altered native serializer signature is rejected; root, working-game, and
  prepared VM EXE/DLL copies have matching hashes.
- The saved-match reclaim warning came from the native selection handler treating
  owner `-2` (a disconnected saved-player reservation) as an occupied live seat.
  A signature-checked host hook now sends only that value and empty owner `0`
  through the existing claim path; live owners and other special values retain
  the original validation. All 12 automated stages pass, including execution of
  the new hook. In the running saved-game test, VM2 reclaimed France, released
  its temporary Spain selection, and correctly failed to take host-owned England.
  The first verification used a running-process patch. A subsequent restart
  reproduced the warning because both VM disk EXEs were still the previous
  build (`D5DE6731...`). Both VMs now have the permanent reclaim build installed
  and verified (`998384BB...` EXE, current `C589356D...` DLL), and both desktop
  launchers have been refreshed from the repository source. The user continued
  the native saved-host setup and confirmed the country-reclaim warning was
  fixed. The subsequent client wait was investigated and resolved below.
- During the fresh-launch investigation, regression coverage exposed local
  initial-options messages being consumed by the remote-transfer interception.
  Local recipients now retain ordinary delivery. The shared native options hook
  also retains the host's original file loader (including its prefix argument)
  and accepts the host's already-generated map at the common lounge hook.
  Clients still fail closed without their matching snapshot. All 12 stages pass
  with these changes. The host subsequently reached its saved planning screen.
- After a saved-country claim, the lobby assigned France to VM2 but its loaded
  country objects still treated England (the saved host) as local. The host was
  already in planning, so no new-game launch broadcast would release the client.
  Receive now checks host-confirmed native ownership on the game thread, reloads
  the committed snapshot once, and scopes the native country initializer so only
  the claimed country becomes local. Live ownership/status and the selected
  country are retained; native post-load UI entry resumes the existing world.
  Generated/scenario joins keep their existing launch path.
- Real VM verification: VM2 restarted with the current DLL, rejoined as DPID 3,
  reclaimed France, and immediately entered gameplay. Its France object is
  TClientGreatPower (`0x706690`), England is remote (`0x706AA0`), and VM1 remains
  England. Both games completed a turn from 1504 to 1506: native turn index 3,
  world ID `0x6AECE6F7`, planning phase 5, and both processes responsive. Client
  crash capture produced no failure. All 12 automated stages pass. Both VM disk
  DLLs match the prepared bundle. VM1's running module was retained as a backup
  while installing its next-launch DLL, preserving the active match.
