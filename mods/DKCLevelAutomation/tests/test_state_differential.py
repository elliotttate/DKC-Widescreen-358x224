from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "cli"))

import run_state_differential as differential  # noqa: E402


def put_u16(memory: bytearray, offset: int, value: int) -> None:
    memory[offset] = value & 0xFF
    memory[offset + 1] = value >> 8 & 0xFF


class StateDifferentialTests(unittest.TestCase):
    def test_checked_in_four_state_plan_validates(self) -> None:
        path = ROOT / "recipes" / "four-user-states-differential.json"
        recipe = differential.validate_recipe(json.loads(path.read_text(encoding="utf-8")), path)
        self.assertEqual(len(recipe["states"]), 4)
        self.assertEqual(sum(len(state["branches"]) for state in recipe["states"]), 11)
        self.assertEqual(recipe["states"][0]["expectedLevel"], "0x0025")

    def test_macro_length_covers_final_checkpoint(self) -> None:
        self.assertEqual(differential.macro_length("0-59=RIGHT;60=B;61-239=RIGHT"), 240)

    def test_atomic_snapshot_summary_extracts_globals_and_actor_tables(self) -> None:
        memory = bytearray(differential.WRAM_SIZE)
        put_u16(memory, 0x0030, 0x0025)
        put_u16(memory, 0x003E, 0x003E)
        put_u16(memory, 0x088B, 0x0100)
        put_u16(memory, 0x0895, 0x5600)
        slot = 3
        put_u16(memory, differential.ACTOR_TABLES["id"] + slot * 2, 0x0002)
        put_u16(memory, differential.ACTOR_TABLES["x"] + slot * 2, 0x0180)
        put_u16(memory, differential.ACTOR_TABLES["y"] + slot * 2, 0x55F0)
        put_u16(memory, differential.ACTOR_TABLES["state"] + slot * 2, 0x002B)
        put_u16(memory, differential.ACTOR_TABLES["x_speed"] + slot * 2, 0xFF80)
        summary = differential.summarize_wram(bytes(memory), {2: "Diddy Kong"})
        self.assertEqual(summary["globals"]["level_id"], 0x0025)
        self.assertEqual(summary["globals"]["entrance_id"], 0x003E)
        self.assertEqual(len(summary["actors"]), 1)
        actor = summary["actors"][0]
        self.assertEqual(actor["slot"], 3)
        self.assertEqual(actor["name"], "Diddy Kong")
        self.assertEqual(actor["state"], 0x002B)
        self.assertEqual(actor["x_speed_signed"], -128)
        self.assertEqual(actor["screen_x_native"], 0x80)
        self.assertEqual(actor["screen_y_native"], -0x10)

    def test_actor_comparison_matches_by_id_and_position_not_slot(self) -> None:
        baseline = [
            {"slot": 4, "id": 0x20, "name": "sprite_0x0020", "x": 100, "y": 200, "state": 1},
            {"slot": 5, "id": 0x20, "name": "sprite_0x0020", "x": 300, "y": 200, "state": 2},
        ]
        candidate = [
            {"slot": 9, "id": 0x20, "name": "sprite_0x0020", "x": 302, "y": 200, "state": 2},
            {"slot": 8, "id": 0x20, "name": "sprite_0x0020", "x": 101, "y": 200, "state": 1},
        ]
        result = differential.compare_actors(baseline, candidate)
        self.assertFalse(result["missingFromCandidate"])
        self.assertFalse(result["extraInCandidate"])
        by_baseline_slot = {match["baselineSlot"]: match for match in result["matched"]}
        self.assertEqual(by_baseline_slot[4]["candidateSlot"], 8)
        self.assertEqual(by_baseline_slot[5]["candidateSlot"], 9)

    def test_memory_diff_reports_changed_ranges_and_pages(self) -> None:
        baseline = bytes(differential.WRAM_SIZE)
        candidate = bytearray(baseline)
        candidate[0x100:0x103] = b"abc"
        candidate[0x205] = 1
        result = differential.changed_memory(baseline, bytes(candidate))
        self.assertEqual(result["changedByteCount"], 4)
        self.assertEqual(result["changedRanges"][0]["start"], "0x7E0100")
        self.assertEqual(result["changedRanges"][0]["bytes"], 3)
        self.assertEqual(result["topChangedPages"][0]["changedBytes"], 3)

    def test_lifecycle_records_spawn_despawn_and_replace(self) -> None:
        checkpoints = [
            {
                "relativeFrame": 0,
                "summary": {"actors": [{"slot": 2, "id": 1}, {"slot": 4, "id": 0x20}]},
            },
            {
                "relativeFrame": 30,
                "summary": {"actors": [{"slot": 2, "id": 2}, {"slot": 5, "id": 0x44}]},
            },
        ]
        changes = differential.actor_lifecycle(checkpoints)[0]["changes"]
        self.assertEqual([change["kind"] for change in changes], ["replace", "despawn", "spawn"])


if __name__ == "__main__":
    unittest.main()
