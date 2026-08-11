# SuperZSNES Audio Timing Probe

Measurement-only BepInEx 5 plugin for SuperZSNES v0.230. It is **disabled by default** and installs no Harmony patches until `Probe.Enabled=true` is present at process startup.

## Measurements

- `MasterExecutor.RunFrame` wall-clock duration and start-to-start cadence, including 16.67 ms / 33.33 ms slow-frame counts and the longest consecutive slow-frame burst.
- `MasterExecutor.Update` duration and cadence, plus the number of `RunFrame` calls made by each host update (`0`, `1`, `2`, `3`, `4`, or `5+`).
- Frame-start gap counts above 25, 33.33, 50, and 100 ms, and the longest consecutive run of gaps above 25 ms.
- The exact scheduler decision after fast-forward scaling: accumulated time, target Hz, due frames, cap, scheduled frames, and due frames discarded by the cap.
- `DSPAudio.OnAudioFilterRead` wall-clock duration and start-to-start cadence.
- All four source-level `lock (bufferLock)` sites in `DSPAudio.AudioCycle`, separated into `voiceClear`, `keyOn`, `keyOnStart`, and `outputCommit`.
- Frame/audio temporal correlation per aggregation window: overlapping callbacks, callbacks within 1 ms of a frame boundary, and how many slow frames overlapped an audio callback.

The `AudioCycle` patch is a one-time Harmony IL transpiler. It replaces the four shipped `Monitor.Enter(object, ref bool)` calls with direct, signature-compatible methods and leaves every `Monitor.Exit`/exception region unchanged. The wrapper first performs `Monitor.TryEnter`. The common uncontended path does not call `Stopwatch` and uses only a plain main-thread attempt counter; only a failed try-enter is timed through the original blocking `Monitor.Enter`. This avoids a Harmony prefix/postfix or per-attempt atomic counter on the 32 kHz `AudioCycle` path while still finding contention at whichever of its lock sites encounters the audio callback. The main-thread invariant is verified by the v0.230 call chain `MasterExecutor.RunFrame -> CPUSPC700.UpdateAudioDSP -> DSPAudio.AudioDSPUpdate -> DSPAudio.AudioCycle`.

The `MasterExecutor.Update` transpiler inserts one direct call immediately after the shipped due-frame local is calculated. The source executes only `min(due, cap)` frames but subtracts all `due` frames from `_accumulatedDT`; therefore `max(0, due - cap)` is the exact amount discarded from the scheduler backlog in that update. The transform validates the `_accumulatedDT -> targetHz -> conv.i4 -> dueLocal` sequence and that the same due local and cap local reach `Mathf.Min`. A mismatch fails closed.

This is a diagnostic probe, not an optimization. Its wrapper and counters add some overhead while collection is active, so compare relative windows and corroborate any small effect with a probe-off run.

## Build

```powershell
dotnet build "<superzsnes-source>\Mods\SuperZSNESAudioTimingProbe\SuperZSNESAudioTimingProbe.csproj" -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File "<superzsnes-source>\Mods\SuperZSNESAudioTimingProbe\verify.ps1"
```

The project defaults to these existing local references and accepts `-p:BepInExRoot=...` / `-p:GameManagedDir=...` overrides:

- `<bepinex>`
- `<superzsnes>\SUPERZSNES_Data\Managed`

## Install and use

No automatic installer is provided. Copy only the built DLL to a dedicated directory such as:

```text
<superzsnes>\BepInEx\plugins\SuperZSNESAudioTimingProbe\SuperZSNESAudioTimingProbe.dll
```

Run once to generate `BepInEx\config\dev.local.superzsnes.audiotimingprobe.cfg`, close the emulator normally, set:

```ini
[Probe]
Enabled = true
WindowSeconds = 5
```

Then start the emulator. Enabling is startup-only so Harmony does not rewrite an audio callback while it may be running. When armed, `F10` pauses/resumes collection without unpatching and `F11` flushes a partial window.

Output is deliberately low-frequency:

```text
BepInEx\AudioTimingProbe\status.json
BepInEx\AudioTimingProbe\session-YYYYMMDD-HHMMSS-fff\windows.jsonl
BepInEx\AudioTimingProbe\session-YYYYMMDD-HHMMSS-fff\windows.csv
```

The percentile values are fixed-histogram upper bounds, not exact samples. Window snapshots use independent atomic counter exchanges, so an audio callback landing exactly on a flush boundary can split its count and histogram bucket across adjacent windows. No file I/O, allocation, Unity API, or logging occurs from the audio callback hook.

## Verified v0.230 target shape

The shipped `Assembly-CSharp.dll` inspected for this project has SHA-256:

```text
33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED
```

Relevant method metadata/IL from that DLL:

- `DSPAudio.AudioCycle(bool)`: token `0x0600043F`, RVA `0x0002023C`, 2734 IL bytes, four `Monitor.Enter(object, ref bool)` and four matching `Monitor.Exit(object)` calls. Enter offsets: `IL_0084`, `IL_011B`, `IL_03BA`, `IL_075B`.
- `DSPAudio.OnAudioFilterRead(float[], int)`: token `0x06000441`, RVA `0x00020D90`, 867 IL bytes; its shared-lock region begins at `Monitor.Enter` `IL_003A` and exits at `IL_035C` after the complete resampling/output loop.
- `MasterExecutor.RunFrame()`: token `0x0600054F`, RVA `0x000364D0`, 652 IL bytes.
- `MasterExecutor.Update()`: token `0x06000547`, RVA `0x00035898`, 2014 IL bytes. Its scheduler stores `due` at `IL_0755`, calls `Mathf.Min(due, cap)` at `IL_076E`, and subtracts all due time at `IL_0789`.

The verifier applies both transpilers to decoded instructions without patching the process, then validates synthetic gap thresholds, a five-frame host-update batch, consecutive missed cadence, a `due=7/cap=5/drop=2` decision, JSON parsing, and CSV column counts. A different binary shape throws during arming and the plugin unpatches itself instead of guessing.
