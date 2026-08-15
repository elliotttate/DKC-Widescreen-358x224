# DKC Debug Invincibility

This isolated BepInEx 5 diagnostic plugin gives the active Kong collision
invincibility while deterministic SuperZSNES v0.230 softlock routes are being
replayed. It is disabled by default, never edits the ROM or save state, and
does not touch DKC's camera, object scanner, actor allocator, controller input,
or widescreen code.

## Exact mechanism

The supplied code `BFA2A060` is a raw eight-digit Pro Action Replay code:

| Field | Meaning |
| --- | --- |
| `BFA2A0` | 65C816 read address `$BF:A2A0` |
| `60` | replacement opcode `RTS` |
| stock byte | `$A4` (`LDY $84`) |

In the USA v1.0 disassembly, `$BF:A2A0` is `CODE_BFA2A0`, the common animal
buddy detachment check at the start of both damage paths:

```asm
CODE_BFA17E:                 ; normal/ground collision damage
    JSR CODE_BFA2A0
    BCC continue_damage
    RTS

CODE_BFA3AE:                 ; underwater collision damage
    JSR CODE_BFA2A0
    BCC continue_damage
    RTS
```

The harmful collision comparisons enter these routines with carry set. An
immediate `RTS` preserves carry, so the following `BCC` is not taken and the
damage routine returns. This prevents the shared Kong hurt/death transition and
also prevents collision damage from knocking the Kong off Rambi, Expresso,
Winky, or Enguarde. It applies to Donkey and Diddy because they use the same
handlers.

SuperZSNES's existing `MasterExecutor.cheatCodes` read override provides the
single byte. There is no per-frame WRAM rewrite and no Harmony hook in an
emulated hot path. Before taking ownership, the plugin verifies the complete
16-byte stock signature at ROM offset `$3FA2A0`; the clean USA ROM and this
project's 358x224 ROM have the same signature. A different ROM is rejected.

`BF8E78FF` was also verified as a raw PAR code, but it changes the operand of
`AND #$FFFE` to `AND #$FFFF` at `$BF:8E77`; that preserves the two-Kong takeover
flag after a hit rather than providing invincibility. It is deliberately not
used. `B6A85FAD` changes `DEC $0575` to `LDA $0575` and is an unlimited-lives
patch, not a damage-path guard, so it is also excluded.

## Predictable lifetime and control

- Startup default: **off** (`EnabledAtStartup=false`).
- Save-state load: the setting stays on because the read override is emulator
  state, not serialized SNES state.
- ROM load/change: the plugin releases its owned entry, revalidates the loaded
  ROM signature, and reapplies only if still requested.
- Disable, plugin unload, or emulator exit: only the exact dictionary entry
  owned by this plugin is removed. A pre-existing cheat at `$BFA2A0` is treated
  as a conflict and is never overwritten or removed.

Create one of these request files in
`BepInEx/plugins/DKCDebugInvincibility/`; it is consumed on Unity's main thread:

| Request file | Action |
| --- | --- |
| `enable.request` | Validate DKC and apply `BFA2A0=60`. |
| `disable.request` | Remove the plugin-owned override. |
| `status.request` | Refresh `status.json`. |

The included no-dependency client makes the file API convenient:

```powershell
python .\cli\dkc_invincibility.py enable
python .\cli\dkc_invincibility.py status
python .\cli\dkc_invincibility.py disable
```

Another in-process diagnostic mod can call
`DKCDebugInvincibilityPlugin.SetEnabled(true/false)`; the request is applied on
the next Unity update. `DesiredEnabled` and `Applied` are read-only status
properties.

## Caveats

- "Almost entirely invulnerable" is accurate. Pits, crushing, timers, forced
  level failure, scripted death, and any path that does not use `CODE_BFA17E`
  or `CODE_BFA3AE` can still kill the player.
- Damage collision side effects, including animal-buddy dismount, Kong swap,
  recoil, and the hurt animation, are intentionally skipped. Do not enable it
  for a regression whose correctness depends on one of those effects.
- Enemy collision detection still runs and its event is consumed normally;
  only the subsequent player damage transition is bypassed. Object scanning,
  spawning, and despawning remain available for lifecycle traces.
- Keep it off for normal play, release builds, performance benchmarks, and
  clean-vs-candidate oracle captures unless survival is specifically needed to
  reach the diagnostic point.

## Build and offline verification

```powershell
.\build.ps1 `
  -BepInExRoot '<BepInEx-v5-root>' `
  -GameManagedDir '<SuperZSNES-v0.230>\SUPERZSNES_Data\Managed'
```

The build runs pure offline tests for ROM signature rejection, reversible
ownership, pre-existing-cheat conflicts, and cheat-dictionary replacement. It
does not install the DLL or launch/restart SuperZSNES.
