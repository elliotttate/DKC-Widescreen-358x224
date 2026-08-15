from __future__ import annotations

import base64
import contextlib
import hashlib
import io
import json
import sys
import tempfile
import unittest
from unittest import mock
from pathlib import Path


TOOL_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TOOL_ROOT))

import route_explorer as explorer  # noqa: E402


def put_u16(memory: bytearray, offset: int, value: int) -> None:
    memory[offset : offset + 2] = int(value & 0xFFFF).to_bytes(2, "little")


def base_document() -> dict:
    return {
        "schema": 1,
        "name": "offline-test",
        "controller": 1,
        "state": {"file": "root.szst0"},
        "search": {"maxDepth": 2, "beamWidth": 4, "maxNodes": 20, "predicateCheckFrames": 2},
        "actions": [
            {"id": "right", "frames": 2, "buttons": "RIGHT"},
            {"id": "neutral", "frames": 2, "buttons": "NONE"},
            {"id": "hurt", "frames": 2, "buttons": "B"},
        ],
        "objective": {
            "terms": [
                {"name": "camera_x", "address": "0x7E1A62", "size": 2, "direction": "maximize", "reference": "baseline"}
            ]
        },
        "goal": {"name": "camera-goal", "address": "0x7E1A62", "size": 2, "op": "ge", "value": 4},
        "death": {"name": "lost-life", "address": "0x7E0575", "size": 2, "op": "lt", "compareTo": "baseline"},
        "forbidden": {"name": "wrong-level", "address": "0x7E0030", "size": 2, "op": "ne", "value": "0x25"},
        "dedup": {
            "selectors": [
                {"name": "camera_bucket", "address": "0x7E1A62", "size": 2, "bucket": 1},
                {"name": "lives", "address": "0x7E0575", "size": 2},
            ]
        },
        "outputTop": 5,
    }


def write_recipe(directory: Path, document: dict | None = None) -> explorer.Recipe:
    path = directory / "recipe.json"
    path.write_text(json.dumps(document or base_document()), encoding="utf-8")
    return explorer.load_recipe(path)


class FakeBridge:
    def __init__(self, drift_after_run: int = 0):
        self.root = bytearray(explorer.WRAM_SIZE)
        put_u16(self.root, 0x0030, 0x25)
        put_u16(self.root, 0x0575, 5)
        self.memory = bytearray(self.root)
        self.root_frame = 1000
        self.frame = self.root_frame
        self.schedule: list[str] = []
        self.cursor = 0
        self.drift_after_run = drift_after_run
        self.load_count = 0
        self.commands: list[str] = []

    def request(self, command: str, arguments: dict) -> dict:
        self.commands.append(command)
        if command == "load_state_file":
            self.load_count += 1
            self.memory[:] = self.root
            self.frame = self.root_frame
            self.schedule = []
            self.cursor = 0
            return {"loaded": True, "paused": True, "schedulesCleared": True}
        if command == "schedule":
            self.schedule = self._parse_macro(arguments["macro"])
            self.cursor = 0
            return {"length": len(self.schedule), "exactOverride": True}
        if command == "run_frames":
            count = int(arguments["count"])
            for _ in range(count):
                buttons = self.schedule[self.cursor]
                self.cursor += 1
                camera = int.from_bytes(self.memory[0x1A62 : 0x1A64], "little")
                if "RIGHT" in buttons:
                    put_u16(self.memory, 0x1A62, camera + 1)
                elif "LEFT" in buttons:
                    put_u16(self.memory, 0x1A62, max(0, camera - 1))
                if buttons == "B":
                    lives = int.from_bytes(self.memory[0x0575 : 0x0577], "little")
                    put_u16(self.memory, 0x0575, lives - 1)
                self.frame += 1
            return {"framesAdvanced": count}
        if command == "snapshot_wram":
            data = bytes(self.memory)
            return {
                "data": base64.b64encode(data).decode("ascii"),
                "sha256": hashlib.sha256(data).hexdigest().upper(),
                "frame": self.frame + (self.drift_after_run if self.cursor else 0),
                "paused": True,
            }
        if command == "clear_schedule":
            self.schedule = []
            return {"status": {}}
        raise AssertionError(f"Unexpected fake command {command}")

    @staticmethod
    def _parse_macro(macro: str) -> list[str]:
        assignments: dict[int, str] = {}
        maximum = -1
        for segment in macro.split(";"):
            range_text, buttons = segment.split("=", 1)
            bounds = range_text.split("-", 1)
            first = int(bounds[0])
            last = int(bounds[1]) if len(bounds) == 2 else first
            for frame in range(first, last + 1):
                assignments[frame] = buttons
            maximum = max(maximum, last)
        return [assignments.get(frame, "NONE") for frame in range(maximum + 1)]


class RecipeTests(unittest.TestCase):
    def test_underwater_generator_is_deterministic_and_exact(self) -> None:
        document = base_document()
        document["actions"] = []
        document["underwaterPulseGenerators"] = [
            {
                "idPrefix": "pulse",
                "directions": ["RIGHT", "UP+RIGHT"],
                "buttons": ["B"],
                "totalFrames": [6],
                "periodFrames": [3],
                "pulseFrames": [1],
            }
        ]
        with tempfile.TemporaryDirectory() as temporary:
            recipe = write_recipe(Path(temporary), document)
        self.assertEqual([action.frames for action in recipe.actions], [6, 6])
        self.assertEqual(
            explorer.macro_from_frames(recipe.actions[0].expand()),
            "0=B+RIGHT;1-2=RIGHT;3=B+RIGHT;4-5=RIGHT",
        )
        self.assertEqual(recipe.actions[1].expand()[0], "B+UP+RIGHT")

    def test_invalid_opposite_directions_are_rejected(self) -> None:
        document = base_document()
        document["actions"][0]["buttons"] = "LEFT+RIGHT"
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "bad.json"
            path.write_text(json.dumps(document), encoding="utf-8")
            with self.assertRaisesRegex(explorer.ExplorerError, "both LEFT and RIGHT"):
                explorer.load_recipe(path)

    def test_macro_frame_ranges_are_zero_based_and_lossless(self) -> None:
        frames = ["RIGHT", "RIGHT", "NONE", "B", "B", "UP"]
        macro = explorer.macro_from_frames(frames)
        self.assertEqual(macro, "0-1=RIGHT;2=NONE;3-4=B;5=UP")
        self.assertEqual(FakeBridge._parse_macro(macro), frames)


class MemoryModelTests(unittest.TestCase):
    def test_predicates_support_baseline_comparison_and_boolean_logic(self) -> None:
        baseline = bytearray(explorer.WRAM_SIZE)
        current = bytearray(baseline)
        put_u16(baseline, 0x0575, 5)
        put_u16(current, 0x0575, 4)
        condition = {
            "all": [
                {"address": "0x7E0575", "size": 2, "op": "lt", "compareTo": "baseline"},
                {"not": {"address": "0x7E0575", "size": 2, "op": "eq", "value": 0}},
            ]
        }
        self.assertTrue(explorer.evaluate_condition(condition, bytes(current), bytes(baseline)))

    def test_compact_state_buckets_and_hashes_selected_wram_only(self) -> None:
        first = bytearray(explorer.WRAM_SIZE)
        second = bytearray(first)
        put_u16(first, 0x1A62, 17)
        put_u16(second, 0x1A62, 23)
        first[0x4000] = 1
        second[0x4000] = 200
        selectors = [{"name": "camera", "address": "0x7E1A62", "size": 2, "bucket": 8}]
        key_a, values_a = explorer.compact_state(bytes(first), selectors)
        key_b, values_b = explorer.compact_state(bytes(second), selectors)
        self.assertEqual(key_a, key_b)
        self.assertEqual(values_a, {"camera": 2})
        self.assertEqual(values_a, values_b)

    def test_weighted_objective_uses_baseline_delta(self) -> None:
        baseline = bytearray(explorer.WRAM_SIZE)
        current = bytearray(baseline)
        put_u16(baseline, 0x1A62, 100)
        put_u16(current, 0x1A62, 124)
        objective = {
            "terms": [
                {"name": "x", "address": "0x7E1A62", "size": 2, "direction": "maximize", "weight": "1.5", "scale": 8}
            ]
        }
        score, values = explorer.objective_score(bytes(current), bytes(baseline), objective)
        self.assertEqual(score, explorer.Decimal("4.5"))
        self.assertEqual(values["x"]["contribution"], "4.5")


class SearchTests(unittest.TestCase):
    def test_search_reloads_root_per_branch_rejects_death_and_outputs_parent_chain(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            recipe = write_recipe(root)
            bridge = FakeBridge()
            engine = explorer.SearchEngine(recipe, bridge, root / "root.szst0")
            report = engine.run()
        self.assertTrue(report["search"]["goalFound"])
        solution = engine.nodes_by_id[report["solutionNodeId"]]
        self.assertEqual(solution.frames, 4)
        self.assertEqual(solution.macro, "0-3=RIGHT")
        route = explorer.output_recipe(solution, engine, "A" * 64)
        self.assertEqual([item["actionId"] for item in route["parentChain"]], ["right", "right"])
        self.assertEqual(route["exactFrames"], 4)
        self.assertEqual(route["finalEmulatorFrame"] - route["rootEmulatorFrame"], 4)
        self.assertGreater(bridge.load_count, 2)
        self.assertIn("rejected", {node.status for node in engine.nodes if node.action_id == "hurt"})
        self.assertNotIn("write_wram", bridge.commands)
        self.assertNotIn("load_rom", bridge.commands)
        self.assertNotIn("resume", bridge.commands)

    def test_exact_emulator_frame_mismatch_aborts(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            recipe = write_recipe(root)
            bridge = FakeBridge(drift_after_run=1)
            engine = explorer.SearchEngine(recipe, bridge, root / "root.szst0")
            with self.assertRaisesRegex(explorer.ExplorerError, "Exact frame mismatch"):
                engine.run()

    def test_decode_snapshot_rejects_bad_digest(self) -> None:
        memory = bytes(explorer.WRAM_SIZE)
        with self.assertRaisesRegex(explorer.ExplorerError, "does not match"):
            explorer.decode_snapshot(
                {"data": base64.b64encode(memory).decode("ascii"), "sha256": "0" * 64, "frame": 1, "paused": True}
            )


class SafetyTests(unittest.TestCase):
    def test_preflight_rejects_running_emulator_or_existing_schedule(self) -> None:
        base = {
            "attached": True,
            "loaded": True,
            "paused": False,
            "frameHook": True,
            "inputHook": True,
            "active": None,
            "schedules": [],
        }
        with self.assertRaisesRegex(explorer.ExplorerError, "paused"):
            explorer.verify_preflight(base)
        base["paused"] = True
        base["schedules"] = [{"controller": 1, "enabled": True}]
        with self.assertRaisesRegex(explorer.ExplorerError, "schedules"):
            explorer.verify_preflight(base)

    def test_invincibility_check_is_read_only_and_explicit(self) -> None:
        status = {
            "version": "0.1.0",
            "override": "BFA2A0=60",
            "applied": True,
            "desiredEnabled": True,
            "romValidated": True,
            "romPath": "D:\\ROMs\\DKC.sfc",
        }
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "status.json"
            path.write_text(json.dumps(status), encoding="utf-8")
            before = path.read_bytes()
            observed = explorer.verify_invincibility_status(path, True)
            after = path.read_bytes()
        self.assertTrue(observed["applied"])
        self.assertEqual(before, after)
        explorer.verify_invincibility_rom(observed, {"rom": "d:\\roms\\DKC.sfc"})

    def test_validate_command_cannot_open_a_socket(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "recipe.json"
            path.write_text(json.dumps(base_document()), encoding="utf-8")
            output = io.StringIO()
            with mock.patch.object(explorer.socket, "create_connection", side_effect=AssertionError("socket opened")):
                with contextlib.redirect_stdout(output):
                    result = explorer.main(["validate", "--recipe", str(path)])
        self.assertEqual(result, 0)
        self.assertFalse(json.loads(output.getvalue())["liveBridgeContacted"])


if __name__ == "__main__":
    unittest.main()
