# SuperZSNES Mesh Bounds Optimization

This isolated, disabled-by-default BepInEx prototype changes only `PPURenderer.Process2DTiles` in memory. It does not alter Mode 7 `ProcessTiles`, sprite meshes, or the on-disk game assembly.

Stock code uploads every generated 2D tile mesh and immediately scans its vertices again:

```csharp
mesh.SetVertices(vertices);
mesh.SetUVs(0, uvs);
mesh.RecalculateBounds();
```

The prototype uses Unity 6000.3's flagged overload and a conservative local-space cube:

```csharp
mesh.SetVertices(vertices, 0, vertices.Length, MeshUpdateFlags.DontRecalculateBounds);
mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 4096f);
mesh.SetUVs(0, uvs); // original call/order remains in Process2DTiles
```

The helper sets bounds during each upload. This still makes one constant-time native bounds assignment per mesh, but replaces `RecalculateBounds`, whose work scales with the mesh's vertex count. By default it deliberately does not use `DontNotifyMeshUsers`; renderers observe each upload exactly as in the accepted v0.1 path. The optional `BatchMeshNotifications` experiment uploads both arrays with `DontNotifyMeshUsers`, then calls `Mesh.MarkModified()` once after assigning the fixed bounds. Geometry, UVs, materials, topology and bounds are unchanged; only the number of native renderer notifications is reduced.

## Bounds and culling safety

Unity documents `Mesh.bounds` as a local-space axis-aligned bounding box; `Renderer.bounds` is the transformed world-space box. A conservative local box therefore follows the existing `TileMeshData` transform normally.

`DrawLines` already clips every accepted 2D tile to the active horizontal viewport before `Process2DTiles` receives it. With the supported seven-tile extension, positions are approximately x +/-24, y +/-15, and z 0..13. Even PAL's larger scanline count remains within roughly +/-27 y. The default +/-2048 cube exceeds those coordinates by more than two orders of magnitude and also tolerates extreme custom scene widths.

The tradeoff is intentionally weaker per-mesh frustum and occlusion culling. These meshes contain only tiles already clipped to the current viewport, so active 2D tile meshes are expected to be submitted anyway. A very broad bound could still increase renderer work in unusual multi-camera, lighting, reflection-probe, or editor scenes; this is why the prototype stays off until paired timing and visual tests pass.

Do not use infinite or NaN bounds. The configurable half-extent is clamped to 64..32768.

## Configuration

```ini
[Optimization]
Enabled = false
BoundsHalfExtent = 2048
BatchMeshNotifications = false
```

Changing settings requires restart because this is a Harmony transpiler. `BatchMeshNotifications` is experimental and remains off unless a controlled visual/cadence A/B accepts it.

## Verification and A/B

Run `verify.ps1` to build and inspect the real v0.230 method. Verification requires exactly one stock `SetVertices(Vector3[])`, one `SetUVs(int,Vector2[])`, and one `RecalculateBounds()` in that order; then confirms the transformed method has one helper call, unchanged UV upload, no bounds recalculation, and unchanged instruction count. It also inspects the compiled helper for the flagged four-argument Unity overload and explicit `Mesh.bounds` setter.

For runtime A/B, pair this with `SuperZSNESCadenceCounter` using `RendererBreakdown=true`. Compare `GenerateBackgrounds` and BG-layer times, Unity Update rate, 0/1/2-frame batches, screenshots, and camera motion across native/widescreen/PAL/Mode 0-6 scenes. Reject for any edge disappearance, layer flicker, unexpected culling, or higher total renderer time.
