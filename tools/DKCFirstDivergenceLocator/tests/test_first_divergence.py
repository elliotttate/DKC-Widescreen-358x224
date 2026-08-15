from __future__ import annotations

import contextlib
import base64
import hashlib
import importlib.util
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path


TOOL_ROOT = Path(__file__).resolve().parent.parent
SPEC = importlib.util.spec_from_file_location("first_divergence", TOOL_ROOT / "first_divergence.py")
assert SPEC and SPEC.loader
fd = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = fd
SPEC.loader.exec_module(fd)


def snapshot(changes: dict[int, int] | None = None) -> bytes:
    memory = bytearray(fd.WRAM_SIZE)
    for offset, value in (changes or {}).items():
        memory[offset] = value
    return bytes(memory)


class PredicateTests(unittest.TestCase):
    def test_expected_camera_difference_is_raw_but_not_selected(self) -> None:
        mask, _included, ignored = fd.build_selection(
            {
                "includeGroups": ["core_gameplay", "actor_pool", "object_bookkeeping", "camera_and_bounds"],
                "ignoreProfiles": ["expected_widescreen_camera_bounds"],
            }
        )
        baseline = [snapshot() for _ in range(5)]
        candidate = [snapshot(), snapshot({0x1A62: 1}), snapshot(), snapshot({0x0D47: 7}), snapshot()]
        result = fd.analyze_snapshot_sequences(baseline, candidate, mask)
        self.assertEqual(1, result["firstRawFrame"])
        self.assertEqual(3, result["firstSelectedFrame"])
        detail = fd.describe_divergence(baseline[3], candidate[3], mask, ignored_ranges=ignored)
        self.assertEqual(1, detail["selectedMemory"]["changedByteCount"])
        self.assertEqual(0, detail["ignoredMemory"]["changedByteCount"])
        ignored_detail = fd.describe_divergence(baseline[1], candidate[1], mask, ignored_ranges=ignored)
        self.assertEqual(0, ignored_detail["selectedMemory"]["changedByteCount"])
        self.assertEqual(1, ignored_detail["ignoredMemory"]["changedByteCount"])

    def test_transient_selected_divergence_is_not_lost_after_reconvergence(self) -> None:
        mask, _included, _ignored = fd.build_selection({"includeGroups": ["object_bookkeeping"]})
        baseline = [snapshot() for _ in range(4)]
        candidate = [snapshot(), snapshot({0x192B: 2}), snapshot(), snapshot()]
        self.assertEqual(1, fd.analyze_snapshot_sequences(baseline, candidate, mask)["firstSelectedFrame"])

    def test_explicit_range_and_ignore(self) -> None:
        mask, _included, _ignored = fd.build_selection(
            {
                "include": [{"start": "$7E1000", "length": "0x10"}],
                "ignore": [{"start": "0x7E1004", "end": "0x7E1007"}],
            }
        )
        self.assertEqual(12, sum(mask))
        self.assertEqual(1, mask[0x1000])
        self.assertEqual(0, mask[0x1005])


class DifferenceTests(unittest.TestCase):
    def test_ranges_are_coalesced_and_contain_hex_samples(self) -> None:
        left = snapshot()
        right = snapshot({0x100: 1, 0x101: 2, 0x103: 3})
        result = fd.changed_ranges(left, right)
        self.assertEqual(3, result["changedByteCount"])
        self.assertEqual(2, result["changedRangeCount"])
        self.assertEqual("0x7E0100", result["ranges"][0]["start"])
        self.assertEqual("0102", result["ranges"][0]["candidateHex"])

    def test_named_actor_and_bookkeeping_differences(self) -> None:
        left = snapshot()
        changed = bytearray(left)
        changed[0x0030:0x0032] = (0x25).to_bytes(2, "little")
        changed[0x0D47:0x0D49] = (0x57).to_bytes(2, "little")  # actor slot 1
        changed[0x15FF:0x1601] = (0x12).to_bytes(2, "little", signed=True)
        changed[0x192B + 0x12] = 0x02
        mask, _included, _ignored = fd.build_selection({"includeGroups": ["full_wram"]})
        report = fd.describe_divergence(left, bytes(changed), mask, {0x57: "Croctopus"})
        self.assertEqual("level_id", report["namedDkcFields"][0]["field"])
        actor = next(item for item in report["actorSlots"] if item["slot"] == 1)
        self.assertEqual("spawn", actor["kind"])
        self.assertEqual("Croctopus", actor["candidateName"])
        self.assertEqual(0x12, report["objectBookkeeping"][0]["recordIndex"])
        self.assertEqual(0x02, report["objectBookkeeping"][0]["candidateActorIndex"])


class TraceTests(unittest.TestCase):
    def test_trace_slice_uses_offsets_and_nearby_frames(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            session = Path(temporary)
            events = session / "events.jsonl"
            events.write_text(json.dumps({"frame": 10, "type": "old"}) + "\n", encoding="utf-8")
            (session / "writes.jsonl").write_text("", encoding="utf-8")
            (session / "scanner.jsonl").write_text("", encoding="utf-8")
            reader = fd.TraceSliceReader(session)
            mark = reader.mark()
            with events.open("a", encoding="utf-8") as handle:
                handle.write(json.dumps({"frame": 20, "type": "actor_allocated", "pc": "$BDF915"}) + "\n")
                handle.write(json.dumps({"frame": 30, "type": "far"}) + "\n")
            rows = reader.rows_since(mark)
            nearby = reader.nearby(rows, 21, 1)
            self.assertEqual(1, len(nearby))
            self.assertEqual("actor_allocated", nearby[0]["type"])
            self.assertEqual("events.jsonl", nearby[0]["traceStream"])


class ReplayOrderTests(unittest.TestCase):
    class FakeBridge:
        def __init__(self, baseline_rom: Path, candidate_rom: Path) -> None:
            self.baseline_rom = str(baseline_rom)
            self.candidate_rom = str(candidate_rom)
            self.variant = ""
            self.frame = 0
            self.commands: list[tuple[str, dict]] = []

        def request(self, command: str, arguments: dict | None = None):
            arguments = arguments or {}
            self.commands.append((command, dict(arguments)))
            if command == "load_rom":
                self.variant = "baseline" if arguments["path"] == self.baseline_rom else "candidate"
                self.frame = 0
                return {"loaded": True}
            if command == "load_state_file":
                self.frame = 0
                return {"loaded": True}
            if command in {"pause", "schedule", "clear_schedule"}:
                return {}
            if command == "step_frames":
                self.frame += int(arguments["count"])
                return {"framesAdvanced": int(arguments["count"])}
            if command == "snapshot_wram":
                memory = bytearray(fd.WRAM_SIZE)
                memory[0x0030:0x0032] = (0x25).to_bytes(2, "little")
                if self.variant == "candidate" and self.frame >= 1:
                    memory[0x1A62] = 1  # ignored camera drift
                if self.variant == "candidate" and self.frame == 2:
                    memory[0x192B] = 2  # first selected divergence
                payload = bytes(memory)
                return {
                    "address": "0x7E0000",
                    "size": fd.WRAM_SIZE,
                    "encoding": "base64",
                    "data": base64.b64encode(payload).decode("ascii"),
                    "sha256": hashlib.sha256(payload).hexdigest().upper(),
                    "frame": 100 + self.frame,
                    "paused": True,
                }
            raise AssertionError(f"unexpected command {command}")

    def test_variants_and_confirmations_are_fresh_sequential_replays(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            baseline_rom = root / "stock.sfc"
            candidate_rom = root / "wide.sfc"
            state_path = root / "state.szst0"
            baseline_rom.write_bytes(b"stock")
            candidate_rom.write_bytes(b"wide")
            state_path.write_bytes(b"state")
            recipe = {
                "schemaVersion": 1,
                "name": "synthetic",
                "checkpointStride": 2,
                "predicate": {
                    "includeGroups": ["object_bookkeeping", "camera_and_bounds"],
                    "ignoreProfiles": ["expected_widescreen_camera_bounds"],
                },
                "states": [
                    {
                        "id": "s",
                        "file": state_path.name,
                        "expectedLevel": "0x25",
                        "scenarios": [{"id": "move", "maxFrame": 3, "inputs": {"1": "0-2=RIGHT"}}],
                    }
                ],
            }
            fd.validate_recipe(recipe)
            bridge = self.FakeBridge(baseline_rom, candidate_rom)
            runner = fd.FirstDivergenceRunner(recipe, bridge, root / "out", fd.TraceSliceReader(None))
            report = runner.run(baseline_rom, candidate_rom, {"s": state_path}, set())
            case = report["cases"][0]
            self.assertEqual(1, case["firstRawFrame"])
            self.assertEqual(2, case["firstUnexpectedFrame"])
            self.assertEqual({"start": 2, "end": 3}, case["search"]["firstDivergentWindow"])
            self.assertEqual(2, case["search"]["refinementReplay"]["locatedFrame"])
            self.assertTrue(case["determinism"]["confirmed"])
            loaded_roms = [args["path"] for command, args in bridge.commands if command == "load_rom"]
            self.assertEqual(
                [
                    str(baseline_rom),
                    str(candidate_rom),
                    str(candidate_rom),
                    str(baseline_rom),
                    str(candidate_rom),
                ],
                loaded_roms,
            )
            self.assertEqual(5, sum(command == "load_state_file" for command, _args in bridge.commands))
            self.assertEqual(5, sum(command == "schedule" for command, _args in bridge.commands))


class RecipeTests(unittest.TestCase):
    def test_bundled_recipe_validates_and_has_all_four_states(self) -> None:
        path = TOOL_ROOT / "recipes" / "four-user-states.sample.json"
        recipe = fd.validate_recipe(json.loads(path.read_text(encoding="utf-8")), str(path))
        self.assertEqual({"szst0", "szst1", "szst2", "szst3"}, {state["id"] for state in recipe["states"]})
        self.assertEqual(11, len(fd.case_keys(recipe)))

    def test_validate_only_does_not_construct_bridge_client(self) -> None:
        original = fd.BridgeClient

        class ForbiddenBridge:
            def __init__(self, *_args, **_kwargs):
                raise AssertionError("validate-only attempted bridge access")

        fd.BridgeClient = ForbiddenBridge
        output = io.StringIO()
        try:
            with contextlib.redirect_stdout(output):
                code = fd.main(["--validate-only", "--recipe", "four-user-states.sample"])
        finally:
            fd.BridgeClient = original
        self.assertEqual(0, code)
        document = json.loads(output.getvalue())
        self.assertFalse(document["automationContacted"])
        self.assertEqual(11, len(document["cases"]))

    def test_invalid_empty_selection_is_rejected(self) -> None:
        recipe = {
            "schemaVersion": 1,
            "name": "empty-selection",
            "predicate": {
                "include": [{"start": "0x7E1000", "length": 1}],
                "ignore": [{"start": "0x7E1000", "length": 1}],
            },
            "states": [
                {
                    "id": "s",
                    "file": "s.szst0",
                    "scenarios": [{"id": "x", "maxFrame": 1, "inputs": {"1": "0=NONE"}}],
                }
            ],
        }
        with self.assertRaises(fd.LocatorError):
            fd.validate_recipe(recipe)

    def test_unknown_recipe_key_is_rejected(self) -> None:
        path = TOOL_ROOT / "recipes" / "four-user-states.sample.json"
        recipe = json.loads(path.read_text(encoding="utf-8"))
        recipe["checkpontStride"] = 10
        with self.assertRaisesRegex(fd.LocatorError, "unknown keys"):
            fd.validate_recipe(recipe)


if __name__ == "__main__":
    unittest.main()
