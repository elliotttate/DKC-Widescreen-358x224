# SuperZSNES Mesh Dynamic Upload Optimization

This isolated BepInEx prototype moves SuperZSNES's existing `Mesh.MarkDynamic()` call before the first mesh-data upload. It is disabled by default, patches only in memory, and is compatible with the separate mesh-bounds and tile-state experiments.

Stock `PPURenderer.GenerateNewMesh` creates all vertex/index/UV/normal/tangent data before applying the dynamic hint:

```csharp
Mesh mesh = new Mesh();
// allocate/fill arrays
mesh.vertices = vertices;
mesh.triangles = triangles;
mesh.uv = initialUvs;
mesh.normals = normals;
mesh.RecalculateTangents();
mesh.MarkDynamic();
```

The prototype preserves all data and call counts but changes the order to:

```csharp
Mesh mesh = new Mesh();
// allocate/fill arrays
mesh.MarkDynamic();
mesh.vertices = vertices;
mesh.triangles = triangles;
mesh.uv = initialUvs;
mesh.normals = normals;
mesh.RecalculateTangents();
```

Unity 6000.3 documents that `MarkDynamic` is most effective before the first vertex/index upload or first render. If called after buffers already exist, its effect applies when buffers are next recreated. SuperZSNES subsequently updates these pooled meshes every rendered frame in `Process2DTiles` or Mode 7 `ProcessTiles`, so they fit Unity's intended dynamic-buffer use case.

## Configuration

```ini
[Optimization]
Enabled = false
```

Changing this setting requires restart.

## Other audited ideas

- `Process2DTiles`'s two simple uploads can technically reach flagged Unity 6000.3 overloads, but Unity's public `DontNotifyMeshUsers` contract names the advanced buffer APIs and requires a later `Mesh.MarkModified`. Batching both simple channel uploads plus the fixed-bounds update may work, but needs runtime notification/culling validation before it is safe enough to prototype as a performance fix.
- `DontRecalculateBounds` on UV-only uploads is unlikely to save meaningful work because UVs do not determine geometric bounds. `DontValidateIndices` is irrelevant because these calls do not upload indices.
- `MarkDynamic` was already present, but too late relative to initial buffer construction. Repeating it per frame would only add native-call overhead.
- Redundant `TileMeshData` state assignments are already isolated in `SuperZSNESTileMeshStateGuards`; they are not duplicated here.

## Expected result and risks

The result is backend-dependent. If Unity was already recreating the pooled buffers after the late hint, expect near-zero improvement. If the late call left repeatedly updated buffers on a static strategy, moving it early can remove CPU/GPU synchronization during vertex and UV uploads. A reasonable expectation is zero to low-single-digit percent renderer improvement, not a guaranteed frame-rate increase.

Unity notes that dynamic buffers can be slightly slower for GPU reads than static buffers. These meshes are updated every frame, so the intended tradeoff favors dynamic uploads, but reject the experiment if renderer time, presentation rate, or frame-time variance worsens. Visual geometry is unchanged, but test native/widescreen, scene transitions, Mode 7, pause/resume, and PAL.

Run `verify.ps1` for the exact v0.230 IL check. It verifies the stock late order, the compiled helper's early order, removal of the late call, unchanged instruction count, and preservation of triangle, UV, normal, and tangent initialization.

Unity references:

- https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Mesh.MarkDynamic.html
- https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Rendering.MeshUpdateFlags.DontNotifyMeshUsers.html
- https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Mesh.MarkModified.html
- https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Mesh.SetUVs.html
