# External music replacement plan

## Conclusion

Use an MSU-1 ROM integration, not a BepInEx MP3 player, for the production implementation.

Donkey Kong Country does not play MIDI files. The 65c816 uploads a Rare/SPC700 sequenced-music blob, then commands the SPC700 to start or stop it. MSU-1 is a virtual SNES expansion interface designed for replacing this kind of sequenced music with sample-accurate external audio. SuperZSNES v0.230 already implements its registers, audio mixing, loop points, volume, pause behavior, and save-state position.

An MP3 supplied by the user should be decoded once during packaging to MSU1-PCM. The shipped game folder should contain PCM rather than MP3. This avoids decoder latency, MP3 encoder delay at the loop boundary, and a second unsynchronized Unity audio clock.

## DKC cue path

The relevant USA v1.0 disassembly flow is:

1. Gameplay selects music ID `$0000-$001A`.
2. `CODE_B99036` or `CODE_B99049` changes the selected song.
3. `$0521` is the requested song and `$0523` is the active/loaded song.
4. `CODE_B990E7` sends SPC command `$FF` through `CODE_8AB1AA` to stop the preceding music.
5. `CODE_8AB1C6` resolves `DATA_8AB159`, uploads the selected song and samples, and waits for the SPC transfer acknowledgement.
6. `CODE_B990CE` sends SPC command `$FE` through `CODE_8AB1AA` to start the new music and sets `$051D`.

This is the correct synchronization boundary. An MSU integration should mirror the song selection and `$FE/$FF` start/stop commands. It should not infer songs from the level number because title screens, bonuses, deaths, victories, bosses, maps, and the music test all change music independently.

## Track mapping

The established DKC MSU convention is `MSU track = DKC music ID + 1`:

| MSU | DKC ID | Song | Playback |
|---:|---:|---|---|
| 1 | `$00` | DK Island Swing | loop |
| 2 | `$01` | Cave Dweller Concert | loop |
| 3 | `$02` | Misty Menace | loop |
| 4 | `$03` | Aquatic Ambience | loop |
| 5 | `$04` | Mine Cart Madness | loop |
| 6 | `$05` | Northern Hemispheres | loop |
| 7 | `$06` | Voices of the Temple | loop |
| 8 | `$07` | Fear Factory | loop |
| 9 | `$08` | Life in the Mines | loop |
| 10 | `$09` | Title Theme | loop |
| 11 | `$0A` | Splash Screen Fanfare | one-shot |
| 12 | `$0B` | Ice Cave Chant | loop |
| 13 | `$0C` | Simian Segue | loop |
| 14 | `$0D` | Forest Frenzy | loop |
| 15 | `$0E` | Credits Concerto | loop |
| 16 | `$0F` | Game Over | one-shot |
| 17 | `$10` | Bonus Room Blitz | loop |
| 18 | `$11` | Death Music | one-shot |
| 19 | `$12` | Victory | one-shot |
| 20 | `$13` | Treetop Rock | loop |
| 21 | `$14` | Funky's Fugue | loop |
| 22 | `$15` | Bad Boss Boogie | loop |
| 23 | `$16` | Candy's Love Song | loop |
| 24 | `$17` | Cranky's Theme | loop |
| 25 | `$18` | Gang-Plank Galleon | loop |
| 26 | `$19` | Failure | one-shot |
| 27 | `$1A` | Beat Level | one-shot |

## SuperZSNES v0.230 contract

Local source audit of `MSU1.cs`, `RomLoader.cs`, `DSPAudio.cs`, and `MasterExecutor.cs` confirms:

- A same-basename `.msu` file enables MSU-1. The data file may be empty when only audio is used.
- Without a manifest, a ROM named `DKC_Widescreen_358x224.sfc` resolves track 1 as `DKC_Widescreen_358x224-1.pcm`.
- Audio files must begin with ASCII `MSU1`, followed by a little-endian 32-bit loop sample, then signed 16-bit little-endian stereo PCM at 44,100 Hz.
- The ROM selects a 16-bit track through `$2004/$2005`, volume through `$2006`, and play/repeat through `$2007`.
- MSU audio is mixed after SPC synthesis, so SPC sound effects remain available.
- Emulator pause stops the audio callback before MSU position advances.
- Save states store the active track and byte offset and restore playback at that position.
- The current implementation loads the complete active PCM file into memory. At 176,400 bytes/second, a five-minute track uses about 50.5 MiB.

The source MP3 is therefore a packaging input, not a runtime asset. Decode it, trim MP3 encoder delay/padding, align its opening to the original cue, set an exact sample-frame loop point, and wrap the raw data in the eight-byte MSU1 header.

## Integration into this ROM

The public DKC MSU-1 v4 patch is useful as behavioral precedent but targets USA Rev. 2. This project builds from USA v1.0 (`MD5 30C5F292FF4CBBFCC00FD8FA96C2DE3B`), so applying that IPS to the widescreen ROM would patch the wrong code.

A clean integration should use this project's Asar overlay and named disassembly routines. Static comparison found the corresponding v1.0 candidates below; every site must be guarded with expected-byte assertions before writing:

- Music-upload hook: v1.0 `$CA:B1CD`, where `STA $4C / ASL / CLC` begins inside `CODE_8AB1C6`.
- NMI/MSU-ready hook: v1.0 `$C0:A971`, beginning `SEP #$20 / LDA $4210`.
- Full-library SPC music mute used by the historical patch: v1.0 `$CA:A9E5`, current bytes `01 D4`, inside the uploaded SPC700 engine image.
- Candidate code/table space: `$FB:F800`; it is still zero-filled in both the clean and current widescreen ROMs, but the final build must assert the entire allocated range is free.
- Historical work RAM: `$0610/$0612`; prefer named definitions and verify them against live writes before adopting them.

The widescreen patch currently does not touch these hook sites or `$FB:F800`, so no direct collision was found.

## One-song versus full-library replacement

Replacing all 27 songs is the straightforward and already-proven design: globally silence SPC music, keep SPC sound effects, and provide every PCM track.

Replacing only one song should not use the historical two-byte global SPC mute; using it with only one PCM would make every other track silent. DKC has a cleaner selective boundary in `CODE_B990CE`:

1. Keep `CODE_B990E7`'s stock `$FF` stop command and also stop MSU playback there.
2. Keep `CODE_B990CE`'s `CODE_8AB1C6` call. This uploads the selected song and its normal sample/SFX environment.
3. If the requested ID is not the configured replacement, execute the stock SPC `$FE` start command unchanged.
4. For the configured replacement ID, select MSU track `ID+1` and check status bit 3.
5. If the PCM is present, set volume/play/loop and omit only the SPC `$FE` command, leaving SPC sound effects operational.
6. If the PCM is missing or MSU-1 is unavailable, execute the stock `$FE` command as a fail-safe fallback.

This is the recommended one-song design. It requires no Unity MP3 player and no SPC700-engine rewrite. A small NMI state machine should delay `$2007` play until MSU status is no longer busy for portability; SuperZSNES currently loads the selected PCM synchronously, but the ROM patch should not depend on that emulator-specific behavior.

The replacement audio must preserve the song's authored time structure. Its opening sample must correspond to the SPC sequence's beginning, its loop sample must correspond to the original musical loop, and one-shot endings must remain non-looping. A single flattened audio file cannot react to hypothetical new cue points inside the song; none were found in DKC's 65c816 command path. The observed game-level behavior is selection, stop, start, and the sequence's own authored loop/end behavior.

## Why a BepInEx MP3 player is the fallback, not the default

A BepInEx player could watch `$0523/$051D`, decode MP3, and play through Unity. It would still need custom handling for pause, reset, ROM changes, save-state seek, rewind, fast-forward, loop points, volume transitions, missing tracks, and synchronization with the emulator audio thread. It would also need a selective way to remove SPC music without removing SPC sound effects. MSU-1 already solves most of those lifecycle problems in the emulated machine and remains portable to other MSU-capable emulators and hardware.

## Acceptance tests

Before shipping an audio pack:

- Assert the exact clean base ROM and every hook's original bytes during build.
- Start every ID through DKC's built-in music test and confirm the expected PCM.
- For a one-song build, confirm the selected ID uses PCM while all other IDs remain byte-for-byte stock SPC playback.
- Remove/rename the selected PCM and confirm that the selected ID falls back to stock SPC music rather than silence.
- Verify looping for all looped tracks across at least two boundaries with no click, gap, or timing drift.
- Verify one-shots stop without repeating.
- Exercise title, level, bonus, death, victory, failure, map, boss, and credits transitions.
- Confirm all SPC sound effects still play while MSU music is active.
- Pause/unpause for at least ten seconds and confirm the track resumes at the same sample.
- Save/load in the middle of a track and compare the restored sample position.
- Test reset, ROM reload, missing PCM, and MSU volume controls.
- Test normal speed and fast-forward explicitly; do not assume the external track follows emulation speed.
- Keep all copyrighted MP3/PCM assets outside the public source repository unless redistribution rights are documented.

## References

- Existing DKC MSU-1 v4 patch and track packs: <https://www.zeldix.net/t1484-donkey-kong-country>
- MSU1-PCM format discussion/specification: <https://forums.nesdev.org/viewtopic.php?t=11004>
- Local DKC disassembly: `DKC1/Misc_Defines_DKC1.asm` and `DKC1/Routine_Macros_DKC1.asm`
- Local SuperZSNES decompilation: `MSU1.cs`, `RomLoader.cs`, `DSPAudio.cs`, and `MasterExecutor.cs`
