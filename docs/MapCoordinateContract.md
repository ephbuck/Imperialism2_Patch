# World-map drawing, tile clicks, and zoom alignment

Investigation: November 12, 2026. Target: GOG/retail 1.03, repository branch `Main-World-high-res`.

## Findings and scope

The map can remain aligned at every zoom if terrain, animation, overlays, and input use **one recorded source-to-viewport transform and its inverse**. The current bridge has separate transforms and incomplete coverage of native drawing paths. Expanding an input point before pane routing, subtracting the map origin twice, or drawing a frame through a different transform changes which terrain tile it appears to occupy.

The first save, **Start Daerfentol**, was loaded during the preceding live investigation. At 0.75x the terrain and explorer initially scaled together, then an unscaled explorer/background patch appeared at the old screen position. With the pointer still, native map ticks, frame changes, and invalidations continued. This establishes a presentation failure in that scene; it does not establish that every animation class or idle path works.

REA 6.9.0 was used through its installed CLI to inspect the executable's PE imports and identity. Its native engines are not configured, its MCP tools are absent from this running session, and package-host DNS failed. Consequently **no REA native decompilation was performed**. Native instruction conclusions below come from direct PE inspection and the existing Capstone disassembler. These are distinct evidence sources, not reconstructed original C++ source.

| Evidence | Scope |
| --- | --- |
| [REA PE import record](evidence/rea-pe-imports.json), ID `ev_06f1090c86be4e1e8068e330c838c4513e7d141721ecfc3a8fdebb7ac79b892c` | Static PE identity, x86 architecture, imported APIs and IAT locations; no execution or native function bodies |
| [Verified byte anchors and vtables](evidence/native-map-coordinate-anchors.json) | Actual executable addresses and instruction signatures |
| [Native disassembly](evidence/native-map-disassembly.txt) | Projection, lookup, localization, buffer copy, rounding, camera/retention and focus drawing instructions |
| [Historical live trace](evidence/map-alignment-runtime-excerpt.txt) | The preceding live session; frame progress and raw blits outside covered scopes |
| [Contract test results](evidence/map-coordinate-contract-results.txt) | Production C# routing/sampling/hooks with controlled native decoder and pane fixtures |

Executable SHA-256: `1BE0901F3FC2B5896122A470A05E8405CB7D8E3ABBD0425BE0EFEBCF5C071253`. The root and working-game executables match. The shipped `.map` file uses another link layout: its names help identify routines, but its numeric addresses must not be copied into hooks.

## The coordinate spaces

| Space | Origin and units | Where it belongs |
| --- | --- | --- |
| World | Tile columns/rows; 108 columns, 60 rows; 64px pitch, alternate rows stagger 32px | Tile identity, movement, camera |
| Native map-local | World pixel position minus camera position | Native Draw rectangle and ScreenToTile input |
| Tactical buffer | Native local position plus the 64px guard margin on both axes | Expanded terrain/animation composition |
| Canvas | Original 800x600 UI; map viewport starts at `(10,58)` and is 576x512 | Pane routing, sidebar, final map placement |
| Physical client | Canvas scaled to monitor resolution, including presentation offsets | Windows mouse messages and GDI window DCs |

The 64px buffer margin and the `(10,58)` canvas placement serve different purposes. Neither is the camera. In particular, the native map draw rectangle must start at local `(0,0)` for a full view; native drawing adds its own buffer margin.

At 3840x2160 in Fit mode, the canvas becomes 2880x2160 at physical `(480,0)`. At Stretch it fills the client and the X/Y display scales differ. Apply that display transform after map zoom, and invert it before native pane routing.

## How terrain and units are drawn

1. `TMapDialog::Draw`, actual implementation `0x0055D070`, receives a rectangle in map-local coordinates. At `0x0055D0C9` onward it copies the caller rectangle, then offsets a copy by `(64,64)` at `0x0055D140` for tactical-buffer access.
2. Tile projection uses `0x004050DD -> 0x0054F680`. Tile ID is split by 108; row and column are multiplied by 64, camera coordinates are subtracted, and odd rows add 32 in X. Horizontal presentation wraps over 6912 pixels. The routine writes 16-bit coordinates.
3. The native map uses a 7040x64 cache: 110 slots of 64px terrain, **not a transparent sprite sheet**. Tile imagery is copied into the expanded tactical buffer. At `0x0055D60A`, an available tile animation's virtual DrawNextFrame is called with `(64,64)` as its buffer offset.
4. The map's final native CopyBits call is at `0x0055D889`. The bridge downsamples its expanded source rectangle into canvas `(10,58)-(586,570)`.
5. The 800x600 canvas is presented at the physical display scale. Separate native focus/background and selection draws can also write directly to the presentation DC. Those paths need the same map transform when their coordinates describe a map frame.

The current local rectangle correction changes the expected full-map source origin from the erroneous `(74,122)` to `(64,64)`. Adding `(10,58)` to an already-local native rectangle was an extra crop of terrain, not a display translation. Headless tests cover the corrected contract; the corrected build still needs a live comparison.

| Zoom | Expanded source extent | Exact source pixels per canvas pixel X/Y |
| --- | --- | --- |
| 1x | 576x512 | 1 / 1 |
| 0.75x | 768x683 | 768/576 / 683/512 |
| 0.5x | 1152x1024 | 2 / 2 |
| 0.25x | 2304x2048 | 4 / 4 |

At 0.75x, multiplying both axes by exactly 0.75 disagrees with the rounded 683px source height. Record the actual source rectangle rather than recomputing an approximate scale from the requested zoom.

## How a click becomes a tile

The Windows input bridge should convert only physical coordinates to the 800x600 canvas. Native routing selects the pane and localizes the point. The view-localization routine at `0x00423BD0` subtracts the view position stored at `+0x24/+0x28`. The map's ScreenToTile hook therefore receives a **map-local point**. It must not subtract `(10,58)` again.

`ScreenToTile`, `0x004078C9 -> 0x0055A5A0`, then:

- Adds `[map+0x64]` to point X and `[map+0x68]` to point Y.
- Multiplies by 1/64. The static initializers at `0x005594E0` and `0x00559510` store this double into `0x0075C028` and `0x0075C000`.
- Converts to integers using `0x006816B0`, which explicitly selects **truncation toward zero**, not floor. Positive interior positions behave like floor; negative edge positions can differ.
- Uses the row parity for the horizontal 32px stagger. For an odd row, column conversion adds 32 and decrements the resulting column.
- Calls `0x00408279 -> 0x00550340`: column wraps by one addition/subtraction of 108, while row clamps to `0..59`. This is not unlimited modulo for arbitrarily distant coordinates.
- Writes **three 16-bit outputs**: column, row, and a `1..4` sub-tile region classification. The third output is not a boolean validity flag.

A downstream handler at `0x0060A476` calls that virtual lookup, constructs a tile ID, and dispatches different click actions according to modifier keys and tool state. This is static dispatch evidence, not proof that every possible click handler has been enumerated.

In native-style pseudocode, away from overflow:

```text
worldY = localY + cameraY
row = truncate(worldY / 64)
odd = row & 1
worldX = localX + cameraX
column = truncate((worldX + (odd ? 32 : 0)) / 64) - (odd ? 1 : 0)
normalize column once by +/-108; clamp row to 0..59
tileID = row * 108 + column
```

Keep the native lookup authoritative. The bridge should undo presentation geometry and hand the resulting native-local point to the original routine; it should not replace terrain/action rules with an independently maintained tile decoder.

## One transform for every layer

For each axis, let `L` be native map-local pixels, `M=64` the buffer margin, `S` the actual source origin, `V` the canvas viewport origin, `a` the actual source extent divided by canvas extent, `d` the physical presentation offset, and `t` the physical pixels per canvas pixel.

```text
buffer pixel B = L + M
canvas pixel C = V + (B - S) / a
physical pixel P = d + t * C

inverse click L = S - M + a * ((P - d) / t - V)
```

Native pane routing already computes `((P-d)/t - V)`. The tile hook only needs the remaining `S-M + a*localDisplayedPoint` step.

For an animation rectangle expressed in global canvas coordinates `G`, first recover native local coordinates with `G-V`, then apply the same forward mapping. Dirty rectangles use outward floor/ceiling; raster samples use the existing sampler's pixel-center/filter rules. Do not round at every intermediate stage.

The practical implementation should store an immutable map-transform snapshot containing the map/view identity, camera, source rectangle, viewport rectangle, display geometry, filter, and a generation number. Publish it when the corresponding full-map render is complete. Rendering, hit lookup, labels, dirty patches and direct frames must read the same snapshot. A requested zoom change must not mix a newly calculated animation scale with the last frame's old input transform.

Top-left anchoring and cursor-centered zoom are different camera policies. Either can work, but it must be explicit and update the snapshot atomically. A unit should stay on the same world tile across zoom; its physical screen position is expected to change when more territory becomes visible.

## Remaining defects to resolve before gameplay acceptance

| Finding | Required change or verification |
| --- | --- |
| Animation rectangle helper derives scale from zoom; terrain and clicks use the last source rectangle | Use the recorded transform. The new tests report three mismatches for an offset source `(74,122)`. This is a supported source geometry, not an accepted animation pass. |
| Live raw sprite/cache blits occur with `tick=False draw=False` | Trace the independent native focus IdleDraw/background-update callers and scope those exact paths, or compose them into the tactical buffer. Tick-only coverage is insufficient. Do not infer transparency or ownership from bitmap dimensions. |
| Old and New World DrawSelection have different implementations (`0x005599C0`, `0x0055B1D0`) | Preserve the original callback per vtable. The current installer stores just one original selection callback for all three vtables. |
| Selection brackets are currently suppressed below 1x | Implement aligned selection feedback through the shared transform; suppression does not satisfy selection acceptance. |
| 110 cache slots are reused while an expanded view can exceed 1000 tiles | Invalidate an evicted tile's cache-slot reference and label ownership. Bounding the allocation avoids overwrite but does not establish coherent terrain/label identity. |
| Partial native paint can write before/after full composition | Verify dirty regions, direct GDI writes, and saved-background reads independently. A correct full redraw can temporarily hide a broken incremental path. |

The temporary raw stack-memory scanner from the interrupted investigation was removed. Bounded coordinate/blit diagnostics remain behind `AnimationLog`; they do not read speculative native return addresses.

## Validation and next live comparison

The current source passes **218 map-coordinate contract checks**, **68 animation/input checks**, and **133 graphics checks**. The coordinate suite reports three known source-offset animation discrepancies separately. It exercises real ViewWndProc routing, the real tile hook, actual DIB pixels through DownsampleMapBuffer, and MapAnimationRectangle. Native pane routing and the native tile decoder are controlled fixtures recovered from instructions; the executable's native callback ABI and full gameplay are not executed by this suite.

The old animation/input test fixture was corrected to model native pane localization, rather than feeding global canvas coordinates into a local-coordinate hook. That correction changes what the test establishes.

```powershell
.\src\InspectMapCoordinates.ps1
.\src\InspectMapCoordinates.ps1 -Exe '.\Imperialism II - Patched\Imperialism II.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
& $csc /nologo /platform:x86 /unsafe /r:obj\Imp2Net.dll /out:obj\TestMapCoordinateContract.exe tests\TestMapCoordinateContract.cs
.\obj\TestMapCoordinateContract.exe
```

For the live test, load Start Daerfentol without issuing movement orders or saving. Record the explorer's tile ID, camera, native-local rectangle and neighboring terrain at 1x. Repeat at 0.75x, 0.5x, 0.25x, and back to 1x. At every step compare projected unit position, click-selected tile ID, hover terrain, and at least ten seconds of idle frame progress. Then test edge tiles, horizontal wrap, panning, selection changes, Old/New World, Fit/Stretch, and modal return. Use a diagnostic overlay showing the native tile ID and projected tile bounds to make a mismatch directly observable.

Further REA decompilation can corroborate the bounded static trace once a supported native engine is supplied. It must retain this executable digest and analyze the actual addresses above, rather than using the bundled linker-map addresses.
