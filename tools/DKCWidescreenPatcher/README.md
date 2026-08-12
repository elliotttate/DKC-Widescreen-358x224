# DKC Widescreen Patcher

This is the source for the small Windows patcher shipped with GitHub releases.
It validates a user-supplied, headerless Donkey Kong Country USA v1.0 ROM,
applies an embedded standards-compatible BPS patch, verifies the exact output
SHA-256, and writes a new ROM without modifying the original.

The release build embeds three legal delta patches:

- standard 358x224 widescreen with original SPC music;
- optional near-exact 16:9 398x224 widescreen with original SPC music;
- widescreen with the 60-track Deluxe MSU-1 hooks;
- widescreen with the traditional 27-track Restoration MSU-1 hooks.

No ROM or audio data is present in the repository or release. BPS files are
generated locally from verified build outputs by `scripts/build-release.ps1`.

The executable also accepts developer-only commands used by the release build:

```text
DKC-Widescreen-Patcher.exe --create-bps source.sfc target.sfc output.bps "metadata"
DKC-Widescreen-Patcher.exe --apply-bps patch.bps source.sfc output.sfc
DKC-Widescreen-Patcher.exe --verify-embedded source.sfc output-directory
```
