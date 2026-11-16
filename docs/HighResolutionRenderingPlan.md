# Native-density world map and independently scaled UI

Investigation date: November 12, 2026. **Investigation and implementation plan only; no game executable, DLL, configuration, or assets were changed, and no game process was launched.**

## Recommendation

Build a small, game-aware rendering hook with a compositor. Keep the existing world simulation, terrain/sprite rasterizers and tile lookup. Render the map into a viewport-sized native-pixel surface and copy it to the selected-resolution presentation target at **1:1**. Render existing UI panes into separate logical surfaces, then scale and anchor those panes independently. Begin with unscaled UI and an enlarged map; add UI scaling after map rendering, cache safety, scrolling and input agree.

A resolution-constant patch alone is insufficient. The code contains independent 800×600 device bounds, fixed camera limits, fixed redraw/animation rectangles, and an inline 110-entry cache with byte-sized tile-to-slot references. A generic DirectDraw/upscaling wrapper cannot make the game generate additional map tiles or separate an already-composited UI from terrain. A complete renderer/UI reconstruction is possible but is unnecessary for the first proof of concept.

The strongest new finding is that the map's tile-loop extents already derive from its Draw rectangle. Its surrounding resource and navigation assumptions are the main obstacles. Enlarging that rectangle without repairing the cache can index beyond its metadata and overwrite the map's cache-surface pointer.

## Scope, identities, and evidence

The working branch was `main_world_REA_highRes`, at `21cab71` when inspected. No `AGENTS.md` existed in the repository or checked ancestor locations. Read the current README and patcher before tracing code. Significant prior research lives on branch `Main-World-high-res`, commit `6961ffa`, including `MapCoordinateContract.md`, `MapAnimationTroubleshooting.md`, native disassembly, runtime excerpts, and `GraphicsBridge.cs`. Those files were read through `git show`; that branch was not checked out or merged.

**Current target:** `ImpII.exe`, PE32/i386, 4,024,320 bytes, image base `0x00400000`, SHA-256 **`998384BB696F02D37A6725CEA33F98BC2CA711B803C2638BED07A91D687CA7CC`**. All addresses below are virtual addresses in this target, unless explicitly identified as historical. Its `.text` starts at VA `0x00401000`, file offset `0x400`; use PE section mapping for other sections. The README identifies this as the multiplayer/audio-patched GOG 1.03 executable.

Do not copy addresses from the old `Imperialism II.map`: it describes another link. The prior graphics branch executable hashes to `1BE0901F3FC2B5896122A470A05E8405CB7D8E3ABBD0425BE0EFEBCF5C071253`. Class/function names below follow that research and the legacy symbol relationships; the current addresses, instructions and dispatch pointers cited here were rechecked using REA. No original C++ source or new native decompilation is claimed.

REA MCP 6.9.0 supplied PE import evidence. It had no configured Ghidra, IDA or Kuna provider. The initial objdump operation also reported a missing executable. To obtain actual REA native instruction evidence, used the same installed REA 6.9.0 CLI with its `execute-rizin-command` operation and a portable official Rizin 0.9.1 build, outside the repository. This route performs static inspection and did not execute the game. The official portable-build mechanism is documented by [Rizin](https://rizin.re/install/); the release came from [rizinorg/rizin v0.9.1](https://github.com/rizinorg/rizin/releases/tag/v0.9.1). A separate shell identity probe reported commit `c3a90e9226d977f58f4e9c75f78fa6b07afe13c7`; `rizin.exe` SHA-256 was `4E23543E17D41242152CD38F8BBEAFD161CE50AF970848794DE407F86CC5CCA7`. REA's own command Evidence leaves the provider-version field unknown; the separate probe does not alter that fact.

The evidence directory contains complete REA JSON records and readable stdout extracts:

- **E1 — imports:** [rea-pe-imports.json](evidence/high-resolution/rea-pe-imports.json), Evidence `ev_e98f1c1ffe3e85078e9711b8d4b6762fecd623b35d7b0010e33d1c491f78f837`.
- **E2 — projection/lookup:** [rea-map-projection.txt](evidence/high-resolution/rea-map-projection.txt), [JSON](evidence/high-resolution/rea-map-projection.json), `ev_698e9a9acf425af8c448d93d0f9866c7be61b88ec0d19b86a351762f6e8732be`.
- **E3 — map draw/cache:** [rea-map-draw-bounded.txt](evidence/high-resolution/rea-map-draw-bounded.txt), [JSON](evidence/high-resolution/rea-map-draw-bounded.json), `ev_a916096a78aaa4cee56ef8824b04e5381598794ede8ee3d90fdef2d4178c2679`.
- **E4 — surfaces/final copy:** [rea-map-surfaces.txt](evidence/high-resolution/rea-map-surfaces.txt), [JSON](evidence/high-resolution/rea-map-surfaces.json), `ev_693b68bf116c1222bfffd3c84bbe261cefcb8481b4ee8cb6e8a67c884b02ee61`.
- **E5 — idle/selection/redraw:** [rea-animation-redraw.txt](evidence/high-resolution/rea-animation-redraw.txt), [JSON](evidence/high-resolution/rea-animation-redraw.json), `ev_c7151f6f8138d344f1fcee6cd352d240ac07c90125c4d0019cd6587c316d90ab`.
- **E6 — display/UI:** [rea-display-ui.txt](evidence/high-resolution/rea-display-ui.txt), [JSON](evidence/high-resolution/rea-display-ui.json), `ev_bd27766cdf16c6b06f770647060011c1afeca5ead823726c50c5e4e607e2c9b5`.
- **E7 — camera/navigation:** [rea-navigation.txt](evidence/high-resolution/rea-navigation.txt), [JSON](evidence/high-resolution/rea-navigation.json). This file contains multiple complete Evidence records; the broad, byte-bounded navigation record is `ev_f247506b97dbfc7af4c4f20b564619c046adf74a8c9970fee06f40352c9e6a9d`.
- **E8 — map dispatch:** [rea-map-dispatch.txt](evidence/high-resolution/rea-map-dispatch.txt), [JSON](evidence/high-resolution/rea-map-dispatch.json), `ev_3227ae1499f3368f628930b381ee77e87d49ac349de1bd90078fe38be8568871`.

These are observations of stored code/data, not observations of calls executing. Indirect-call coverage is partial. Rizin's first long `pd` decode produced inconsistent instructions past its default read block; E3 explicitly enlarges the block and uses bounded byte decoding. Use E3 rather than the superseded first attempt. Some other bounded extracts end in the middle of an instruction; such terminal fragments do not support findings. The complete records retain commands, limitations, exit/completion status and pre/post executable digests.

## 1. Display setup: several independent dimensions

**Confirmed in current code (E6):**

- `0x00532980` stores **800** at `0x0075BC88` and **600** at `0x0075BC8C`. `0x005329B0` copies those values into `0x0075BC70/74`. They participate in engine geometry, not just the OS display request.
- A separate lazy QuickDraw device initializer, `0x004AADB0`, hardcodes 800 and 600 into device bounds associated with `0x00759F48` and the pointer cell `0x0075A050`. Patching the first globals misses this copy.
- Mode-switch caller `0x004942D0`, at `0x00494364–70`, pushes `16, 600, 800` and calls `0x00403E3B → 0x0048AD90`.
- `0x0048AD90` uses a DirectDraw interface: virtual `+0x50` for cooperative level and `+0x54` at `0x0048AE95` for display mode. The IID bytes at `0x00716D98` match `IID_IDirectDraw4`; `+0x54` receives width, height, bit depth and two zero parameters. Cooperative flags include `0x13` for the fullscreen path and `0x08` for the normal path. Surface creation `0x0048AFA0` calls virtual `+0x18` at `0x0048B023`.
- Windows QuickDraw `CopyBits`, `0x00404E8F → 0x004AAA40`, has multiple destination paths, including a GDI `BitBlt` call at `0x004AAC99`. DIB drawing reaches `StretchDIBits` at `0x00486083` and `0x00486114`. Presence of `StretchDIBits` is not evidence that the entire game is always stretched.

Short native excerpt:

```asm
00494364  push 0x10       ; 16 bpp
00494366  push 0x258      ; 600
0049436B  push 0x320      ; 800
00494370  call 0x00403E3B ; -> 0x0048AD90
...
0048AE95  call dword [ecx+0x54]
```

**Separation from DPI/wrappers:** These instructions establish the game's request and internal defaults. They do not establish the mode actually accepted by a modern driver or compatibility layer. E1 contains no static `SetProcessDPIAware`/`SetProcessDpiAwareness` import, but that alone does not establish runtime DPI awareness or exclude dynamic calls/manifests. A compatibility wrapper can virtualize 800×600 and enlarge its output without enlarging Draw's tile rectangle. Windows DPI virtualization is another independent transform. Measure actual client pixels, DPI awareness, loaded `ddraw.dll` path and surface descriptors in the baseline runtime probe.

The prior bridge resizes the physical window but reports 800×600 to legacy geometry queries and scales its output globally. Its `GraphicsBridge.cs` `RecalculateGeometry` (approximately lines 1229–58), `MyGetClientRectHook` (3456–77), and `ViewWndProc` (3990–4033) show this explicitly; input is mapped back and clamped to 799/599. Reusing that global mapping would prevent clicks in a larger native-pixel map.

## 2. Main map draw path and its real limits

**Confirmed dispatch (E8):** vtable Draw cells `0x00703A14`, `0x00703CA4`, `0x00703F34` all point to `0x00409ADE → 0x0055D070`. ScreenToTile cells `0x00703AC4`, `0x00703D54`, `0x00703FE4` all point to `0x004078C9 → 0x0055A5A0`. Selection cells differ: `0x00703AB8/3FD8 → 0x00402009`, versus `0x00703D48 → 0x00403616`. Preserve the Old/New World implementations separately.

The draw chain supported by E2–E4 is:

```text
map Draw(rectangle) 0x55D070
  copy caller's map-local rectangle
  offset buffer-access copy by (+64,+64)
  derive tile row/column loop bounds from rectangle + camera
  wrap tile columns / stop at world row 60
  populate/reuse terrain cache; copy each 64×64 tile into tactical GWorld
  call available animation's DrawNextFrame(+64,+64)
  draw additional selection/labels
  CopyBits at 0x55D889 -> 0x404E8F -> 0x4AAA40 -> destination path
```

The rectangle copy is at `0x0055D0C9–E7`; its buffer offset calls `OffsetRect` at `0x0055D147`. Dynamic bounds are calculated at `0x0055D259–2CC`: differences of the supplied right/left and bottom/top coordinates are divided by 64, with edge coverage. Loops run at `0x0055D327–647`. **This is viewport/dirty-rectangle tile drawing, not a pre-rendered full-world bitmap.** Its terrain cache and composition surface are separately sized.

Projection `0x0054F680` splits a tile ID into **108 columns**, shifts row and column by 6 (**64 pixels**), subtracts camera, offsets odd rows by **32 pixels**, and handles a horizontal span of `0x1B00` (**6912 pixels**). Outputs are signed 16-bit coordinates; a divisor parameter supports other scales, but the main Draw calls it with **1** (`0x0055D369`). World height is 60×64 = **3840 native pixels**. These constants should stay unchanged for the requested behavior. The 64-pixel pitch is raster geometry; movement adjacency need not be reimplemented as a rectangular grid.

**Two distinct offscreen resources:**

- Map initialization `0x005598F0`, at `0x00559900–41`, builds a **7040×64** terrain cache (`0x1B80 = 110×64`), requests 8-bit storage, and stores its GWorld pointer at **`map+0x3F8`**.
- Shared tactical GWorld setup `0x00532EA0` builds bounds **(-64,-64)–(globalWidth,globalHeight+64)** and requests 16-bit storage. With the original 800×600 globals that is **864×728 storage**, not a 576×512 surface. It is lazily allocated through pointer cells `0x0075BCD8/0x0075BCDC`; map initialization copies the shared pointer into `0x0075C0E8` at `0x0055995F`. The final CopyBits uses that pointer and the screen-port pointer cell `0x00759F90`.

Do not confuse surface bounds with map placement. The prior research's original visible map is **(10,58)–(586,570)**, or **576×512** inside the 800×600 UI. Current camera invalidation confirms its size (E7); the placement comes from the historical coordinate/runtime work and still needs a current runtime pane-geometry check.

**Cache failure mechanism (E3; consequence is derived from confirmed instructions):**

```asm
0055D20D  mov [esp+0x10], 0x6E ; 110 entries
0055D22A  lea ebp, [edi+9]    ; fixed retained row span
...
0055D3BC  cmp dx, 0x6E
0055D3C0  jnl 0x0055D3E3     ; reaches indexing even at exhausted index
...
0055D3E3  movsx eax, dx
0055D3E6  mov bx, [esi+eax*8+0x8A]
...
0055D441  mov [eax+ebp+0x13], dl ; tile's slot is one byte
0055D53D  movsx ecx, byte [eax+ebp+0x13]
```

Cache metadata begins at `map+0x84`, with 8-byte records. Entry 110 begins at **`map+0x3F4`**; its fields at `+0x88/+0x8A` relative to the indexed base overlap **`map+0x3F8`**, the cache GWorld pointer. Thus an exhausted allocation can corrupt a pointer when an enlarged view needs more protected tiles. This is a static hazard, not a crash reproduced in this session. Simply changing 110 to a larger number is unsafe: both the inline object layout and signed-byte slot consumers must change. A wraparound allocator alone also leaves stale tile ownership unless every eviction invalidates the previous tile's `+0x13` backlink.

## 3. UI composition and independent scaling

**Confirmed:** Map terrain, animation frames and map labels are composed through QuickDraw ports/GWorlds; the final map rectangle is copied into a shared screen destination. Labels are called at `0x0055D7C0 → 0x0040A63C → 0x004A8700`. CopyBits compares its destination pixmap against `0x00759ED4` at `0x004AAAB7`; its unmasked memory branch calls `0x00403125 → 0x00487790` at `0x004AAB08`, while its screen/masked path selects DC globals `0x00759FC8/0x00759FC4` and reaches BitBlt. This provides a useful separation seam, but memory-to-memory terrain-cache/atlas copies must remain unscaled. Focus/selection can also draw outside the full-map path (next section). There is no confirmed ready-made independent UI layer to scale after a final screen capture.

**Historical evidence:** The bridge treats all UI as an 800×600 canvas, expands the map source according to zoom, downsamples it into the old 576×512 hole, then scales the whole canvas. At 4K Fit the old canvas is 2880×2160 with pillarboxing. That behavior can make a zoomed-out map look larger but changes the number of physical pixels per tile; it is not the requested native-density renderer. Its current animation research also records direct idle background/sprite writes escaping the scoped transform.

**Implementation implication:** intercept at semantic map/view and port boundaries, before final composition loses layer identity. Route map-owned drawing into a native map target; preserve a logical 800×600 UI backing store initially. Extract/reanchor top controls, bottom controls and the right sidebar as separate UI regions, or render their owning views into logical pane surfaces. Clip the old map hole out of UI presentation. Keep dialog ownership/z-order and input tied to the same pane mapping. Do not classify a blit solely because it is small or lands near the old map rectangle.

Use a 32-bit presentation compositor even if native source buffers remain 8/16-bit. Preserve actual source palettes, RGB555/RGB565 masks, signed DIB height and row pitch when converting. Treat map labels/tooltips as UI-sized text anchored at native map positions; keep terrain, units and world-space selection geometry at native map density. A larger bitmap font can be scaled initially; crisp text at fractional UI scales may require recreating the matching font and measuring layout at that scale.

## 4. Input, scrolling, and navigation

**Confirmed lookup path (E2/E5):** `0x00423BD0` subtracts a view's origin `[view+0x24/+0x28]` from a 32-bit point. ScreenToTile `0x0055A5A0` receives an already-local point, adds `[map+0x64/+0x68]` camera pixels, multiplies by 1/64, and writes three **16-bit** outputs: column, row, and a sub-tile classification. Initializers `0x005594E0` and `0x00559510` supply the 1/64 doubles. Helper `0x006816B0` explicitly selects x87 truncation toward zero. Preserve the native decoder, including its edge classification; the third output is not a validity boolean.

Normalization `0x00550340` makes a single ±108 correction to columns and clamps rows to **0..59**. Projection itself has another wrapping loop for positions below -64; these are distinct operations. Keep camera/input positions normalized rather than assuming every helper implements unlimited modulo. At 1920/3840-wide targets the proposed map view is narrower than one 6912-pixel circumference; wider future targets need an explicit repeated-world policy.

**Confirmed navigation limits (E7):**

- Camera setter **`0x0055AA30`** clamps row to **0..52** at `0x0055AA9B`; stores row/column shifted by 6 into `map+0x68/+0x64`. One horizontal branch depends on a world-state flag and clamps against `110-[0x0075C048]`; another wraps ±108. Preserve that variant distinction until its world semantics are verified.
- It invalidates a fixed **576×512** rectangle at `0x0055AAEA/F2`, then calls animation-location update at **`0x0055AB36 → 0x0040104B → 0x004B9EC0`** with **(-64,-64)–(640,576)**. The latter moves animation rectangles and removes out-of-range entries. Updating buffer size alone would still retire animations outside the original area.
- Scroll **`0x0055ABA0`** handles direction bits 1/2 vertically and 4/8 horizontally; it includes a half-step mode using `0x0075C04C/50`. It reaches the setter through virtual `+0x218`, thunk `0x0040A2AE`.
- Center-like routine **`0x0055A880`** subtracts a fixed four rows and half of `0x0075C048`, and invalidates **575×511**; a matching getter **`0x0055A920`** adds them. Centering, recenter-after-selection and minimap navigation cannot keep those original half-view dimensions.
- Immediate tile drawing **`0x0055A700`** rejects projected tile positions beyond **576×512**, with partial-tile handling. Enlarging only full Draw would leave this incremental path clipped. Local view bounds are built from `[view+0x34/+0x38]` by **`0x00423B20`**, independently of point localization.

For a taller map, compute vertical limits from **3840 minus map viewport height**, then quantize according to the existing scrolling mode. With row-aligned movement, the last safe row is `floor(max(0,3840-mapHeight)/64)`. Keep horizontal world wrapping/variant clamping separate from physical client edges. Replace the fixed invalidation and animation-retention rectangles with the actual native map bounds plus guard.

**New input contract:** first give a modal/UI pane its hit test in z-order. For UI pane i, invert its display mapping: `U = logicalPaneOrigin + (physicalPoint-physicalPaneAnchor)/uiScale`. For the exposed map, use `L = physicalPoint-mapPhysicalOrigin` and call the original map-local handler/decoder; do not divide by UI scale or subtract `(10,58)` a second time. Preserve button state, double clicks, capture, drag coordinates, hover, keyboard focus and right-button tools. Extended map clicks must reach the map handler even when they lie outside the original root's 800×600 or map child's 576×512 hit bounds. Either grow the real map pane/root bounds safely or explicitly route through the verified original map event entry; selecting that entry and checking its ABI is a Stage 0 gate.

**Minimap static path recovered (E7):** current `TMiniMapView` identity string `0x00750168` leads through runtime-class record `0x00711240` and factory `0x004031FC → 0x0060EAA0` to vtable `0x00711758`. TrackMouse slot 105, cell `0x007118FC`, resolves through `0x00403C5B → 0x0060F150`. In phase 2 it divides the local pointer coordinates by two (**two logical minimap pixels per tile**), adds origin fields `+0x8C/+0x90`, subtracts half displayed extents `+0x9C/+0xA0`, wraps column once by ±108, and clamps row to 0..60 before forwarding. The downstream camera setter applies its narrower legal range.

At `0x0060F1EF`, tracking calls `[mini+0x88]` virtual `+0x1E8`. The recovered `TMapUberPicture` vtable `0x007114E8` maps that slot to `0x0040A7A4 → 0x0060D070`, which forwards through child `[owner+0xB0]` virtual `+0x1E4`. On the known map vtable that is `0x004024F0 → 0x0055AA00 → virtual +0x218 → 0x0055AA30`. **Actual owner/child bindings require runtime confirmation**; this is a statically supported candidate chain, not an observed click trace. `0x0060ECD0` SetScreenSize writes extents `+0x9C/+0xA0`; Draw at `0x0060ED50` multiplies them by two for its indicator. Supporting identity/forwarding record: `ev_013089bdde2f948e18fe8f85cdd2fd5d9624adad95838d0a8469b2c9d4a0a9b1`; additional clipping record: `ev_92aa43668afe4d1f9524d7b650b97730e2b53c724a27805422027406f069560c`.

Minimap terrain coordinates should retain their world meaning; only its UI pane scales. Invert that pane's physical/UI mapping before the native two-pixels-per-tile conversion. Update SetScreenSize from the enlarged map's native extent, including fractional edge coverage if its integer extent cannot represent partial tiles exactly. Its indicator must reflect the new viewport and split at the seam as necessary. Recenter must use the new half-viewport and the correct camera setter.

## 5. Redraw, overlays, and screen transitions

**Confirmed (E5):** `TAnimator::DoIdle` at `0x004B9C40` iterates virtual Tick `+0x28` at `0x004B9CD2` in phase 1. Focus idle draw `0x004B86E0` invokes virtual DrawNextFrame `+0x2C` at `0x004B873B`, using a zero offset rather than the full-map +64 offset. Transparent-focus paths **`0x004B8C10/0x004B8CB0`** reach CopyBits at **`0x004B8D52` and `0x004B8DA9`**, including saved-background handling. Old/New selection implementations **`0x005599C0/0x0055B1D0`** consult the signed-byte cache index too. A hook only on full-map Draw misses these paths.

`TView::Invalidate`, `0x0049FBF0`, converts a supplied rectangle through virtual `+0x134`, or obtains bounds through `+0x160`, before calling **`InvalidateRect` at `0x0049FC55`**, IAT `0x0076C5CC`. A shared inverse/forward transform must cover dirty rectangles and clips as well as visible output. Round transformed dirty bounds outward; preserve/restore GDI clip state. Arbitrary region handles (`SelectClipRgn`, `ExtSelectClipRgn`) require their own region mapping, not just RECT hooks.

Keep an immutable frame geometry record with active map identity, camera, native source bounds, physical map rectangle, UI pane mappings, and generation. Publish it together with a completed frame. Background saves/restores, labels, full/dirty drawing and input use that same generation. On resize, UI-scale change, world switch or map destruction, invalidate saved backgrounds and stale surface/DC associations before reuse.

Explicit follow-up cases: coast/fog/political overlays; selected worker/explorer/ship; labels at all four edges; tooltips; popup menus; modal dialogs; Old/New World; combat and other non-map screens; load/new game; alt-tab; minimize/restore; display loss. None were executed in this session. For non-map screens, a separate UI-only 800×600 composition mode is a practical first release policy. Entering/leaving that mode must reset map-specific hooks and capture state rather than applying stale map transforms to the other screen.

## 6. Smallest viable implementation and coordinate ownership

**Binary-only patch:** useful for a throwaway fixed-resolution experiment, but would require coordinated changes to mode request, multiple device/global bounds, allocation, pane geometry, camera/setter/getter, redraw/animation rectangles and cache ownership. It still would not deliver independent UI scaling by changing constants. Do not use search-and-replace on all 800/600/576/512 values; some are resource/world/UI dimensions with different meanings.

**Recommended game-aware hook:** a dedicated x86 graphics DLL, loaded early through a deliberate loader route; a later production patch can add a minimal validated loader thunk. It can alternatively be loaded through the existing DLL startup infrastructure if startup order is demonstrated. Keep graphics logic separate from network protocol behavior. A plain `ddraw.dll` proxy can help observe/present surfaces, but needs the game-aware Draw/cache/navigation hooks above. Stage 0 should use a controlled loader with the executable unchanged on disk.

**Substantial reconstruction:** replacing terrain interpretation, animation lifecycle, map event handling and all UI layouts in a modern renderer would require broad resource/state recovery. Defer it unless safe interception cannot isolate the existing rendering paths. GPU presentation does not by itself require replacing simulation or tile rasterization.

Dimension ownership should be explicit:

- **Physical display/client W×H:** output target, swapchain/compositor, window client bounds, physical clips, actual input and presented dirty regions. Make the process/window DPI handling explicit and test monitor changes.
- **Native map pixels:** map viewport width/height, map target, camera, 64-pixel pitch/32-pixel stagger, sprite sizes, map clips, animation rectangles, world wrapping and the 64-pixel buffer guard. Copy map pixels 1:1; source and destination extents must match. Keep internal signed 16-bit boundaries bounded and convert to 32-bit geometry in the hook.
- **Logical UI units × independent scale s:** buttons/text/panels, UI hit bounds, dialog placement and minimap pane size. Anchor panes to physical edges; UI growth consumes physical space but never changes map tile size. Keep the legacy UI's logical size where needed instead of overwriting shared engine globals globally.

One proposed layout, preserving the original margins/chrome widths, is map `(round(10s),round(58s))–(W-round(214s),H-round(30s))`. This is a design proposal, not recovered native behavior. It yields the following test cases:

- **800×600, s=1:** map **576×512**, unchanged placement and density.
- **1920×1080, s=1** for the first map-only stage: **1696×992**. At proposed **s=1.5** in the later UI stage: **1584×948**.
- **3840×2160, s=1** for the first stage: **3616×2072**. At proposed **s=3** later: **3168×1896**.

Allocate a dedicated map target with at least 64 pixels of guard on each side, using coherent origin/bounds metadata. The existing shared GWorld allocator's asymmetric bounds are evidence to accommodate, not a reason to patch global UI geometry. Preserve native origin semantics when converting from that old allocation.

**First cache strategy:** retain the existing 110-slot indexed raster cache and replace allocation/ownership with bounded eviction. After a tile is copied into the large map target it need not remain pinned merely because it is visible. Invalidate the evicted tile's backlink, update the new owner's backlink, and regenerate a tile on demand for selection/background consumers. This avoids expanding the inline object or byte slot format. It is a proposed implementation, subject to auditing every cache consumer. Fail closed on an unknown consumer or exhausted slot; never continue with index 110. If bounded streaming cannot satisfy native selection/animation consumers, move ownership into a sidecar cache and hook those consumers together. A larger count alone is not the fallback.

## 7. Staged proof of concept and acceptance gates

### Stage 0 — baseline and resolve hook boundaries

1. Use a legitimate asset installation and a copied, hash-checked executable. Log actual loaded EXE/DLL paths, DirectDraw/GDI route, monitor/client pixels, DPI awareness, selected bpp, surface dimensions/bounds/pitch/masks, map/UI pane rectangles and source/destination rectangles. Observe original 800×600 gameplay before enabling rendering changes.
2. Trace representative full draw, selection, idle frame, camera move, minimap click, modal and world switch. Capture call sites/active view/port identities, not arbitrary screenshot-size heuristics. Confirm map placement `(10,58)` in the running current build and identify the exact map event entry, minimap click/indicator and tooltip/dialog roots.
3. Audit all tile-cache backlink consumers and allocator failure behavior. Add diagnostic guards around the inline map object and surface bounds in the experimental hook. Identify safe per-screen activation and teardown.
4. Require verified original bytes/vtable pointers, x86 calling conventions/stack cleanup, successful allocations/protection changes, atomic hook installation and rollback, and instruction-cache flushing. Do not copy the historical bridge's unvalidated runtime writes wholesale.

**Gate:** enough current runtime evidence to choose map-owned surfaces/events and a safe cache strategy. No claim of current runtime coverage is made by this report.

### Stage 1 — expanded native map, UI scale 1

1. Create a physical-resolution presentation target, initially in a window/borderless path to avoid dependence on modern exclusive 16-bit mode support. Keep native source formats where the game needs them; convert at presentation.
2. Give the active map a larger local rectangle and dedicated guarded target. Keep projection divisor at its existing value 1, and present the map with equal source/destination dimensions. Keep UI controls at native size, anchored to top/right/bottom.
3. Install the bounded cache strategy before asking Draw for more tiles. Make camera clamps/centering, minimap extents, actual hit bounds, immediate tile-draw clipping, invalidation and animation-retention bounds viewport-aware. Keep wrapping and world-variant policy intact.
4. Route direct focus/background/selection writes into the same map target. Begin with full-map composition after actual map changes for correctness; do not synthesize game ticks or permanently redraw on every mouse move. Preserve native tick timing and modal behavior.

**Gate:** at 800×600, 1920×1080 and 3840×2160, a tile/sprite covers the same physical pixels, more terrain is visible, and clicks/scroll/selection/idle updates agree. No cache memory overwrite, stale ownership or surviving fragments.

### Stage 2 — independently scaled UI

1. Introduce logical UI pane surfaces and configurable uniform scale, with 1, 1.5 and 3 as initial test values. Reanchor panes, resize only the map's remaining physical rectangle, and publish a new geometry generation atomically.
2. Apply scale to UI output and its inverse to hit testing. Route map input independently. Cover UI capture/drag, double clicks, hover/tooltips, native dialogs and keyboard focus. Keep map position and native decoding authoritative.
3. Scale labels/tooltips about their native map anchors; retain readable minimap/UI text and update its viewport indicator. Preserve native map/sprite dimensions when UI scale changes.

**Gate:** changing s moves/resizes UI and may resize the available map rectangle, but a 64-pixel map pitch remains 64 physical pixels. UI clicks and map clicks remain aligned at every scale.

### Stage 3 — incremental rendering, performance and release hardening

Map dirty rectangles and all region clips through the same generation; compare incremental output to a forced full draw. Reuse buffers/DCs and release them on resize/destruction. Measure CPU map draw time, cache misses/evictions, tick cadence, dirty pixels, upload/present time, p50/p95 frame time, memory and GDI handle counts. Disable synchronous per-blit file logging while measuring.

At 4K, one 32-bit full output is about **31.6 MiB**; the Stage 1 guarded 3744×2200 native 16-bit map target is about **15.7 MiB**, before pitch, other surfaces and copies. These are calculated storage estimates, not measurements. A 3616×2072 map covers about **25.4×** the original map area; native CPU rasterization may dominate. Establish a named test machine and a provisional 30-fps interaction budget (p95 ≤33.3 ms), then use measured baseline/4K data to decide whether GPU composition, dirty uploads or tile batching is necessary. Keep tick scheduling independent of presentation speed.

### Validation procedure at each required resolution

Run the same saved scene/camera at **800×600**, **1920×1080**, **3840×2160**, first s=1 everywhere, then the proposed UI scales. Include UI scale changes without restarting and a return to original resolution.

- **Rendering/density:** screenshot an unanimated tile/landmark and sprite; compare native pixel runs/pitch, not only visual size. Interior map pixels should match a reference crop at the same world camera. Check partially visible tiles, guard edges, masks, fog/coasts, labels and Old/New World. Ensure both newly exposed right/bottom areas contain distinct world content.
- **Clipping/redraw:** select/unselect units at every edge and beyond the old 576×512 rectangle; leave the pointer still for ten seconds; test background restore and scroll-induced exposure. Compare a dirty render to a full redraw. Check UI occlusion and restored clip state after tooltips/modals.
- **Input alignment:** click centers and edge/sub-tile regions on even/odd rows, the old viewport seam, new physical corners, horizontal wrap seam and north/south limits. Compare the native decoder's column/row/subregion with the rendered tile; test hover, right click, drag/capture, double click and UI boundaries. Include fractional UI scale and 100/150/200% Windows DPI as separate variables.
- **Navigation:** keyboard/edge/half-step scrolling, minimap recenter and indicator, selected-unit recenter, seam crossing, extreme camera positions. Verify the bottom clamp uses the actual map height; no black/stale rows, premature animation deletion or wrong-world wrap policy.
- **Lifecycle:** load/new game, world switch, combat/non-map screen, dialogs/tooltips, alt-tab, minimize/restore, resize, DPI/monitor change and display-loss recovery. Confirm map hooks are inactive for unrelated views.
- **Performance/safety:** pan continuously and animate a unit-rich scene with logging disabled. Record the metrics above and cache integrity; compare idle versus input-driven work. Check for allocation failure, signed coordinate overflow, cache index 110, tile backlinks pointing to another owner, leaked GDI handles and memory growth.

These are proposed tests; they have **not** passed merely because the static traces or historical headless fixtures exist.

## Unresolved questions and precise next probes

- **Actual accepted mode / live surface route:** trace the DirectDraw4 calls and GDI destination identities in a baseline process; static imports do not reveal an installed wrapper or its runtime policy.
- **Pane placement and full UI ownership:** inspect active view bounds and log Draw entry/exit with current port/DC. Confirm separate capture points for top/right/bottom panes, modal roots and tooltips before implementing a compositor.
- **Minimap live binding and edge autoscroll:** confirm the recovered minimap owner/child chain during one click, verify units passed to SetScreenSize, and trace the exact screen-edge autoscroll event/boundary tests. Corroborate the world-variant flag and `0x0075C048` initialization before treating that horizontal limit as generic wrapping.
- **Cache streaming compatibility:** enumerate signed-byte `tile+0x13` loads and inline cache references; exercise selection/background restore after eviction. Promote streaming from proposal to confirmed-safe only after these consumers regenerate or use the correct owner.
- **All overlay/animation paths:** log map owner, port and call stack for writes to the presentation target outside Draw; compare idle and selection traces. Existing historical evidence already warns that Tick-only scoping misses direct writes.
- **Regional clips and transitions:** inspect arbitrary GDI regions and view teardown/recreation. Verify that resize/world switches clear saved backgrounds and atomically replace geometry rather than mixing generations.

Coverage is strongest for static display setup, map iteration/cache, camera/lookup and minimap dispatch. UI ownership, minimap runtime bindings, edge autoscroll and runtime edge/performance validation remain partial. Those are explicit Stage 0 gates, not assumed implementation details.
