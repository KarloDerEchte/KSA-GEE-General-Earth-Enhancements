# GEE off-game tests

Checks the geometry and data logic of the mod in a few seconds, without starting KSA.

```
powershell -File tests\run-tests.ps1            # build + test + summary (about 4 s warm, 6-10 s cold)
powershell -File tests\run-tests.ps1 -Filter "FullyQualifiedName~RoadLayout"
```

Everything the build produces (test binaries, the TRX result, a Release build of `src` as `gee\GEE.dll`) goes to
`%TEMP%\GEE.Tests` or the folder in `-Out` / `GEE_TEST_OUT`. Nothing is written into the mod folder, `src/obj` is untouched.
`dotnet test tests\GEE.Tests` works as well (same output redirection through `tests\Directory.Build.props`).

## How it works

`GEE.Tests` is an xunit project that compiles the pure-logic files of `../src` in place
(`<Compile Include="$(SrcDir)X.cs" Link="src/X.cs" />`, never copied) and references only `Brutal.Numerics` from the
Ahwoo install (`-p:KsaDir=...` to change). Game-side types the compiled files touch are replaced by tiny stubs in
`GEE.Tests/Support/Stubs.cs` (`Tile`, `WaterTile`, `LakeInfo`, `WaterVertex`, `Log`); keep them in step with Tiles.cs / Water.cs / Log.cs.
If you add a file to `src`, add it to the test csproj only when it compiles without KSA types.

Compiled from src: Placement, Footprint, Earcut, Buildings, Roads, Landmarks, VectorTile, Pipelines/PipelineData, Structures/StructureData,
Road{Gore,Barriers,Merges,Bridges,Junctions,Layers,Wear,WearTag}, BuildingDetail, LightSet, BuiltUp, Clutter, WorldCover,
DownloadGate, Settings, ElevationData, RiverData, WaterGeometry, JunctionMarkings, RoadFurniture.

`SatellitePackTests` call the private `SatelliteImagery.Pack` / `Twist` by reflection on a built `GEE.dll`
(Satellite.cs holds Vulkan state and cannot be compiled without the game). run-tests.ps1 builds `src` to the scratch folder and passes
the DLL in `GEE_TEST_DLL`; without it the installed `mods/GEE/GEE.dll` is used.

## What is covered

| Area | Tests |
|---|---|
| Footprint, Earcut | min-area rectangle golden, hull, clipping, decomposition covers the outline; triangulation area = shoelace area, holes, no degenerate triangles, 40 random star polygons |
| VectorTile | tile bounds/TileAt round trip + golden (KSC = 14/4523/6832), hand-built MVT through `Parse`: building boxes, hide_3d, holes, edge clipping, road classes/widths/edge flags, junction points, aeroway, landmarks and pipelines in the launch tile |
| Roads.Classify | widths (oneway 60 %, min 3 m), lane variants, ramps one rank lower, tunnels/footpaths dropped, lift ranks vs tie-breaks |
| Placement | DirCcf/Axes golden + orthonormal everywhere, MakeInstance on flat/sloped/sub-sea terrain, roof handedness, dome, slab, FlatPull bounds, ModelToEgo |
| Road layout (AddRoadInstances) | pieces finite, width, no gaps, 0.1 m miter overlap, tile-edge ends exact, bends, terrain-following within tolerance, cross-slope shear, bridges/overpasses, lamps/runway lights, junction plain asphalt, date line and pole robustness |
| Buildings | FromRing sizes/heights/roofs/tanks, EstimateHeight buckets, colour -> variant, variant table consistency |
| Pipelines / Structures / Landmarks | every piece in exactly one tile at any zoom, total pipe length = data length, data inside the box `Pipelines.AddTo` early-outs on, landmark placement and `Adjust` |
| BuildingDetail | pieces valid/deterministic, which buildings get none, tank rings |
| LightSet | hash distribution, pinned hash values, Pack round trip, sort order |
| WaterGeometry / RiverMap / ElevationWindow | lake level from shore, area covered, holes, skipped water classes, river collection; river raster; Terrarium decode, window build exact for a linear field, wrap/pole tiles |
| Clutter | cube-face UV round trips, deterministic sub-cell positions, exclusion masks equal a brute-force "inside the footprint" test |
| WorldCover / DownloadGate / BuiltUp | synthetic TIFF + zlib block, file naming, 404 = ocean, 403 = refusal (not ocean), cache, eviction; pause doubling 5 -> 60 min |
| Satellite | 10-bit height packing round trip (error <= half a step), bit layout used by the shader, Twist |

## Real findings

* **RiverMap.Build signature is weak** (`RiverData.cs`): per line it hashes `LatLon[0] + LatLon[^1] * 3 + Width`, i.e. the first *latitude*
  and the last *longitude*. A line that changes only its last latitude or its middle points keeps the signature. Test
  `Signature_ChangesWhenANorthSouthLineIsExtended` documents it and is skipped (`Skip = "KNOWN BUG"`); remove the Skip when fixed.

## Candidates for extraction (not testable without refactoring src)

* `Satellite.cs`: the tile geometry / Mercator / height-grid code (`Geo`, `Emit`, `SampleHeights`, `Part`, `Merc`, `AncestorHeights`,
  the `words -> H0..H6` assembly) sits in a static class with Vulkan state. Only Pack/Twist are reachable (by reflection). Extract a
  `SatelliteMath` static class (pure) and `Emit` as a function returning a `SatInstance`.
* `Tiles.cs` (936 lines): `Tile`, `Place`, `BuildStreetLights`, `OnLaunchPad`, the per-tile pipeline from RawBuilding/RawRoad to
  instances is interleaved with `Celestial` terrain access and threads. A `TilePlacer(Func<double3,double> terrain, ...)` taking data in and
  returning instances/lights would allow whole-tile tests (clutter, pad clearing, lamps).
* `BuildingRenderer.cs`: frustum / pixel-size culling and the adaptive `LodScale` selection are inside the render loop with `Camera`.
* `FarField.cs`, `RegionalLights.cs`, `PublicLamps.cs`, `TreeCover.cs` (`LakeOcean` reference), `GroundTint.cs`: parsing, binning and
  thinning logic mixed with downloads and game types.
* `LakeOcean.cs` (1424 lines): lake union-find (`LakeInfo.Edges`), level percentile, lake map rasterisation (RGBA8 window) and the
  terraform depth maths are not separated from Vulkan resources. The level logic would be the first thing worth extracting.
* `Water.cs` `WaterField` / `Rivers.cs`: tile scheduling vs. geometry.
* `FacadeTechnique.cs`, `GlowTechnique.cs`, `Vapor.cs`, `PostFx.cs`, `ShaderPatch.cs`, `RoadWear.cs` (patch text): GLSL lives in C# strings
  inside classes that also hold pipeline code; `PatchVertex/PatchFragment`-style string functions could be tested against the real game
  shaders and glslc offline once separated.
* `Elevation.cs`, `Settings.cs` (de)serialisation: `Settings` compiles, but there are no tests for the JSON round trip yet.
* `Placement.cs` `AddPillars` / `RoundedPoints` / `Refine` are private; they are only covered through `AddRoadInstances`.
* `Axes()` returns NaN at exactly +-90 degrees latitude (cross product with the pole axis is zero); irrelevant for real places.
