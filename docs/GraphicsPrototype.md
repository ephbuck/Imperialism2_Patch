# Graphics prototype: Stage 0

This implements the diagnostic and coordinate foundation from [the rendering plan](HighResolutionRenderingPlan.md). It **does not yet enable a higher-resolution map**. Native map rendering, bounded terrain-cache eviction, physical presentation and independent UI composition still need integration. No graphics patch has been applied to `ImpII.exe` or `dplayx.dll`.

## Implemented

- `src/MapViewportGeometry.cs`: immutable frame geometry, 1:1 map coordinates, independent anchored UI transforms, inverse hit testing, guarded surface extents, camera limits, dirty rectangles and generation IDs. It has no native process access.
- `src/graphics/observe-rendering.js`: signature-checked Frida observation at 12 native locations and six imported APIs. It samples structured events and counts calls, observes map/camera/input/minimap/copy/clip paths, and warns about cache indices outside 0..109. It never changes arguments/results or game data. Frida itself temporarily instruments process code; this is not passive tracing.
- `src/PrepareGraphicsSandbox.ps1`: copies legitimate installed assets and saves into an isolated `obj/graphics-game` folder with the exact researched executable. It rejects an existing sandbox rather than overwriting its saves.
- `src/ObserveRendering.ps1`: hash-checks and launches that copy under REA, from the required asset working directory, then writes a bounded complete Evidence record under `obj/`.

The existing production build/deployment script is unchanged. These files are opt-in developer tooling, not an installed graphics feature.

## Tests

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\src\TestMapViewportGeometry.ps1
node .\tests\TestRenderingProbe.js
node --check .\src\graphics\observe-rendering.js
```

The geometry suite passes **205 checks**, including original 800×600, 1920×1080, 3840×2160, 1/1.5/3 UI scales, fractional rounding, map density, clipping, camera limits, pane anchors and inverse transforms. Probe fixtures test callback reads, signed 16-bit tile outputs, sampling, signature refusal, partial-install rollback and cleanup. Fixtures do not establish native ABI correctness or gameplay coverage.

## Repeat a diagnostic capture

Supply an asset installation; it remains untouched:

```powershell
.\src\PrepareGraphicsSandbox.ps1 -GameDirectory 'X:\Games\Imperialism II'
.\src\ObserveRendering.ps1 -Seconds 60
```

The observer uses pinned `rea-agents@6.9.0` via `npx.cmd`, Node.js and compatible Frida bindings supplied by REA. To reuse an installed REA package, pass `-ReaScript 'C:\absolute\path\to\rea.mjs'`. The capture duration is 1–60 seconds. REA/Frida availability can differ between shell and MCP environments. A missing provider is an error, not an empty successful trace.

Click the intro to skip it, open the chest, select a copied save, then click its load icon. Observe map selection, minimap clicks and idle rendering without saving. The bounded launcher releases instrumentation at capture end; process lifetime depends on its owner and is not guaranteed. For longer interactive experiments use REA's persistent attach/load/status/close tools and explicitly clean up the owned test process. Do not load the probe into other executable versions.

The output records `ready`, sampled calls and periodic full counters. Check for `refused`, Frida `error`, `probe-error`, `cache-out-of-range`, retention truncation and cleanup errors. Absence of a sampled call does not establish that it never executed. The launcher rejects errors/refusal and requires a ready event; a menu-only capture remains insufficient for map acceptance.

## Live baseline collected

An isolated copy with executable SHA-256 `998384BB696F02D37A6725CEA33F98BC2CA711B803C2638BED07A91D687CA7CC` loaded the copied **Start Daerfentol** save. The probe passed all 12 native signature checks. A persistent REA session observed map draw, tile lookup, minimap tracking, camera changes and subsequent dirty map draws. The sandbox process was closed after detaching; no save was written during this test.

Complete REA records: [rea-runtime-baseline-bundle.json](evidence/high-resolution/rea-runtime-baseline-bundle.json). Final status Evidence: **`ev_61bdaabc9e1dae3398fb0531a90148cb3c70fb22fd2e94aed2d13b725e84a581`**. The bundle also preserves the earlier hook-load/status observations. Frida status records have no authenticated executable subject; the externally verified sandbox hash and probe signature checks provide the stated target association, not an intrinsic runtime Evidence digest.

Confirmed in this scene:

- Map vtable `0x00703900`, view origin **(10,58)**, size **576×512**; first full Draw receives local **(0,0)–(576,512)**.
- Initial camera **(4160,1344)**. A minimap click reached `0x0060F150` on vtable `0x00711758`, then the camera setter changed it to **(4928,1920)** and terrain visibly recentered.
- Minimap origin fields were **(30,2)** and displayed extent fields were **(10,9)**. These are larger than 576/64 by one column and 512/64 by one row. Preserve this edge-coverage convention when changing SetScreenSize; it is not simply an exact viewport width/64.
- Tile lookup received map-local points and returned column/row/subregion. For example local **(325,273)** at camera (4160,1344) returned column **69**, row **25**, subregion **3**.
- Final-map CopyBits used a source rectangle translated by the **64-pixel guard**. A dirty draw (288,64)–(352,128) copied source (352,128)–(416,192) into that same local destination.
- One observed cache state had **90 pinned entries** in the original view. This is not evidence that an enlarged view is safe.
- Camera-set can run before pane/cache initialization. One early sample contained uninitialized origin/size/cache-pointer patterns. Future hooks must distinguish initialization from an active rendered map; never dereference those fields just because the camera setter ran.
- No Frida script errors or `probe-error` events were retained; retention was not truncated. Timing under instrumentation is not a performance benchmark.

A separate startup capture under `obj/rendering-baseline.json` observed the game's **800×600×16** request and loaded the system `DDRAW.dll`, plus Windows compatibility modules. The game reached its menu. It did not reach the map during that bounded capture; it must not be presented as map acceptance.

## Next implementation gate

Before enabling an expanded Draw rectangle, complete bounded cache eviction/backlink handling and audit selection/background consumers. Use an active-map identity established by Draw, not initialization-time camera state. Then add the dedicated guarded map surface, viewport-aware immediate clipping/camera/minimap/animation bounds and 1:1 presentation. Only after those pass at the original size should the 1080p/4K geometry be connected to native rendering. Independent UI scaling follows that map acceptance gate.
