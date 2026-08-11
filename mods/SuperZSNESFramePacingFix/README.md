# SuperZSNES Frame Pacing Fix

This isolated BepInEx plugin fixes confirmed lost emulated time in SuperZSNES v0.230 and can raise Unity's normal-speed presentation cadence so fewer emulated frames are hidden inside two-frame batches. It is disabled by default, is not installed automatically, and never rewrites `SUPERZSNES_Data/Managed/Assembly-CSharp.dll` on disk.

## Confirmed stock bug

`MasterExecutor.Update()` uses a 60 Hz NTSC or 50 Hz PAL accumulator:

```csharp
_accumulatedDT += Time.deltaTime;
var dueFrames = (int)(_accumulatedDT / (1f / targetHz));
var scheduledFrames = Mathf.Min(dueFrames, cap);
for (var i = 0; i < scheduledFrames; i++) RunFrame();
_accumulatedDT -= dueFrames * (1f / targetHz); // bug: charges unscheduled frames
if (dueFrames > 0) snesRenderer.GenerateBackgrounds();
```

Normal speed sets `cap = 5`. A 1.045-second NTSC stall makes roughly 62 frames due: only five execute, but stock removes all 62 from the accumulator. The missing 57 frames can never be recovered.

The installed `Assembly-CSharp.dll` confirms this sequence in `MasterExecutor.Update`, RVA `0x35898`:

- `IL_0754..IL_0755`: calculate/store `dueFrames` in local 9.
- `IL_0768..IL_0773`: loop against `Mathf.Min(local9, local7)`.
- `IL_0775..IL_0789`: subtract `local9 / targetHz` from `_accumulatedDT`.
- `IL_078e..IL_0799`: call `GenerateBackgrounds()` once when `dueFrames > 0`.

## Fixed semantics

At normal speed only, the patch charges the number of frames the existing loop can actually schedule:

```csharp
var scheduledFrames = Math.Min(Math.Max(dueFrames, 0), 5);
_accumulatedDT = Math.Max(0, _accumulatedDT - scheduledFrames * period);
```

The full positive remainder is carried into later Unity updates, whose existing five-frame cap drains it safely. The fractional remainder is retained. `GenerateBackgrounds()` remains once per Unity update with due work, just as in stock.

`EmergencyMaxBacklogFrames` is a last-resort ceiling on the backlog left after the current update's scheduled batch is charged. Its default is 120 frames (two NTSC seconds), so it does not affect the observed 1.045-second stall. Set it to `0` for unbounded retention. The accumulator returned by the normal-speed path is never negative.

Fast-forward (`cap = 1..4`) takes the stock arithmetic path exactly:

```csharp
return accumulated - dueFrames * (1f / targetHz);
```

No fast-forward clamping or backlog retention is added.

## Presentation-cadence constraint and improvement

`GenerateBackgrounds()` runs once after the emulation loop, not once per `RunFrame()` call. If two SNES frames execute in one Unity `Update`, only the second state reaches that Unity presentation. No accumulator formula can display 300 distinct emulated frames through only 262-277 Unity presentations in five seconds.

Version 0.3 therefore has an optional `PresentationCadence` controller. During normal play it sets:

```csharp
QualitySettings.vSyncCount = 0;
Application.targetFrameRate = 120;
```

The 120 Hz value is a ceiling that gives the Unity loop headroom above the emulator's hard-coded 60 Hz NTSC or 50 Hz PAL rate. It does not make emulation run at 120 Hz. With the measured 14-15 ms `MasterExecutor.Update`, the likely result is an unthrottled 65-70-ish Unity updates per second: normally zero or one emulated frame per presentation instead of recurrent one/two batches. True hitches can still require catch-up batches, and if the whole Unity frame remains slower than 16.67 ms, a scheduling-only plugin cannot guarantee that every emulated frame is presented.

Unity ignores `Application.targetFrameRate` on desktop while VSync is enabled, which is why both values must be changed. Software frame pacing can introduce tearing or microstutter, so this mode remains behind the plugin's disabled-by-default master switch and requires an A/B test.

The controller captures the original VSync and target-frame-rate values. For `cap=1..4` fast-forward it restores those exact values before the next Unity update; when normal `cap=5` play resumes it reapplies the cadence lift. The fast-forward accumulator calculation remains bit-identical to stock. Original values are also restored when the plugin unloads.

## No half-frame hysteresis

There is intentionally no half-frame bias. A half-frame threshold can turn small timing jitter around an otherwise exact 60 Hz callback into a steadier phase, but it cannot repair an average callback rate below 60 Hz: at 52-55 callbacks per second, the same number of two-frame batches must eventually occur. It also executes frames up to 8.33 ms early and requires a negative/borrowed accumulator. This fix retains the existing floor calculation and solves the actual presentation-slot shortage by raising the Unity cadence.

## VSync interaction

The reconstructed Unity project selects quality level `Ultra`, whose `vSyncCount` is 1. No game script assigns `Application.targetFrameRate`; the desktop Unity update cadence normally follows display refresh. The emulator does not use the hardware-accurate approximately 60.099 Hz SNES rate here: `MasterExecutor.Update` explicitly chooses `60f` for NTSC and `50f` for PAL. An integer Unity target therefore matches the program's existing timebase, not exact console hardware. Display rates such as 59.94, 60, 120, or 144 Hz cannot guarantee a universal one-to-one mapping. On a hitch, no normal-speed Unity update executes more than five SNES frames.

## Build and offline verification

```powershell
dotnet build .\SuperZSNESFramePacingFix.csproj -c Release
& .\verify.ps1
```

The verifier reads the real installed `MasterExecutor.Update` IL, runs the compiled transpiler against it without patching or starting the emulator, checks the replacement instruction shape, bit-compares the compiled fast-forward helper against stock arithmetic, simulates a 60-frame normal-speed backlog draining in twelve five-frame updates, verifies the cap-based normal/fast-forward cadence policy, and inspects the compiled cadence controller for both Unity setting setters and restoration setters.

The output DLL is under `bin/Release/net472`. Do not copy it into BepInEx until an A/B test is coordinated.

Generated configuration defaults:

```ini
[FramePacing]
Enabled = false
EmergencyMaxBacklogFrames = 120

[PresentationCadence]
Enabled = true
TargetFrameRate = 120
RestoreDuringFastForward = true
```

`PresentationCadence.Enabled=true` has no effect while the master `FramePacing.Enabled=false` default remains unchanged.

Changing `Enabled` requires an emulator restart because the Harmony transpiler is applied only during plugin startup.

## A/B test plan

1. Use identical ROM/save-state/controller input and disable unrelated experimental performance patches.
2. Record Unity update duration, `RunFrame` count per Unity update, `_accumulatedDT`, SNES frame number, audio underruns, and `GenerateBackgrounds` count.
3. On the current display, compare stock VSync against the cadence lift for at least ten minutes. Record Unity updates per five seconds and counts of 0/1/2+ `RunFrame` batches. Patched normal play should exceed 300 Unity updates per five seconds or materially reduce two-frame batches without reducing the 300 emulated-frame count.
4. Inject controlled stalls of 100, 200, 500, and 1,045 ms. Each normal-speed Unity update must execute at most five SNES frames, while subsequent updates drain all retained time.
5. Exercise PAL, pause/single-step, rewind, loading, focus loss, and fast-forward at digital and analog strengths. Confirm VSync/target-rate restoration during steady fast-forward and reapplication afterward. Fast-forward accumulator results must remain bit-identical to stock.
6. Reject the cadence lift if Unity updates remain below 60 Hz under sustained load, or if it causes tearing, software-pacer microstutter, audio instability, altered fast-forward speed, excess `GenerateBackgrounds` calls, or long-run drift.
