# DKC Softlock Watchdog

`DKCSoftlockWatchdog` is an isolated BepInEx 5 diagnostic plugin for Donkey
Kong Country in SuperZSNES v0.230. It turns the object-lifecycle facts already
corrected in `DKCObjectLifecycleTracer` v0.2.1 into persistent real-time
watchpoints. It does not reference or modify the tracer's files.

The default is observation-only:

- it samples once after each emulated frame;
- it reads WRAM and the current ROM's authored object data;
- it writes status and evidence files;
- it does not write WRAM, ROM, input, save states, renderer state, or timing;
- it does not pause the game or request other plugins' captures; and
- it does not open or contact the `DKCLevelAutomation` bridge.

Pause and external `capture.request` actions exist, but both are independently
disabled by default and must be enabled in config or through an explicit file
command.

## What is watched

The frame sampler records the exact gameplay context needed to interpret DKC's
bank-BD object scanner:

- level `$30`, entrance `$3E`, game state `$2E`, and operating mode `$A75`;
- camera/layer position, `$1B23/$1B25` camera bounds, and `$EF/$F1` scanner
  edges;
- `$A0/$A2/$A4` scanner cursors/current record;
- all 25 bank-BD allocation slots at raw even indexes `$02,$04,...,$32` with
  actor ID, position, motion, state, animation, graphics, and `$15FD` source
  record;
- the complete `$192B-$1A2A` bookmark table;
- type-9 state, pending descriptor, and current/pending ranges at
  `$1E03-$1E0D`; and
- the entrance's complete authored object list, type-5 children, and type-9
  range descriptors.

The same gameplay gate as the corrected tracer is enforced: entrance must be
below `$E6`, camera bounds must be nonzero, and upper must not be below lower.
Map/menu/transition WRAM is never interpreted as an actor/object context.

The decoder follows bank-B5 sprite-spawn-script inheritance (`Op82`) directly
from ROM so it can identify camera objects, exits, barrels, and controllers
before they allocate an actor. The watched critical set is:

- camera object `$5D`;
- normal and underwater exits `$6A/$6B`;
- barrel, rope/oil/DK/TNT barrels, barrel cannons, checkpoints, enemy-spawn,
  light-switch, and minigame barrels;
- group/light/minigame/boss/credits controllers;
- every type-5 group parent and its decoded children;
- every type-9 section controller and its authored range descriptors; and
- unresolved type-2 records, conservatively, so an unfamiliar ROM cannot
  silently hide a camera/exit-style record.

## Trigger conditions

Single-frame stale state is not called a softlock. The detector primes one
baseline frame after an entrance change or state-load rewind and then tracks
consecutive gameplay frames. A condition fires only once per continuous run
and is eligible to fire again only after recovery and the cooldown.

| Condition | Default persistence | Interpretation |
| --- | ---: | --- |
| `eligible_without_allocation` | 12 frames | Critical record is in its type-specific activation window and active type-9 range but has neither a `$192B` booking nor a source-linked actor. |
| `booked_actor_missing` | 4 frames | A critical record's actor/bookmark ownership broke while the record remained eligible. This covers a booked actor disappearing prematurely without treating normal out-of-window cleanup as failure. |
| `type5_child_missing` | 8 frames | Type-5 parent is eligible and marked `$FF`, but a child has neither bookmark nor actor. |
| `allocator_exhaustion` | 12 frames | The relevant primary or secondary pool has zero free IDs while an eligible critical record remains unallocated. “Low slots” is not called exhaustion. |
| `type9_range_contradiction` | 3 frames | Current range is inverted/out of bounds, is not an authored forward/reverse pair, or its primary cursor is outside the active range. |
| `type9_pending_contradiction` | 3 frames | `$1E05` does not name an authored descriptor, pending bounds do not match it, or `$A2` contradicts the pending state. |
| `scanner_window_contradiction` | 3 frames | `$F1` is below `$EF`. |
| `exact_allocator_exhaustion_witness` | immediate | Optional instruction hook saw verified `$BDF3B1` or `$BDF3D2` with every slot occupied for the eligible critical `$A4` record. |

Type-14 and type-12 special-child requests use the secondary `$1E..$32` pool;
ordinary records and type-5 roots/children use `$02..$1C`. `$192B` is decoded
as the raw even actor index, not a slot ordinal. `$FF` is accepted only as the
type-5 root marker.

## Atomic evidence

On a trigger, the RunFrame postfix clones that exact 128 KiB WRAM image and
queues it. Unity `Update` then writes both files to a same-volume staging
directory and commits them together with a directory rename:

```text
BepInEx/plugins/DKCSoftlockWatchdog/
  status.json
  Sessions/<launch timestamp>/
    events.jsonl
    Triggers/<UTC>-f########-<condition>/
      wram-7e7f.bin
      evidence.json
```

`evidence.json` includes the snapshot SHA-256, all authored records and group
children, every actor slot (including free slots), all 256 bookmarks,
camera/scanner/type-9 context, both pool free lists, opcode-validation status,
and every condition that fired on the frame. Consumers never observe a
half-written trigger directory.

`status.json` is atomically replaced and reports arming, action, hook, opcode,
gameplay-gate, entrance, and decoder state. `events.jsonl` records committed
captures and post-commit action results.

## Safe action path

Trigger detection occurs in the emulated-frame postfix, but evidence I/O and
optional actions are queued to Unity `Update`. This prevents worker/network
threads from touching emulator state.

`PauseOnTriggerAtStartup=false` by default. When explicitly enabled, the
watchdog calls SuperZSNES `PauseGame` from Unity's main thread only after the
WRAM/evidence directory has committed. The existing automation plugin will
then report the paused state normally; the watchdog never contacts its TCP
bridge.

`RequestExternalCapturesAtStartup=false` by default. If enabled, each
semicolon-separated `ExternalCaptureTargets` item is treated as either an
absolute directory or a directory name below `BepInEx/plugins`. The watchdog
atomically creates `capture.request` only when one is not already pending.
For example:

```ini
[Actions]
RequestExternalCapturesAtStartup = true
ExternalCaptureTargets = DKCObjectLifecycleTracer;DKCTilemapInspector;SuperZSNESDKCFramebufferRenderer
```

Only configure targets known to consume `capture.request` from their own
Unity update loop.

## File control

Create a request file in `BepInEx/plugins/DKCSoftlockWatchdog/`. Commands are
consumed on Unity `Update` and deleted:

| Request file | Effect |
| --- | --- |
| `capture.request` | Commit a manual WRAM + JSON evidence pair; optional text becomes the reason. Never pauses or fans out. |
| `arm.request` / `disarm.request` | Start/stop condition evaluation and reset persistence history. |
| `reset.request` | Reset entrance, decoder, witnesses, and persistence context without changing the game. |
| `instruction-witness-on.request` / `instruction-witness-off.request` | Add/remove the expensive per-instruction exhaustion witness hook. Enabling is refused unless runtime opcode validation passed. |
| `pause-on-trigger-on.request` / `pause-on-trigger-off.request` | Opt in/out of main-thread pause after a future trigger. |
| `external-captures-on.request` / `external-captures-off.request` | Opt in/out of configured `capture.request` fan-out after a future trigger. |

BepInEx writes the persistent configuration to
`BepInEx/config/dev.local.superzsnes.dkcsoftlockwatchdog.cfg` after first load.
Persistence thresholds and individual conditions are configurable there.

## Exact opcode trust boundary

The optional instruction witness is activated only when runtime ROM reads
match byte-exact signatures for:

- primary search/failure/success at `$BDF3A2/$BDF3B1/$BDF3B5`;
- secondary search/failure/success at `$BDF3C3/$BDF3D2/$BDF3D6`;
- type-9 initialization at `$BDFDBD`;
- type-9 commit at `$BDFF85`; and
- pending cleanup at `$BDFF95`.

This preserves the corrected v0.2.1 meaning: `$BDF3B1/$BDF3D2` are exhaustion,
while `$BDF3B5/$BDF3D6` are success. Frame watchpoints remain available when a
custom ROM fails this signature gate, but evidence says validation failed and
the PC witness hook stays uninstalled.

## Build and offline validation

```powershell
.\build.ps1 `
  -BepInExRoot '<BepInEx-5-x86-root>' `
  -GameManagedDir '<SuperZSNES-v0.230>\SUPERZSNES_Data\Managed' `
  -CleanRomPath '<headerless DKC USA v1.0 ROM>'
```

The script builds the isolated DLL and runs pure model tests. Tests cover the
gameplay gate, bounded entrance decode, inherited sprite-script resolution,
object/group/type-9 decoding, persistence and recovery, missing ownership,
group children, exact pool exhaustion, pending contradictions, and PC
semantics. With `CleanRomPath`, it also requires the known USA v1.0 SHA-256 and
checks every byte of the runtime signatures. Without it, that copyrighted-ROM
test is reported as skipped.

The build script does not install, launch, stop, restart, pause, or contact an
emulator. An explicit install helper is provided for later use with a closed
SuperZSNES copy:

```powershell
.\install-plugin.ps1 -GameDir '<SuperZSNES-v0.230>'
```

## Limits

- A persistent non-definitive record condition is evidence for diagnosis, not
  proof that every authored object must respawn. `definitive` is true only for
  structural contradictions and zero-free-slot/exact-PC exhaustion.
- The optional instruction prefix runs for every 65C816 instruction and is
  intentionally off by default. Use it around a short reproduction.
- The runtime classifier understands DKC's standard bank-B5 spawn scripts and
  inherited `Op82` parents. Unknown custom script opcodes leave the record
  unresolved; type-2 remains conservatively watched.
- Auxiliary/effect actor indexes above `$32` are not bank-BD ownership slots
  and are intentionally excluded.
