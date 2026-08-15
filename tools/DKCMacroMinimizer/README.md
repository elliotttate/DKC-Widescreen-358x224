# DKC deterministic macro minimizer

This standalone Python 3 tool reduces an exact controller macro while retaining
a user-defined WRAM success or failure outcome. It uses an already running,
paused `DKCLevelAutomation` v0.1.3 bridge and sequentially reloads the same
external save state for every candidate. It is independent of the route
explorer and does not modify automation-plugin source.

The minimizer is intended for questions such as:

- Which frames of a long underwater B/Y pulse route are actually needed to
  reproduce a softlock?
- How short can a successful exit or camera-trigger macro become?
- Is a neutral release interval part of the reproduction, or just surrounding
  input noise?

## Safety boundary

The live command temporarily replaces game state and controller input in the
target process. Never point it at active play or an emulator controlled by
another tool. Use a manually prepared dedicated instance.

The tool never launches, kills, restarts, or installs SuperZSNES. It never
loads or writes a ROM, writes WRAM, creates/edits a save state, or changes ASM.
It will not auto-discover an endpoint or connect anywhere except loopback.

Its complete live bridge allowlist is:

```text
status
load_state_file
snapshot_wram
schedule
run_frames
clear_schedule
```

The supplied ROM is a read-only identity input. The minimizer hashes it, hashes
the ROM path reported by the running bridge, and requires the digests to match.
It refuses a mismatched process rather than calling `load_rom`.

Live minimization additionally requires:

- an explicit v0.1.3/protocol-1 endpoint and exact expected PID;
- attached, loaded, paused, idle status with both frame/input hooks and no
  installed schedules;
- explicitly pinned state and ROM SHA-256 values;
- `--ack-live-control`;
- a new or empty evidence directory.

Every candidate reload verifies the root's complete 128 KiB WRAM bytes,
WRAM SHA-256, and emulator frame against the first replay. At completion or an
ordinary failure, the tool attempts to reload the root state, clear all
schedules, leave the emulator paused, and re-hash the state and both ROM paths.
A process crash or disconnected bridge can prevent cleanup, so inspect
`failure.json` before resuming the emulator.

## Offline validation

No endpoint file is read and no socket is opened by either command:

```powershell
$tool = '.\tools\DKCMacroMinimizer\macro_minimizer.py'

python $tool validate `
  --recipe .\tools\DKCMacroMinimizer\examples\underwater-failure.example.json

python $tool hash-inputs `
  --state 'D:\States\failure-root.immutable.szst0' `
  --rom 'D:\ROMs\DKC_Widescreen_358x224.sfc'

python -m unittest discover -s .\tools\DKCMacroMinimizer\tests -v
```

`validate` expands gaps and overlapping assignments with the same zero-based,
last-assignment-wins semantics as `DKCLevelAutomation`, prints the canonical
macro and transition signature, and reports `"liveBridgeContacted": false`.
It does not require the placeholder state or ROM paths to exist.

## Safe live protocol

1. Manually prepare a dedicated SuperZSNES instance with
   `DKCLevelAutomation` v0.1.3 and the exact intended ROM. This tool will not
   install, start, restart, or load it.
2. Copy the root state outside the emulator's normal state directory. Make the
   state and ROM read-only if practical.
3. Pause the emulator, clear all automation schedules, and stop every other
   input, stepping, state-load, regression, or debugger client.
4. Run the offline `validate` and `hash-inputs` commands. Confirm that the
   original macro, predicate, settle duration, transition policy, replay count,
   and evaluation budget are intentional.
5. Read the PID from this exact process's
   `BepInEx\plugins\DKCLevelAutomation\bridge.json`.
6. Run with explicit paths, hashes, PID, acknowledgement, and a new output
   directory.
7. Before using the emulator again, verify that the report cleanup fields
   `restoredRoot`, `rootWramVerified`, `scheduleCleared`, `stateHashUnchanged`,
   `romHashUnchanged`, and `loadedRomHashUnchanged` are all true.

Example with placeholders replaced:

```powershell
$tool = '.\tools\DKCMacroMinimizer\macro_minimizer.py'

python $tool minimize `
  --recipe .\tools\DKCMacroMinimizer\examples\underwater-failure.example.json `
  --state 'D:\States\failure-root.immutable.szst0' `
  --state-sha256 '<64 hex characters>' `
  --rom 'D:\ROMs\DKC_Widescreen_358x224.sfc' `
  --rom-sha256 '<64 hex characters>' `
  --endpoint 'D:\SuperZSNES\BepInEx\plugins\DKCLevelAutomation\bridge.json' `
  --expect-pid 12345 `
  --ack-live-control `
  --output .\tools\DKCMacroMinimizer\runs\failure-001
```

## Recipe model

The core fields are:

```json
{
  "schema": 1,
  "name": "example",
  "controller": 1,
  "state": {"file": "root.szst0", "sha256": "optional-in-recipe"},
  "rom": {"file": "game.sfc", "sha256": "optional-in-recipe"},
  "macro": "0-59=RIGHT+B;60-63=RIGHT;64-119=RIGHT+B",
  "outcome": {
    "label": "failure",
    "predicate": {"address": "0x7E0575", "size": 2, "op": "lt", "compareTo": "baseline"},
    "settleFrames": 8,
    "requirePredicateFalseAtRoot": true
  },
  "preserveTransitions": {"mode": "buttons", "buttons": ["B"]},
  "confirmationReplays": 3,
  "maxEvaluations": 500
}
```

`outcome.label` is descriptive (`failure` or `success`); in both cases, a
candidate is retained only when the predicate evaluates true. A leaf predicate
accepts a 1-4 byte WRAM `address`, `size`, `op`, and either a fixed `value` or
`"compareTo":"baseline"`. Operators are `eq`, `ne`, `lt`, `le`, `gt`, and
`ge`. Conditions compose recursively with `all`, `any`, and `not`; leaves also
support `mask`, `shift`, and `signed`.

`settleFrames` advances deterministic neutral frames after the candidate input
before observing the outcome. The exact controller override remains installed,
so physical input cannot leak into settling. By default the predicate must be
false at the freshly loaded root; set `requirePredicateFalseAtRoot` to false
only when intentionally minimizing input for an autonomous delayed outcome.

## Transition semantics

Frame deletion compacts time: retained frames keep their order but move next to
one another. Configure `preserveTransitions` according to the experiment:

- `false` or `"none"`: any retained-frame subsequence is allowed.
- `"all"` or `true`: the collapsed sequence of complete SNES controller masks
  must remain identical. Every original press/release/direction transition
  survives, although run durations can shrink.
- `{"mode":"buttons","buttons":["B","Y"]}`: preserve the collapsed
  projected on/off state sequence for selected buttons. This is useful for
  underwater pulse semantics while still allowing unrelated direction ranges
  to disappear.

Preservation concerns transitions, not durations. A `B` run lasting 12 frames
may reduce to one frame while its press and release ordering remains intact.

## Reduction and deterministic confirmation

The reduction order is deterministic:

1. confirm the original macro;
2. ddmin over constant-button macro segments;
3. greedily test progressively smaller contiguous frame ranges;
4. ddmin over remaining frames;
5. test every retained frame individually to prove 1-minimality under the
   configured transition policy;
6. run an independent final confirmation set.

Every live candidate—not only the final result—is replayed
`confirmationReplays` times from the root. All replays must agree on:

- predicate truth;
- complete final WRAM SHA-256;
- exact final emulator frame, verified as root + input + settle.

Any disagreement aborts the run as nondeterministic. Candidate results are
cached by their exact 16-bit button-mask sequence. `maxEvaluations` bounds
unique candidate sets; if exhausted, the best confirmed reduction is emitted
as `budget-limited` and is not claimed to be minimal. Otherwise the result is
`1-minimal-under-transition-policy`: no single retained frame can be removed
while preserving both policy and outcome. As with standard ddmin, 1-minimal is
not a proof of the globally shortest possible subsequence.

An autonomous outcome may reduce to zero input frames. The emitted replay
script represents that safely by scheduling one neutral sentinel but advancing
zero input frames, followed only by the configured neutral settle frames.

## Evidence

The new/empty output directory receives:

- `minimal.recipe.json`: canonical minimal macro, exact input/settle counts,
  retained and removed original frame ranges, input hashes, transition policy,
  independent confirmation evidence, and a bridge-compatible replay script;
- `report.json`: original/final confirmations, reduction decisions, hashes,
  status, cleanup, and whether the evaluation budget was exhausted;
- `trials.jsonl`: one compact record per actually executed candidate replay
  set, including predicate leaf values and final WRAM digest;
- `failure.json`: fail-closed error, completed trials, and cleanup results when
  a live run cannot finish safely.

Generated run directories belong under `runs/` and are ignored locally.
