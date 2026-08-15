# DKC WRAM Flight Recorder

`DKCWramFlightRecorder` is a short-replay provenance tool for the step after
`DKCFirstDivergenceLocator` has narrowed a failure to exact WRAM bytes. It records
who wrote those bytes and the immediately preceding CPU/write history.

The recorder is **disarmed by default** (`ArmedAtStartup = false`). While
disarmed it installs no Harmony hooks. Arming is an explicit, fail-closed
transaction: a bounded range plan and the exact SuperZSNES v0.230 runtime
contract must validate before either hot prefix is installed; any partial
failure removes both prefixes. Disarming removes both prefixes.

This is observation-only instrumentation. It never writes SNES memory, changes
controller input, pauses the emulator, loads a ROM/state, contacts an automation
bridge, or calls another plugin. The project has no install script by design.

## What a target write contains

Every JSONL row in `writes.jsonl` has:

- emulated frame, scanline, dot, monotonically increasing write sequence;
- 24-bit bus address, canonical `$7E/$7F` address, WRAM offset, pre-write byte,
  and requested new byte;
- the exact configured range and label that matched;
- current 65C816 PC, PB, DB, D, A, X, Y, S, flags, cycle count, and optional
  SuperZSNES opcode/disassembly text;
- bounded, oldest-to-newest arrays of preceding instructions and all preceding
  WRAM writes (not merely writes to selected ranges).

The write prefix runs before `MainMemoryMap.WriteMem`, so `oldValue` is genuinely
the pre-write byte. Native banks `$7E/$7F` and the SNES low-WRAM mirrors in
banks `$00-$3F/$80-$BF` are normalized. Other bus writes are ignored.

Hot hooks enqueue target evidence only. `writes.jsonl`, status, dumps, and
watchdog evidence observation are handled from Unity `Update`, not from the
emulated CPU/write prefix. The rings, target count, pending queue, range count,
total selected bytes, and emulated replay duration are all bounded.

## Build and offline verification

From this directory:

```powershell
.\build.ps1 `
  -BepInExRoot '<BepInEx 5 root>' `
  -GameManagedDir '<SuperZSNES v0.230>\SUPERZSNES_Data\Managed'
```

The script builds the DLL, runs 16 offline C# model tests, runs 8 Python report
conversion tests, then verifies the real v0.230 managed assembly and the plugin.
It does not copy/install anything and does not inspect or contact an emulator
process.

The runtime and offline verifier both require:

| Contract item | Exact value |
|---|---|
| `Assembly-CSharp.dll` bytes | `612352` |
| SHA-256 | `33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED` |
| MVID | `11738189-56ff-499d-8e00-b87cfb7f66eb` |
| `CPU65c816.ExecuteNextInstruction()` token / IL bytes / IL SHA-256 | `0x060004BB` / `14028` / `3931A27E4F8B3C6F5EAEAA192E4DABC053101FA2C3EEDA8B31B838CB08DE172F` |
| `MainMemoryMap.WriteMem(uint, byte)` token / IL bytes / IL SHA-256 | `0x0600056E` / `209` / `1640D72CEE188DC079AFC641E4AE3EE8755C7DC5499D87B5A5279B83E46F6A9C` |

CPU/register, master timing, and main-RAM field/method shapes are also checked.
This recorder has no hard-coded DKC ROM PC or opcode trigger: it observes the
generic emulator instruction and WRAM-write boundaries. Therefore clean-ROM
PC/opcode validation is not applicable here; adding a ROM-specific PC condition
later must add an exact clean-ROM opcode gate before it can arm.

## Prepare ranges from FirstDivergence

The helper reads `DKCFirstDivergenceLocator` schema 1 and defaults to the
case's `difference.selectedMemory.ranges`. It rejects truncated, empty,
overlapping, out-of-WRAM, or over-limit plans.

```powershell
python .\prepare_ranges.py D:\Evidence\run\report.json `
  --case szst0/right-swim `
  --output D:\SuperZSNES\BepInEx\plugins\DKCWramFlightRecorder\control\ranges.txt
```

Use `--validate-only` to inspect the selected range/byte count without writing.
`--kind allMemory` is available when the wider difference is intentional.

The plugin's strict text grammar is one non-overlapping range per line:

```text
$7E192B-$7E1930 scanner-bookmarks
0x1A5B+2 layer-x
$7F0010 single-upper-bank-byte
```

Addresses are hexadecimal full `$7E/$7F` addresses or offsets `00000-1FFFF`.
`+length` is decimal unless prefixed with `0x`. Text after the first whitespace
is the evidence label; `#` starts a comment.

## Arm, dump, and disarm

After a human installs the already-built DLL and creates `control/ranges.txt`,
file requests below the plugin directory are sufficient. Requests are claimed
by rename and consumed only on Unity's main thread.

| Request | Contents | Effect |
|---|---|---|
| `control/arm.request` | empty, or an absolute alternate ranges path | Validate plan/runtime, create a session, then install both hot prefixes |
| `control/dump.request` | optional reason text | Atomically commit a bounded ring snapshot as `dump-NNNN.json` |
| `control/mark.request` | marker text | Append a marker with current instruction/write sequence |
| `control/disarm.request` | optional reason text | Dump, drain target rows, remove both prefixes, and close the session |
| `control/watchdog.request` | exact watchdog `evidence.json` or trigger directory | Read-only evidence correlation, hash/log it, and atomically dump |
| `capture.request` at plugin root | watchdog-created reason text; requires `ConsumeCaptureRequest = true` | Consume the optional cross-plugin request, correlate newest committed watchdog evidence, and dump |

`control/status.json` is atomically replaced and explicitly reports `armed`,
`hotHooksPresent`, both individual prefix states, limits/counters, current
session, last dump, last watchdog evidence, and any fail-closed error.

Session layout:

```text
Traces/<timestamp>/
  session.json       immutable plan, hashes, runtime contract, limits
  writes.jsonl       one complete provenance object per selected write
  events.jsonl       arm/marker/dump/watchdog/disarm events
  dump-NNNN.json     atomic bounded flight-recorder snapshot
```

Per-instruction instrumentation is intentionally expensive. Use a short,
deterministic replay and keep ranges narrow. Defaults auto-dump/disarm after 600
emulated frames or 100,000 selected writes; pending evidence overflow or any
hook/output exception requests a main-thread dump and fail-closed disarm.

## Optional watchdog correlation (read-only, uncoupled)

`Watchdog.ObserveCommittedEvidence = false` by default. If enabled before
arming, the recorder scans the configured evidence root for a newly committed
`DKCSoftlockWatchdog/**/evidence.json`. It reads and hashes that file, records
its path, and creates an atomic flight-recorder dump. It does not reference the
watchdog assembly, modify/delete its files, create watchdog requests, or ask it
to pause/capture. `DisarmAfterEvidence` is also false by default.

For a single explicit correlation, leave automatic observation off and put the
evidence path in `control/watchdog.request` while the recorder is armed.

The watchdog's existing optional external-capture convention is also supported:
set this recorder's `Watchdog.ConsumeCaptureRequest = true`, arm it, and configure
the watchdog to target `DKCWramFlightRecorder`. The watchdog writes
`DKCWramFlightRecorder/capture.request` only after its evidence commit. This
recorder consumes that request, locates the newest committed `evidence.json`,
hashes/reads it without modification, and dumps. Both sides of that integration
are off by default and there is no code or assembly reference between them.
