# SuperZSNES Variable Material Batching

Retired runtime-only BepInEx prototype for SuperZSNES v0.230. `Prototype.Enabled` defaults to `false`; installing the DLL alone patches nothing.

> **Quarantined after live QA:** v0.1.0 produced a frame with the sky gradient but essentially all foreground/tile geometry missing. Disabling it and restarting restored the frame. No runtime exception survived the restart, and the exact offline IL, branch, argument-index, ordering, and topology models all pass. The remaining premise—using `RenderType=Opaque` plus render queue to prove that renderer boundaries can be collapsed—is not strong enough for SuperZSNES's SNES priority/compositing path. v0.1.1 therefore refuses `Enabled=true`, writes `quarantined-visual-failure`, and applies no Harmony patch.

## What it changes

Stock `GenerateBackground` calls `Process2DTiles` with greedy sizes 256, 64, 16, 4, and 1 for every `(Material, MaterialPropertyBlock)` list. Every productive chunk performs a separate mesh upload, bounds operation, GameObject/renderer setup, property-block assignment, and MeshFilter binding.

For an eligible list, the prototype replaces only the first `256` constant with `SelectFirstBatchSize(material, mct)`. It returns the entire list count, so the first stock `Process2DTiles` invocation consumes the list once; the remaining four stock calls see `mct == 0` and remain no-ops. Empty lists use size 1, and ineligible or oversized lists return 256 and preserve the complete stock decomposition.

Eligibility is intentionally conservative:

- `RenderType` must be `Opaque`;
- `material.renderQueue` must be at or below 2500;
- the list must contain at most 4,095 quads, keeping 16,380 vertices inside the UInt16 index limit.

The shared `mesh256` pool is also used by Mode 7 `ProcessTiles`. Both `Process2DTiles` and `ProcessTiles` therefore receive the same exact shape check before retrieving a pooled mesh. If a pool slot's prior vertex/UV arrays have a different length, the existing Unity `Mesh` is cleared and rebuilt with stock triangle winding, base UVs, normals, and tangents; no Unity Mesh or GameObject is created or destroyed by the prototype. Managed working/topology arrays are reused through length-keyed pools. `MarkDynamic` is not repeated during a reshape: every pooled mesh was already marked by stock `GenerateNewMesh`, and the separately tested per-frame/early `MarkDynamic` path regressed cadence badly.

## Ordering and residual risk

Within each material list, TileInfo order and triangle order are unchanged. The same material, property block, Unity layer, vertex z coordinates, and render queue are used. Renderer boundaries are removed only for opaque queues, where depth-writing makes chunk-to-chunk sorting immaterial under the stock tile shader.

This is still an experimental render-path change. Modded shaders can misreport `RenderType=Opaque`, and renderer-boundary-dependent effects are possible. Pixel comparison across scrolling, windows, mosaics, transitions, and enhanced/modded materials is mandatory before timing conclusions. Transparent queues retain stock batching.

The existing fixed-bounds transpiler is compatible: this prototype inserts its shape check before mesh retrieval and does not modify `SetVertices`, `SetUVs`, or `RecalculateBounds` instructions.

## Projection from the captured DKC map

The captured final-background map contained 91 material keys and 1,375 tiles. Without the individual list sizes, exact stock submissions are unavailable. Across every possible distribution over 91 non-empty lists, stock greedy decomposition requires 91 to 544 productive meshes. If every list passes the opaque eligibility checks, this prototype emits exactly 91: a projected reduction of 0 to 453 mesh submissions for that background. The extrema are constructive: 83 lists of 1, three of 4, and five of 256 yield 91 stock meshes; 90 lists of 15 and one of 25 yield 544. `status.json` records actual list counts, eligible lists, stock-projected meshes, selected-projected meshes, shape changes, and failures during a controlled run.

## Build and verify

```powershell
dotnet build .\SuperZSNESVariableMaterialBatching.csproj -c Release
dotnet run --project .\Tests\VariableBatching.Tests.csproj -c Release
dotnet build .\Verifier\VariableBatching.Verifier.csproj -c Release
.\Verifier\bin\Release\net472\VariableBatching.Verifier.exe
Get-FileHash .\bin\Release\net472\SuperZSNESVariableMaterialBatching.dll -Algorithm SHA256
```

The semantic test checks tile order and stock triangle winding for counts 0 through 4,095 and computes the 91-list/1,375-tile submission bounds. The IL verifier requires the exact five `Process2DTiles` call sequence and inserts exactly one selector plus one shape check in each shared-pool consumer.

## Historical controlled-test design

No files are installed by this project. Do not re-enable or retest v0.1.0. Version 0.1.1 is intentionally non-operational even if `Prototype.Enabled=true`; the implementation is retained only as an auditable record of the rejected experiment.
