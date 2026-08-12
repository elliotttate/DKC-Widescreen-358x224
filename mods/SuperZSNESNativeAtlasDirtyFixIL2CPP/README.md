# SuperZSNES v0.300 native atlas dirty fix

This BepInEx 6 IL2CPP startup plugin corrects the 2/4/8-bpp atlas page dirty
assignment in the exact 32-bit SuperZSNES v0.300 `GameAssembly.dll`. The stock
native accessors mark an atlas page dirty before checking whether the requested
SNES tile changed. That causes unchanged pages used by the legacy tile renderer
to be uploaded every presented frame.

The earlier managed experiment wrapped all three accessors with Harmony. It
proved the bug but added callbacks to roughly 1.26 million clean-tile accesses
per benchmark trial and raised presentation time 37.1%. This project instead
rewrites six verified native instruction windows once during plugin startup:

| Accessor | Removed store RVA | Dirty-path hook RVA |
|---|---:|---:|
| 2bpp | `0x003A956E` | `0x003A95A0` |
| 4bpp | `0x003A9A5E` | `0x003A9A90` |
| 8bpp | `0x003A9FBE` | `0x003A9FF0` |

The hooks enter tiny native x86 trampolines only after the existing per-tile
dirty test succeeds. Each trampoline sets the already-validated page boolean,
replays the displaced instructions, and returns to the stock body. There are
zero managed hot-path callbacks.

Safety properties:

- full `GameAssembly.dll` SHA-256 gate;
- exact expected-byte verification at all six sites before any write;
- hooks are installed before stores are removed, preventing an under-dirty
  intermediate state;
- rollback on partial failure and plugin unload;
- changed sites are never overwritten during rollback;
- the on-disk game DLL is never modified; and
- disabled by default.

Build and verify:

```powershell
& .\verify.ps1 -GameRoot '<verified-superzsnes-v0300>'
```

Install into a closed disposable copy for A/B testing:

```powershell
& .\install-plugin.ps1 -GameRoot '<superzsnes-v0300-dev>' -Enable
```

The optimization affects only the legacy Unity tile renderer. Supported DKC
frames presented by `SuperZSNESDKCFramebufferRendererIL2CPP` already bypass
these accessors, so benchmark it with framebuffer presentation disabled before
deciding whether it is useful for fallback, transition, UI, and Mode 7 frames.

## Benchmark disposition

Four fresh-process trials per configuration used 12 seconds of warmup and
approximately 20 seconds of measurement with the stock renderer. The native
patch was active at all six sites and reported zero managed hot-path callbacks.

| Metric | Stock | Native fix | Difference |
|---|---:|---:|---:|
| Process CPU cores | 1.2350 | 1.2234 | -0.94% |
| `GenerateBackgrounds` ms | 2.6249 | 2.6356 | +0.41% |
| Unity `Update` ms | 4.5180 | 4.5167 | -0.03% |
| Emulated FPS | 59.9978 | 59.9970 | -0.001% |

The differences are smaller than run-to-run noise and presentation did not
improve. This source-equivalent patch therefore confirms that eliminating the
false page assignments is not a material v0.300 optimization in the tested DKC
workload. Keep it disabled; retain it as a verified reference patch for future
fade/UI/Mode 7 studies or an upstream correctness change.

The benchmarked DLL SHA-256 was
`5F1931D49993EA5891C5AE699A482CFE5C59AAD8EC04D2CABE1331DD3BC9BB39`.
The final build adds failure-only immediate rollback hardening and has SHA-256
`C12FE2CDDEB12158A3B31A3B11F87F8C0D251CBFD132BE21612A5218AA05C68E`;
the successful patch and trampoline bytes are unchanged.
