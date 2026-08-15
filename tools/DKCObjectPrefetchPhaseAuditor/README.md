# DKC Object-Prefetch Phase Auditor

This isolated tool tests whether widescreen bank-BD object prefetch is merely
visual or advances gameplay behavior before stock DKC would create the same
object. It drives an **already-running** `DKCLevelAutomation` **v0.1.3** bridge
and uses one atomic 128 KiB `snapshot_wram` result after every completed frame.

Every case is strictly sequential:

1. load the stock ROM, reload the external state, install the exact controller
   schedules, run the complete stock timeline, and close its evidence archive;
2. load the wide ROM, reload the identical state, reinstall the identical
   schedules, and run the complete wide timeline;
3. align the two saved timelines by relative frame and map records through
   `$7E192B-$7E1A2A` bookmarks, raw even actor indexes, and signed/absolute
   `$7E15FD,x` source-record backlinks.

There is no lockstep, concurrent-emulator, or simultaneous-bridge assumption.
The auditor does not modify ROM/ASM bytes, launch/kill/install/restart an
emulator, arm diagnostic hooks, or write outside its report directory.

## Per-record report

Every dynamically observed or recipe-cataloged record gets:

- first observed wide active/bookmark frame;
- first observed wide actor-allocation frame;
- first observed stock active and actor-allocation frames;
- first stock eligibility frame when the catalog has record type and authored
  X;
- first counterfactual wide-window eligibility frame using the recipe margin;
- `earlyActiveFrameCount`: aligned frames where wide was active before the
  first stock allocation while stock was inactive;
- complete active and actor-allocation episodes for stock and wide, including
  left-/right-censoring, active-to-inactive `releaseFrame`, later
  `reallocation`, actor-index transitions, and start/end mappings;
- stock and counterfactual-wide eligibility episodes, including each
  became-eligible and became-ineligible transition;
- `stockCullGaps` pairing every stock release with its next stock allocation,
  the wide actor's continuity proof, and the later actor comparison;
- `continuousEarlyActiveFrames`: stock-inactive frames during cull gaps where
  one exact wide actor slot remains continuously allocated;
- wide-active frames before the cataloged record enters the stock window;
- the raw bookmark, source-record, and actor-slot mapping in referenced WRAM
  evidence;
- actor-field comparison at the exact first stock allocation frame, matched by
  record rather than by slot.

The actor comparison covers:

- identity: sprite ID and source record;
- position: world X/Y and OAM Z;
- motion: signed X/Y speed;
- state: actor state word;
- animation/render: displayed/current pose, animation ID/timer/speed/script
  index, and graphics;
- conservative collision candidates: the unnamed normal-sprite scratch words
  from `$0C35` through `$109D` that collision/interaction logic may consume for
  different actor types.

The report includes exact stock and wide actor addresses because different
replays may allocate the same source record into different raw slots.

## Classification

`harmless_visual_prefetch` requires all of the following:

- wide allocated earlier than stock;
- the same record still maps to an actor at the stock allocation frame; and
- identity, world position, motion, state, and every conservative
  collision-candidate word match. Exact matches and animation/render-only
  phase drift are allowed.

`behavior_phase_advancement` means wide allocated earlier and, by the stock
allocation frame, the wide actor was already gone or at least one identity,
position, motion, state, or collision-candidate word differs there. A relevant
difference without an earlier wide allocation is `behavior_phase_difference`,
not evidence of prefetch advancement.

The same classification also applies after a stock cull/reload cycle: when
stock releases a record, wide keeps one exact actor continuously allocated,
and behavior-relevant fields differ at the first subsequent stock-eligible
allocation (or at reallocation when eligibility is unknown). A matching later
comparison may be `harmless_visual_prefetch`; the root comparison is preserved
separately and never substitutes for the later lifecycle evidence.

The auditor also uses explicit non-conclusions:

- `indeterminate_without_stock_allocation`: wide allocated, but stock did not
  allocate within the horizon;
- `wide_persists_stock_culls`: stock released an allocated record, one exact
  wide actor persisted through the rest of the horizon, and stock did not
  reload it; this has disposition `indeterminate` and is never called harmless;
- `active_marker_without_actor_comparison`: a group/bookmark marker existed
  without a comparable actor;
- `mapping_inconsistent`: allocation evidence could not be mapped safely;
- `behavior_phase_difference`: stock allocated before wide;
- `synchronized_allocation` and `no_observed_allocation`.

These are evidence classifications, not causal proof. In particular, an
unnamed collision-candidate difference shows actor phase drift but does not
prove that a collision actually occurred.

### Poison right-Y lifecycle semantics

If a loaded Poison save already has records 55 and 56 allocated in both ROMs
at relative frame 0, those root episodes have `leftCensored: true`. Equal frame
0 actors only establish equal state at the observation boundary; they do not
establish synchronized spawning or lifecycle behavior.

For the observed record-56 shape where stock releases at frame 1, remains
inactive through frame 17, and reallocates/returns eligible at frame 18 while
wide keeps the same actor slot continuously:

- `stockReleaseFrames` contains `1` and `stockReallocationFrames` contains
  `18`;
- `continuousEarlyActiveFrames` is `17`;
- `stockCullGaps[0].comparisonReason` is
  `first_subsequent_stock_eligible_allocation`;
- `actorComparisonAtStockAllocationFrame` remains the left-censored frame-0
  comparison, while `classificationComparisonFrame` and
  `classificationActorComparison` contain the decisive frame-18 evidence;
- a position, motion, state, identity, or collision-candidate difference at
  frame 18 produces `behavior_phase_advancement`.

If stock does not reload record 56 before the horizon ends, the result is
`wide_persists_stock_culls` with disposition `indeterminate`, not
`harmless_visual_prefetch`.

## Recipes and offline validation

The format is defined by [recipe.schema.json](recipe.schema.json). Bundled
recipes are:

- [four-user-states-prefetch.sample.json](recipes/four-user-states-prefetch.sample.json):
  one focused case for each supplied state; the unlocked map is explicitly
  gated out of bank-BD interpretation;
- [poison-pond-prefetch.sample.json](recipes/poison-pond-prefetch.sample.json):
  neutral, RIGHT+B, and RIGHT+Y Poison Pond routes, with catalog entries for
  the early Mincers, barrel, camera objects, group parent, and child bookmarks.

Validate without checking ROM/state/endpoint files, reading `bridge.json`, or
opening a socket:

```powershell
python .\tools\DKCObjectPrefetchPhaseAuditor\audit_prefetch_phases.py `
  --validate-only `
  --recipe poison-pond-prefetch.sample
```

`automationContacted` is printed as `false`. `--case szst1/right-enguarde-b`
and path overrides can be supplied in validation mode to inspect the resolved
plan without accessing those paths.

## Controlled run

Coordinate with the SuperZSNES user first because a run intentionally changes
the loaded ROM/state and replaces controller input. Start SuperZSNES yourself
with `DKCLevelAutomation` v0.1.3 already installed, then run:

```powershell
python .\tools\DKCObjectPrefetchPhaseAuditor\audit_prefetch_phases.py `
  --recipe poison-pond-prefetch.sample `
  --stock-rom "D:\ROMs\Donkey Kong Country (USA).sfc" `
  --wide-rom "D:\ROMs\DKC_Widescreen_358x224.sfc" `
  --state-dir "D:\States" `
  --case szst1/right-enguarde-b `
  --automation-endpoint "D:\SuperZSNES\BepInEx\plugins\DKCLevelAutomation\bridge.json" `
  --output "D:\Evidence\poison-prefetch"
```

Repeat `--case` to select several cases or omit it for the whole recipe. Repeat
`--state STATE_ID=PATH` for nonstandard state filenames. The endpoint is
rejected unless it is loopback, protocol 1, and reports version `0.1.3`.
SuperZSNES remains paused after each exact-frame replay.

## Raw evidence

Every frame is retained, not only transitions. Each variant produces:

```text
stock-wram.frames.bin.gz     concatenated 128 KiB WRAM records
stock-wram.frames.jsonl      relative/emulator frame, SHA-256, offset, length
wide-wram.frames.bin.gz
wide-wram.frames.jsonl
```

Offsets in the JSONL index refer to the **uncompressed** fixed-record stream.
Each report evidence reference repeats the archive, index, frame, digest,
offset, and length. Extract and verify one frame with:

```powershell
python .\tools\DKCObjectPrefetchPhaseAuditor\extract_snapshot.py `
  --archive "D:\Evidence\poison-prefetch\cases\szst1\right-enguarde-b\wide-wram.frames.bin.gz" `
  --index "D:\Evidence\poison-prefetch\cases\szst1\right-enguarde-b\wide-wram.frames.jsonl" `
  --frame 42 `
  --output "D:\Evidence\wide-f00042-wram.bin"
```

The extractor refuses a partial record or SHA-256 mismatch.

## Important limitations

- A save state may already contain active records. Frame-zero allocations are
  marked `*AllocationLeftCensored: true`, and their root allocation episodes
  have `leftCensored: true`; the tool can say “active at the first observation,”
  not when they originally spawned. Releases and later reallocations remain
  observable even when the root episode is censored.
- Atomic WRAM identifies records already represented by bookmarks/backlinks.
  A complete authored entrance list is not available from the v0.1.3 bridge.
  The recipe catalog supplies known high-value records; unobserved,
  uncataloged authored records are omitted. This is deliberately documented
  rather than inferred from distributed ROM bytes.
- Static stock eligibility is available only when the catalog provides both
  record type and authored X. Types `$01/$02/$03/$05/$06/$08/$0A/$0E/$0F/$10`
  use the stock general window; `$04/$07` retain their native wider windows.
  Other types report unknown eligibility.
- `$FF` is a type-5 group-active marker, not an actor index. It counts as active
  but never as an actor allocation.
- Signed source records are canonicalized by absolute value, except `$8000`,
  which means no ordinary bank-BD source. Bookmark/backlink inconsistencies
  remain in the raw evidence and are not silently treated as good mappings.
- Relative-frame alignment assumes the supplied state and exact schedule are
  deterministic under both ROMs. The complete archives make that assumption
  independently auditable.

## Offline tests

Tests use synthetic WRAM and an in-memory fake bridge only:

```powershell
$env:PYTHONDONTWRITEBYTECODE = "1"
python -m unittest discover `
  -s .\tools\DKCObjectPrefetchPhaseAuditor\tests `
  -p "test_*.py" -v
```

They cover bookmark/source/slot mapping, negative source records, `$FF` group
markers, stock-window eligibility, early-active counting, harmless visual
prefetch, left-censored stock cull/reallocation with a continuous wide actor,
behavior-phase advancement, persistent-wide/no-stock-reload indeterminacy,
indeterminate wide-only allocations,
raw archive extraction/hashes, strict stock-then-wide orchestration, both
bundled recipes, and validate-only endpoint isolation.
