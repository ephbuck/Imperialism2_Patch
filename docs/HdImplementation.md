# HD map and cohesive UI

Updated November 16, 2026. This is an **opt-in implementation under gameplay validation**, not a completed HD release. The normal multiplayer/audio installation remains in legacy mode unless the startup patch and `windowed-map.ini` marker are installed together.

## Implemented

- Native-density expanded map: the game's 64-pixel terrain pitch and decoder remain authoritative. A larger map contains additional terrain, independently of UI scale.
- One immutable `HdLayout` supplies map bounds, UI placement and inverse input mapping. The sidebar targets 20% of client width; the complete native controls must fit and the map retains at least 576x512 pixels. Effective scale combines automatic sizing, `UiScale` and reported Windows DPI, then limits to fit. The limit is logged explicitly.
- Complete top and bottom groups centered over the map, using one rounded destination rectangle per group. Additional space uses background strips, with no repeated controls. The sidebar keeps all four Explorer information rows together; only its help/end-turn corner controls and bottom border anchor to the bottom. Native map-frame corners stay at native size; narrow middle sections repeat at the actual map edges.
- Bounded 110-slot streaming cache, exact tile ownership checks at all four recovered selection/background readers, and on-demand native terrain regeneration after eviction. The inline native object and signed-byte slot format are unchanged.
- Native camera/clipping/minimap/retention dimensions use the map viewport. Map input uses native pixels; pane input uses the inverse of the displayed pane rectangle. Live tracking uses the real cursor rather than a clamped legacy position.
- Expanded native map bounds are scoped to drawing and related rendering operations. Outside that scope, the native root retains its original map bounds so sidebar and minimap clicks reach their controls.
- Native autoscroll uses a configurable band inside each physical map edge, including diagonal corner scrolling. Its default and tracked configuration are 128 pixels at the user's request. Only its temporary boundary-test point is adapted; the original focus/modal guards, direction flags, throttle, camera movement and limits remain in place. UI panes and positions outside the map suppress autoscroll. Idle pointer queries can obtain a map proxy without holding a button.
- Pointer decoding preserves the native fixed camera-origin point at `0x75C010`, even when native scrolling occurs inside a mouse dispatch. Replacing this internal origin with the pointer causes camera jumps; pointer-driven tile selection still uses physical map coordinates.
- Coalesced presentation, resize/DPI layout generations, windowed/borderless modes, configuration reload and diagnostic captures. A new layout is redrawn before its map is presented or used for map input.
- The entire composed frame is assembled in a separate GDI bitmap and published with one BitBlt. Sidebar filler, controls and menu clears never draw directly to the visible destination. Background-erase messages no longer publish a frame from inside an unfinished native paint; presentation also waits until map-drawing scopes finish. The extra frame backing is replaced on resize and released on shutdown.
- Exact executable digest checks, native signatures, startup rollback, protected code writes, instruction-cache flushing, and executable trampolines with tracked rollback allocations.

Bitmap UI scaling preserves the original artwork; it does not replace the game's fonts or assets with new high-resolution artwork. Map labels currently retain native rendering. Full map draws remain the correctness path, so 4K interaction performance still needs measurement before optimizing dirty rendering.

## Build and use

Use a copied legitimate installation. The default installation target is the isolated `obj/graphics-game` sandbox. The patcher accepts the researched patched GOG 1.03 image (`998384BB...7CA7CC`) or its exact startup-patched image (`BE5DA3D7...8E4FAE`), and refuses other digests.

```powershell
# Compile without changing an installation:
.\src\BuildWindowedMap.ps1 -BuildOnly

# Compile and deploy into the existing isolated copy:
.\src\BuildWindowedMap.ps1

# Or explicitly select another copied installation:
.\src\BuildWindowedMap.ps1 -GameDirectory 'X:\Copies\Imperialism II HD'
```

The build embeds the managed renderer into `dplayx.dll` and adds a startup thunk to the copied executable. It backs up existing EXE/DLL files once as `*.before-windowed-map`. Assets, music and saves are not replaced. The production build also compiles the optional renderer because the IL export references its startup class; without the marker/startup patch, it does not install graphics hooks.

Add this section to the copied installation's `dplayx.ini`:

```ini
[Graphics]
WindowWidth=1920
WindowHeight=1080
UiScale=1
SidebarFraction=0.20
EdgeScrollMargin=128
Borderless=0
```

`WindowWidth/Height` are requested client pixels, limited to 800x600 through 3840x2160 and the monitor's available window area. Borderless mode uses the selected window's monitor at its current resolution, up to those limits; it does not change desktop display settings. At ultrawide aspect ratios, the sidebar can occupy less than 20% so the complete column fits vertically. `UiScale` is an accessibility multiplier, not map zoom. Invalid numeric values are ignored or bounded.

`EdgeScrollMargin` is the activation distance in physical map pixels (default 128, bounded to 8-128 and at most one quarter of the smaller map dimension). Reduce it for a narrower activation band. F8 reloads this setting; it does not extend scrolling over the UI controls.

- **F8:** reload the graphics section and apply its requested size/layout.
- **F9:** save UI/map backing buffers, a composed frame PNG and geometry/performance counters in `hd-captures` beside the copied executable.
- **F11:** switch between windowed and borderless presentation.

For legacy mode, close the copied game and rename `windowed-map.ini` to `windowed-map.disabled`, then restart. The startup thunk calls ordinary COM initialization and installs no graphics hooks. To restore installation files completely, close the game and copy the preserved `Imperialism II.exe.before-windowed-map` and `dplayx.dll.before-windowed-map` back over their corresponding files. Preserve the backups and saves.

## Evidence and newly resolved questions

The current original-map baseline was reproduced in the isolated copy using REA/Frida: map origin (10,58), viewport 576x512, camera (4160,1344), and a final source rectangle offset by the 64-pixel guard. Runtime Evidence `ev_f628ecc514f8af4d9dbf7d32d2c7c6f8a6fdce9c964e7ca0a6ce91c891fcf0f7`; local complete bundle: `obj/hd-validation/rea-baseline.json`. The loaded DirectDraw module was the system DLL. The observation is instrumented and is not a performance benchmark.

The pre-existing windowed build was played to the same save at 1280x900. Additional terrain was visible, together with black UI gaps and decorative pieces at the old map edge. A native DIB dump confirmed that the map-edge pieces were already inside the tactical surface, rather than a final-composition artifact. Runtime dump Evidence `ev_1d2cbf315194a8b1722b522c8abcc1633dd1e2d57a89b8a951c96ac100ecf525`; local investigation bundle: `obj/hd-validation/rea-hd-investigation.json`.

Focused static evidence retained in this repository:

- [cache-consumers.json](evidence/hd-ui/cache-consumers.json), `ev_31023cb2d799afb59ef09043dccdcee6cd965e8ef4c3be97f73795ddbb499ce1`: existing selection/background readers.
- [cache-neighbors.json](evidence/hd-ui/cache-neighbors.json), `ev_70fc35e31097506cb7d0b1ad01206f8c00e5c16e8046f886ef73c8b739b996cf`: the previously missed fourth reader at `0x559BF1`. A bounded byte-pattern scan of `.text` also found exactly these four instances of the two recovered indexed-load encodings; that scan alone is not a proof that no other encoding/indirect reader exists.
- [map-frame-thunks.json](evidence/hd-ui/map-frame-thunks.json), `ev_1610f09aa69cf677d7dbf328e335d7093f76ea3d1c5b2e9ce977da3478e20484`: map-frame thunk resolves to `0x60A240`.
- [map-frame-body.json](evidence/hd-ui/map-frame-body.json), `ev_6a617135cd536d28906a8902d5f1cfae4deb8a9f3f19d5cc73fb050ac3bc9422`: two masked 58x512 source strips and a fixed 518-pixel right offset. Only its map-Draw call is replaced; unrelated callers keep native behavior.
- [sidebar-runtime.json](evidence/hd-ui/sidebar-runtime.json): the sidebar received correct logical coordinates before the bounds fix, but the expanded native map intercepted them. After the fix, minimap recentering changed the camera to (4800,1280); an idle observation verified native hit-test bounds were restored to 576x512 while drawing used 1012x795. The observed allocation indices stayed within the 110-slot cache.
- [edge-scroll-handler.txt](evidence/hd-ui/edge-scroll-handler.txt): ordinary Rizin disassembly of the native root handler at `0x4B6C60`, its guarded point conversion at `0x4B6CE8`, four-pixel bounds, throttle and direction dispatch. The existing installed Rizin was used directly because the REA MCP provider's executable was not on PATH. This file is explicitly not REA Evidence. [edge-scroll-runtime.json](evidence/hd-ui/edge-scroll-runtime.json) retains REA/Frida observations associating the live root's mouse virtual method with thunk `0x4082CE`, which resolves to that handler. The later boundary probe had no retained hits; it does not establish execution of those boundary tests.
- [edge-scroll-fixed-runtime.json](evidence/hd-ui/edge-scroll-fixed-runtime.json) observes the revised boundary path reaching native directional dispatch. [edge-camera-origin-runtime.json](evidence/hd-ui/edge-camera-origin-runtime.json) observes the fixed native camera-origin point `(0,0)`, the native map/controller association, and a signature-checked live camera-origin decoder bypass used to test the origin-preservation correction. The permanent managed fix preserves that specific point rather than patching the decoder call; the live test mutation lasts until restart. Runtime instrumentation is not a performance benchmark.

Terminal partial instructions in bounded extracts are not evidence. Static observations do not establish execution. REA runtime records lack an authenticated executable subject; external file hashing, module paths and validated signatures provide the stated target association.

## Automated validation

```powershell
.\src\TestHd.ps1
# Geometry/rendering iteration without rerunning networking:
.\src\TestHd.ps1 -SkipMultiplayer
```

Executed successfully during this implementation:

- 205 existing viewport geometry checks.
- 13,476 HD layout and pane round-trip checks, including legacy, 1080p, 1440p, 4K, 3440x1440, fractional UI scales, and simulated 100/150/200% DPI inputs.
- 18,675 x86 cache checks, executing emitted stubs and the original eviction instructions. These include fourth-consumer coverage, regeneration callback arguments, stale owners, invalid slots, refusal/rollback and protected neighboring members.
- 39 native input fixture checks, including the shifted HD map origin, scaled pane boundaries, idle map-proxy availability at each UI boundary, and preserving the fixed native camera-origin query inside mouse dispatch.
- 1,876 edge-scroll checks execute the original native direction-classification instructions, testing 16/32/64/128-pixel margins and four map layouts against nine native view sizes, all four edges, corners, the band boundary, map interior, UI exclusion, margin limits, the 128-pixel default and inactive-map rejection.
- 45 projection/bounds/callback checks: 36 execute the original game's projection instructions, including its vertical-output-first ABI and horizontal wrapping; four verify scoped native map bounds are restored; five execute the edge callback through its native ThisCall call-site shape and check stack cleanup, map/UI classification and inactive-map fallthrough.
- 414 actual GDI compositor/DIB/input checks at 36 resolution/DPI/UI combinations. These scan every output pixel for uncovered chunks and verify an actual 64x64 landmark remains 64x64 under UI scaling. They also cover complete lower-sidebar information, a first-loaded selected unit with populated labels, bottom-up/top-down DIB orientation, RGB555/RGB565 conversion and stable GDI handle counts across resizing. Enhanced-metafile recordings verify that map and menu presentations each send exactly one BitBlt to the visible destination. A counterfactual run using direct composition fails this check with 929 visible commands instead of one.
- Rendering-probe fixture suite and JavaScript syntax check.
- Complete 12-stage multiplayer/world-sync integration suite against the HD-built DLL. The Windows firewall prompt raised by the test executable was not accepted or automated.

These fixtures do **not** establish gameplay, physical monitor DPI transitions, native modal ownership, native ABI coverage beyond the executed snippets, animation correctness or 4K speed. The local automated transcript is `obj/hd-validation/final-automated-tests.txt`.

## Remaining acceptance gates

Live visual checks now pass at 1280x900, 1920x1080, 3840x2160 borderless and 3440x1440 windowed, using the same loaded save and populated Explorer panel. The internal old map border and black UI gaps are absent, all four information rows remain visible, and the top/bottom groups are centered. Minimap recentering, Explorer selection, F8 resizing and F11 borderless transitions were exercised. The earlier firewall dialog no longer blocks these checks; its settings were not automated.

The corrected EXE/DLL were installed in `Imperialism II - Patched`, with timestamped `*.before-hd-ui-20261116-121710` backups of replaced files. This resolved the mismatch where that folder still contained the older renderer during the reported screenshot. The subsequent flicker fix is installed there as module `4b399b1a-1291-428d-9481-131a1b193656`, DLL SHA256 `29187C384079472DA446F9733CE9974C62E1220C19386057589288C826F8CB17`, recorded in its `hd-build.json`. The preceding DLL/manifest were preserved as `*.before-flicker-fix-20261116-123919`.

A fresh installed-game launch exposed an additional first-load case: a save with a selected Explorer could populate the initial background snapshot with text. The filler now repeats only a small empty texture beside the portrait and separate stone strips, instead of repeating the full row containing labels and icons. The new fixture poisons that old row with a marker. Live 1280x900 and 4K checks were repeated against the final installed DLL after this correction.

After the user reported frequent UI flicker, source inspection showed that the background and controls were being repainted in many operations on the visible DC. Offscreen frame composition fixes that exposure. The user exercised the buffered test copy at 1280x900 and confirmed "Steady now" while moving around the map. A later allocation-failure cleanup guard was added and all automated checks rerun before the normal installation was rebuilt. The open test copy was left running for the user; its saves remain in the sandbox. An attempted REA/Frida attach to the preceding process failed with VirtualAllocEx access denied, so no native runtime trace was claimed and no Frida session was created.

The subsequent edge-scrolling report exposed two separate issues: idle pointer queries lacked a map proxy, and the native handler tested the old view's outer four-pixel boundary. Adapting its boundary point enabled scrolling, but pointer substitution also affected a fixed camera-origin decoder call during mouse movement. Preserving `0x75C010` corrected the jumps. [edge-scroll-camera-steps.json](evidence/hd-ui/edge-scroll-camera-steps.json) records the permanent managed fix producing native up/right/left camera deltas of exactly 64 pixels on the requested axis, including repeated right/left steps. The user confirmed consistent panning, then requested a larger activation band. All automated checks were rerun with the 32-pixel default and configurable margins.

The normal installation and sandbox now both contain module `78a13853-cb72-4f8a-98ec-05a642d91abf`, DLL SHA256 `71AB6CF13376AD369E0DC147BA47147DC79D3A3A82917521B9BF2AF4AE7C5B61`, with `EdgeScrollMargin=32`. Previous normal-install files are backed up as `*.before-edge-scroll-20261116-131003`. The test copy was closed after authorized restart testing, and the normal installation was launched for handoff.

The user subsequently increased the normal installation's setting to `EdgeScrollMargin=128`. The committed configuration and source fallback also use 128, and the native classifier suite explicitly covers that margin. The installed DLL above retains its earlier fallback, overridden by the explicit 128-pixel setting in its INI.

Uninstrumented diagnostic samples reported zero terrain restore misses/errors. GDI handles were 396 at 1280x900 and 4K, and 399 after the ultrawide window transition; this short run is not a leak endurance test. At 4K, presentation p50/p95 was 6.641/12.664 ms. Only seven native draw samples were collected (p50/p95 32.151/111.499 ms), so these are observations rather than an acceptance benchmark; the provisional interaction target is not established.

The pre-buffering installed-build 4K capture accumulated 223 draw samples, p50/p95 110.778/113.368 ms, with presentation p50/p95 9.237/15.485 ms and 396 GDI handles. This exceeds the provisional draw/interaction budget and confirms performance optimization remains necessary; visual acceptance does not imply acceptable 4K frame cadence. Performance has not been rebenchmarked after adding frame buffering.

The full HD release still has the following acceptance gates:

1. Complete live checks at 800x600 and 2560x1440 for the final build; repeat the matrix after further rendering changes. Inspect other populated sidebar states and long text, beyond the Explorer panel already checked.
2. Exercise selections before/after eviction, hover, right/double clicks, dragging, idle animation, scrolling, minimap recenter/indicator, seam/bottom limits and both worlds. Compare decoder outputs to visible tiles, especially beyond the old viewport. Confirm regeneration does not change native animation cadence or reenter unsafe draw state.
3. Native report dialogs currently retain their original unscaled map-relative placement; adapting them remains separate work. Verify tooltips/modals, combat and non-map screens, world/load transitions, minimize/restore, alt-tab and physical DPI/monitor changes. Native modal ownership and arbitrary regional clipping remain explicit runtime questions, not established capabilities.
4. Benchmark without REA/Frida or per-blit logging. Test machine: EPHRAIM-PC, i9-13900K, RTX 3080 Ti, 64 GiB RAM; primary 3840x2160 and secondary 3440x1440 reported by Windows. Record baseline and HD idle CPU, private/working-set memory, GDI handles, pan/selection frame cadence, draw/present p50/p95 and cache faults. Use a provisional p95 interaction target of 33.3 ms; qualify or optimize it from actual results. Draw and presentation counters are separate timings, not an end-to-end frame-time claim.
5. Compare incremental/background updates with forced full redraws; repeated resizes must settle without GDI/memory growth. Recheck legacy mode by disabling the marker. Two-process gameplay with HD/legacy combinations remains separate from the passing transport fixtures.

Local corrected captures are `obj/hd-validation/sidebar-fixed-1280x900.png`, `sidebar-fixed-3840x2160.png` and `sidebar-fixed-3440x1440.png`. The first two show the background-corrected build before frame buffering; the ultrawide capture predates that background correction. The baseline and pre-fix captures remain alongside them for comparison. The local automated transcript records 34,730 numeric checks, plus the rendering-probe fixtures and syntax check; networking validation was run separately. The expected counterfactual failure is retained in `obj/hd-validation/flicker-regression-reproduction.txt`.
