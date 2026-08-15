# DKC Object Lifecycle Tracer

This is a focused BepInEx 5 diagnostic plugin for SuperZSNES v0.230. It answers
the questions that ordinary screenshots cannot answer when a DKC level becomes
impossible to finish:

- Was an authored level record inside or outside the scanner's activation
  window?
- Did the object scanner reject it, find no free actor slot, or mark it active?
- Which raw actor index and source-record index were assigned?
- Did `$192B` bookkeeping still point to a live actor?
- Was a type-5 group marked active while one of its children was missing?
- Which 65C816 PC last wrote the actor identity/source or bookkeeping byte?
- Which type-9 section-controller transition ran before ropes or enemies went
  missing?

The plugin does not modify gameplay, ROM bytes, controller input, save states,
renderer state, or emulation timing. It only samples WRAM and optionally
installs diagnostic Harmony prefixes.

## Captured state

Every emulated gameplay frame, the low-overhead sampler tracks:

- the bank-BD actor pool at raw even indexes `$02,$04,...,$32` (ID/name,
  world X/Y, speed, state,
  animation, pose, graphics, and `$15FD` source-record value);
- the full `$192B-$1A2A` object-to-actor bookkeeping table;
- Layer 1 and camera X/Y plus `$1B23/$1B25` camera bounds;
- the active bank-BD scanner window/cursors (`$EF/$F1`, `$A0/$A2/$A4`);
- type-9 section-controller state (`$1E03-$1E0D`);
- the current entrance's authored bank-BD object list, including decoded
  type-5 child records;
- structurally impossible bookkeeping values as definitive anomalies;
- transient/stale bookmarks, duplicate references, actor/source
  back-reference mismatches, and active groups with missing children as
  non-definitive observations.

`$192B` stores the raw 65C816 X actor index; it is not a zero-based slot
ordinal. Output therefore uses `actorIndex` (`$02,$04,...,$32`) and includes a
separate zero-based `poolOrdinal` only for display. Index `$00` is excluded
because the bank-BD allocator never allocates it.

The actor arrays and entrance fields are reused by DKC on maps, menus, and
transitions. Object tracing is active only when the entrance is inside the
exact `$E6`-entry `DATA_BD8000..DATA_BD81CB` table and camera bounds are
nonzero and ordered. Outside that context, `current.json` records
`objectTracingActive: false` and a reason, and deliberately emits no actors,
bookmarks, object decode, write trace, or scanner decisions. This prevents map
data from being mislabeled as repeated Diddy actors or invalid object lists.

Actor allocation, deallocation, replacement, bookmark changes, anomaly start,
and anomaly recovery are emitted as transition events instead of duplicating a
complete WRAM dump every frame. Non-definitive observations must persist for
three consecutive gameplay frames by default before their own start event is
emitted; change `ObservationPersistenceFrames` to tune that diagnostic filter.

Relevant-memory-write tracing is enabled by default. It records exact PCs for
actor identity/source, object-bookkeeping, and section-controller mutations.
Actor position/state writes are excluded from the JSONL stream by default to
keep it bounded, although the last-writer correlation remains available.

The optional scanner trace uses `CPU65c816.ExecuteNextInstruction` and records
only named decision points in `$BDF3A2-$BDFF95`, plus the widescreen helper
area. It names pool exhaustion, out-of-window rejection, accepted allocation,
group-child retry/cleanup, cursor seeking, and type-9 section transitions. The
hook is intentionally off at startup because a managed prefix on every SNES
instruction is expensive; enable it only around a short reproduction.

Pool-search semantics are opcode-verified: `$BDF3B1` is primary exhaustion and
`$BDF3B5` is primary success; `$BDF3D2` is secondary exhaustion and `$BDF3D6`
is secondary success. Scanner events also include the currently free raw actor
indexes in each pool. Tracer v0.1 had those success labels backwards;
`analyze_trace.py` corrects legacy scanner rows by PC before counting them.

## Output

Each emulator launch creates:

```text
BepInEx/plugins/DKCObjectLifecycleTracer/Sessions/<timestamp>/
  current.json       latest full actor/context snapshot and nearby objects
  events.jsonl       lifecycle, bookkeeping, level, and anomaly transitions
  writes.jsonl       relevant WRAM writes with PC and scanner context
  scanner.jsonl      optional semantic bank-BD instruction events
  capture-*.json     explicit complete authored-object captures
```

`current.json` is rewritten atomically every 30 frames and immediately after
interesting transitions. Session streams are append-only and can be read while
the emulator is running.

## File control surface

Create an empty request file below
`BepInEx/plugins/DKCObjectLifecycleTracer/`; it is consumed on Unity's main
thread and deleted:

| Request | Effect |
| --- | --- |
| `capture.request` | Write a complete capture. Optional file text becomes its reason/name. |
| `scanner-trace-on.request` | Enable the high-cost semantic bank-BD CPU trace. |
| `scanner-trace-off.request` | Remove the per-instruction Harmony prefix. |
| `write-trace-on.request` | Enable relevant WRAM write/PC tracing. |
| `write-trace-off.request` | Remove the `WriteMem` prefix. |
| `reset.request` | Clear diff/last-writer context without changing the game. |

The last command result is in `command-status.json`. This file-based surface
does not listen on a network port and does not require an MCP server.

## Recommended softlock workflow

1. Install the tracer, launch SuperZSNES normally, and load the reproduction
   state.
2. Create `capture.request` before moving. Preserve this as the known-good
   authored/object/actor baseline.
3. Create `scanner-trace-on.request`, reproduce only the short transition that
   loses the rope/enemy/barrel, and immediately create
   `scanner-trace-off.request`.
4. Create another `capture.request` at the stuck point.
5. Compare `events.jsonl`, `writes.jsonl`, and `scanner.jsonl`. A bookmark that
   changes to an actor index before that index is allocated, a pool-exhaustion
   decision, or a persistent type-5 root/child mismatch is surfaced directly.
6. Summarize the evidence with:

   ```powershell
   python .\analyze_trace.py '<session-directory>'
   ```

For an A/B ROM comparison, replay the same state and controller schedule in a
fresh emulator launch for each ROM. Each launch creates an independent session,
so traces cannot silently mix.

## Build and offline verification

```powershell
.\build.ps1 `
  -BepInExRoot '<bepinex-root>' `
  -GameManagedDir '<SuperZSNES-v0.230>\SUPERZSNES_Data\Managed' `
  -CleanRomPath '<headerless DKC USA v1.0 ROM>'
```

The build script also runs pure model tests covering WRAM aliases, raw actor
indexing, gameplay/map gating, bounded entrance decoding, lifecycle diffs,
bank-BD object/group decoding, observation/anomaly classification, and valid
bookmark/source ownership. It does not install the DLL or launch/stop the
emulator.

`CleanRomPath` enables byte-exact assertions at `$BDF3A2-$BDF3DE` and verifies
the known clean-ROM SHA-256 before accepting those PC meanings. It is optional
so the source can be built without distributing a copyrighted ROM; the test is
explicitly reported as skipped when no clean ROM is supplied.

Install into a closed SuperZSNES copy only when ready:

```powershell
.\install-plugin.ps1 -GameDir '<SuperZSNES-v0.230>'
```

Configuration is written to
`BepInEx/config/dev.local.superzsnes.dkcobjectlifecycletracer.cfg` after first
load. The tool is intended for short diagnostic runs; turn both trace hooks off
when measuring performance.

## Scope and caveats

- Symbolic PC meanings match the USA v1.0 DKC disassembly and this project's
  source-built widescreen patch. Unknown custom ROM rewrites can move code and
  reduce the labels to raw PC evidence.
- Actor indexes `$34-$72` are auxiliary/effect allocations that intentionally
  overlap DKC's structure-of-arrays layout. They are not part of the bank-BD
  level-object ownership pool and are excluded from consistency checks.
- A bookmark value of `$FF` is the type-5 group-root active marker, not an
  actor slot.
- Save-state loads can move the emulator frame counter backward. The tracer
  detects this and resets diff/last-writer context so unrelated timelines are
  not correlated. `analyze_trace.py` reports these independent timelines as
  trace segments and does not present repeated state replays as one continuous
  run.
- Tracer v0.1 labeled stale bookmark/source states as anomalies. The analyzer
  recognizes those legacy messages and reports them as observations so older
  sessions remain useful without overstating the evidence.
