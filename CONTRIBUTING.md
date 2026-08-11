# Contributing

- Never commit ROMs, extracted game assets, saves/states, emulator binaries, Unity assemblies, installed plugin DLLs, captures, logs, or credentials.
- Keep runtime patches fail-closed against unknown `Assembly-CSharp.dll` versions.
- Default new experimental optimizations to disabled.
- Run `scripts/validate-source.ps1` before committing.
- For visual changes, include a deterministic `DKCLevelAutomation` recipe or oracle comparison and document both accepted and rejected results.
- Preserve narrow-room/stock behavior in ROM patches unless the change explicitly documents otherwise.
