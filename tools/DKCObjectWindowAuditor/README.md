# DKC object-window auditor

This offline tool correlates a SuperZSNES `wram-7e7f.bin` capture with the
readable DKC1 disassembly. It reports:

- the entrance's exact `DATA_BD8000` sprite-data list;
- the type-$09 section range selected in `$1E03-$1E0D`;
- each record's `$192B` active-slot bookkeeping;
- stock and widened horizontal activation eligibility;
- normal-sprite-pool occupancy; and
- records which are eligible in wide mode but have not obtained a sprite.

This is intended for intermittent softlocks where a screenshot cannot tell
whether an object was never scanned, was scanned but failed allocation, or was
created and later removed.

## Capture and run

Use `DKCWidescreenDebugger` to capture the failing frame. Then run:

```powershell
python .\tools\DKCObjectWindowAuditor\audit_object_windows.py `
  --disassembly '<DKC1 disassembly>\DKC1\Routine_Macros_DKC1.asm' `
  --wram '<capture>\wram-7e7f.bin'
```

Add `--json --output report.json` for a machine-readable regression artifact.
The default margin is `$38` (358x224). Pass `--margin 0x48` for the 398x224
profile.

## Interpretation

`missingWideEligibleRecords` is a high-value lead, not automatically a bug.
Some record types are one-shot triggers, and zero bookkeeping can be valid
after they run. Compare the same checkpoint from a successful attempt. For a
type-$05 group, also inspect the child bookkeeping: `$FF` on the parent only
means that its controller exists; it does not prove that every child obtained
a sprite slot.

The tool treats the normal allocation pool exactly as `CODE_BDF3A2` does:
even raw indices `$02..$1C`. Slots outside that range include players and
special-purpose objects and are still listed under `actors` but are not
counted as free normal allocation entries.
