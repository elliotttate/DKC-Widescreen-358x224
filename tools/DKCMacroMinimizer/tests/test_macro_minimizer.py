from __future__ import annotations

import base64
import contextlib
import hashlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


TOOL_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TOOL_ROOT))

import macro_minimizer as minimizer  # noqa: E402


MARKER = 0x0100


def recipe_document(mode: str = "pulse") -> dict:
    if mode == "pulse":
        macro = "0-1=RIGHT+B;2-3=RIGHT;4-5=RIGHT+B"
        transitions: object = {"mode": "buttons", "buttons": ["B"]}
    else:
        macro = "0-5=RIGHT"
        transitions = False
    return {
        "schema": 1,
        "name": f"offline-{mode}",
        "controller": 1,
        "state": {"file": "root.szst0"},
        "rom": {"file": "test.sfc"},
        "macro": macro,
        "outcome": {
            "label": "failure",
            "predicate": {"name": "marker", "address": "0x7E0100", "size": 1, "op": "eq", "value": 1},
            "settleFrames": 0,
            "requirePredicateFalseAtRoot": True,
        },
        "preserveTransitions": transitions,
        "confirmationReplays": 2,
        "maxEvaluations": 100,
    }


def write_recipe(directory: Path, mode: str = "pulse") -> minimizer.Recipe:
    path = directory / "recipe.json"
    path.write_text(json.dumps(recipe_document(mode)), encoding="utf-8")
    return minimizer.load_recipe(path)


class FakeBridge:
    def __init__(self, behavior: str = "pulse", frame_drift: int = 0, nondeterministic: bool = False):
        self.behavior = behavior
        self.frame_drift = frame_drift
        self.nondeterministic = nondeterministic
        self.root = bytearray(minimizer.WRAM_SIZE)
        self.memory = bytearray(self.root)
        self.root_frame = 700
        self.frame = self.root_frame
        self.schedule: list[int] = []
        self.cursor = 0
        self.projected_b: list[bool] = []
        self.right_count = 0
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
            self.projected_b = []
            self.right_count = 0
            return {"loaded": True, "paused": True, "schedulesCleared": True}
        if command == "schedule":
            self.schedule = self._parse_macro(arguments["macro"])
            self.cursor = 0
            return {"length": len(self.schedule), "exactOverride": True}
        if command == "run_frames":
            count = int(arguments["count"])
            for _ in range(count):
                mask = self.schedule[self.cursor] if self.cursor < len(self.schedule) else 0
                self.cursor += 1
                b = bool(mask & minimizer.BUTTON_BITS["B"])
                if not self.projected_b or self.projected_b[-1] != b:
                    self.projected_b.append(b)
                if mask & minimizer.BUTTON_BITS["RIGHT"]:
                    self.right_count += 1
                if self.behavior == "pulse" and self.projected_b[:3] == [True, False, True]:
                    self.memory[MARKER] = 1
                if self.behavior == "right" and self.right_count >= 2:
                    self.memory[MARKER] = 1
                if self.behavior == "autonomous":
                    self.memory[MARKER] = 1
                self.frame += 1
            if self.nondeterministic:
                self.memory[0x0200] = self.load_count & 1
            return {"framesAdvanced": count}
        if command == "snapshot_wram":
            data = bytes(self.memory)
            return {
                "data": base64.b64encode(data).decode("ascii"),
                "sha256": hashlib.sha256(data).hexdigest().upper(),
                "frame": self.frame + (self.frame_drift if self.cursor else 0),
                "paused": True,
            }
        if command == "clear_schedule":
            self.schedule = []
            return {"status": {}}
        raise AssertionError(f"Unexpected command {command}")

    @staticmethod
    def _parse_macro(macro: str) -> list[int]:
        frames = minimizer.parse_macro(macro)
        return [frame.mask for frame in frames]


class MacroTests(unittest.TestCase):
    def test_parser_fills_gaps_and_canonicalizes_overrides(self) -> None:
        frames = minimizer.parse_macro("0-3=RIGHT;1=B;5=UP")
        self.assertEqual([frame.buttons for frame in frames], ["RIGHT", "B", "RIGHT", "RIGHT", "NONE", "UP"])
        self.assertEqual(minimizer.macro_from_frames(frames), "0=RIGHT;1=B;2-3=RIGHT;4=NONE;5=UP")

    def test_parser_rejects_opposite_directions(self) -> None:
        with self.assertRaisesRegex(minimizer.MinimizerError, "both LEFT and RIGHT"):
            minimizer.parse_macro("0=LEFT+RIGHT")

    def test_button_transition_policy_preserves_pulse_edges(self) -> None:
        original = minimizer.parse_macro("0-2=RIGHT+B;3-5=RIGHT;6-8=RIGHT+B")
        policy = minimizer.transition_policy({"mode": "buttons", "buttons": ["B"]}, original)
        retained = [original[0], original[3], original[6]]
        self.assertTrue(policy.allows(retained))
        self.assertFalse(policy.allows([original[0], original[6]]))
        self.assertEqual(policy.expected_signature, (minimizer.BUTTON_BITS["B"], 0, minimizer.BUTTON_BITS["B"]))


class PredicateTests(unittest.TestCase):
    def test_baseline_relative_predicate_and_evidence(self) -> None:
        baseline = bytearray(minimizer.WRAM_SIZE)
        current = bytearray(baseline)
        baseline[0x300] = 5
        current[0x300] = 4
        predicate = {"name": "lost-life", "address": "0x7E0300", "size": 1, "op": "lt", "compareTo": "baseline"}
        minimizer.validate_predicate(predicate, "test")
        self.assertTrue(minimizer.evaluate_predicate(predicate, bytes(current), bytes(baseline)))
        self.assertEqual(minimizer.predicate_evidence(predicate, bytes(current), bytes(baseline))[0]["expected"], 5)


class MinimizationTests(unittest.TestCase):
    def test_hierarchical_minimization_retains_exact_b_pulse_transitions(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            recipe = write_recipe(Path(temporary), "pulse")
            bridge = FakeBridge("pulse")
            evaluator = minimizer.ReplayEvaluator(recipe, bridge, Path(temporary) / "root.szst0")
            result = minimizer.HierarchicalMinimizer(recipe, evaluator).run()
        self.assertEqual(len(result.frames), 3)
        self.assertEqual(minimizer.macro_from_frames(result.frames), "0=B+RIGHT;1=RIGHT;2=B+RIGHT")
        self.assertEqual(result.minimality, "1-minimal-under-transition-policy")
        self.assertTrue(result.final_evaluation.reproduced)
        self.assertGreater(bridge.load_count, 2)
        self.assertNotIn("load_rom", bridge.commands)
        self.assertNotIn("write_wram", bridge.commands)
        self.assertNotIn("resume", bridge.commands)

    def test_free_policy_reduces_to_two_required_frames(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            recipe = write_recipe(Path(temporary), "right")
            bridge = FakeBridge("right")
            evaluator = minimizer.ReplayEvaluator(recipe, bridge, Path(temporary) / "root.szst0")
            result = minimizer.HierarchicalMinimizer(recipe, evaluator).run()
        self.assertEqual(len(result.frames), 2)
        self.assertEqual(minimizer.macro_from_frames(result.frames), "0-1=RIGHT")

    def test_neutral_transition_policy_can_emit_zero_input_frames(self) -> None:
        document = recipe_document("right")
        document["macro"] = "0-3=NONE"
        document["preserveTransitions"] = "all"
        document["outcome"]["settleFrames"] = 1
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "recipe.json"
            path.write_text(json.dumps(document), encoding="utf-8")
            recipe = minimizer.load_recipe(path)
            bridge = FakeBridge("autonomous")
            evaluator = minimizer.ReplayEvaluator(recipe, bridge, root / "root.szst0")
            result = minimizer.HierarchicalMinimizer(recipe, evaluator).run()
        self.assertEqual(result.frames, [])
        self.assertEqual(minimizer.macro_from_frames(result.frames), "")
        script = minimizer.bridge_script(recipe, Path("root.szst0"), result.frames)
        self.assertEqual(script["steps"][1]["args"]["macro"], "0=NONE")
        self.assertEqual([step["args"].get("count") for step in script["steps"] if step["command"] == "run_frames"], [1])

    def test_exact_frame_drift_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            recipe = write_recipe(Path(temporary), "right")
            bridge = FakeBridge("right", frame_drift=1)
            evaluator = minimizer.ReplayEvaluator(recipe, bridge, Path(temporary) / "root.szst0")
            with self.assertRaisesRegex(minimizer.MinimizerError, "Exact frame mismatch"):
                evaluator.evaluate(recipe.original, {"phase": "test"})

    def test_nondeterministic_confirmation_digest_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            recipe = write_recipe(Path(temporary), "right")
            bridge = FakeBridge("right", nondeterministic=True)
            evaluator = minimizer.ReplayEvaluator(recipe, bridge, Path(temporary) / "root.szst0")
            with self.assertRaisesRegex(minimizer.MinimizerError, "nondeterministic"):
                evaluator.evaluate(recipe.original, {"phase": "test"})


class SafetyTests(unittest.TestCase):
    def test_preflight_rejects_running_or_scheduled_target(self) -> None:
        status = {
            "attached": True,
            "loaded": True,
            "paused": False,
            "frameHook": True,
            "inputHook": True,
            "active": None,
            "schedules": [],
            "rom": "D:\\ROMs\\DKC.sfc",
        }
        with self.assertRaisesRegex(minimizer.MinimizerError, "paused"):
            minimizer.verify_preflight(status)
        status["paused"] = True
        status["schedules"] = [{"enabled": True}]
        with self.assertRaisesRegex(minimizer.MinimizerError, "schedules"):
            minimizer.verify_preflight(status)

    def test_validate_command_cannot_open_a_socket(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "recipe.json"
            path.write_text(json.dumps(recipe_document()), encoding="utf-8")
            output = io.StringIO()
            with mock.patch.object(minimizer.socket, "create_connection", side_effect=AssertionError("socket opened")):
                with contextlib.redirect_stdout(output):
                    code = minimizer.main(["validate", "--recipe", str(path)])
        self.assertEqual(code, 0)
        self.assertFalse(json.loads(output.getvalue())["liveBridgeContacted"])


if __name__ == "__main__":
    unittest.main()
