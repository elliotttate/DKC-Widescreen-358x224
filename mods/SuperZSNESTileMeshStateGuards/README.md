# SuperZSNES Tile Mesh State Guards

This isolated BepInEx experiment replaces six unconditional Unity setters in `PPURenderer.Process2DTiles` with equality guards: active state, world position, local scale, layer, shared material, and mesh. The per-frame `MaterialPropertyBlock` upload remains unconditional because the emulator mutates pooled blocks every frame.

The plugin is disabled by default and patches only in memory. It requires a restart.

```ini
[Optimization]
Enabled = false
UseSharedMeshSetter = false
```

This is intentionally independent from mesh-bounds, batching, and DrawLines cache experiments so it can be visually and quantitatively rejected with one switch.

`UseSharedMeshSetter` is a separate setter-only experiment. It leaves the six rejected equality guards off and assigns the emulator's already-pooled `Mesh` through `MeshFilter.sharedMesh` instead of the instance-oriented `MeshFilter.mesh` property. It does not alter mesh data or ownership and remains disabled until isolated visual/cadence testing accepts it.
