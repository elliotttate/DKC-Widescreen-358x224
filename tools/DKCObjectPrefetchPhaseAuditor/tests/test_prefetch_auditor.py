from __future__ import annotations

import base64
import contextlib
import gzip
import hashlib
import importlib.util
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent


def load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


pa = load_module("audit_prefetch_phases", ROOT / "audit_prefetch_phases.py")
extractor = load_module("extract_snapshot", ROOT / "extract_snapshot.py")


def put16(memory: bytearray, offset: int, value: int) -> None:
    memory[offset : offset + 2] = (value & 0xFFFF).to_bytes(2, "little")


def make_snapshot(
    relative_frame: int,
    *,
    record: int = 0x37,
    actor_index: int | None = None,
    layer_x: int | None = None,
    x: int = 0x0180,
    state: int = 0,
    animation: int = 0,
) -> bytes:
    memory = bytearray(pa.WRAM_SIZE)
    put16(memory, 0x0030, 0x0017)
    put16(memory, 0x003E, 0x0022)
    put16(memory, 0x088B, relative_frame * 0x20 if layer_x is None else layer_x)
    if actor_index is not None:
        memory[pa.BOOKKEEPING + record] = actor_index
        put16(memory, 0x0D45 + actor_index, 0x0020)
        put16(memory, 0x0B19 + actor_index, x)
        put16(memory, 0x0BC1 + actor_index, 0x0200)
        put16(memory, 0x1029 + actor_index, state)
        put16(memory, 0x10D1 + actor_index, animation)
        put16(memory, 0x15FD + actor_index, record)
    return bytes(memory)


def parse_sequence(values: list[bytes]) -> list[pa.FrameState]:
    return [pa.parse_frame(memory, frame, 100 + frame) for frame, memory in enumerate(values)]


class MappingTests(unittest.TestCase):
    def test_bookmark_source_and_actor_slot_are_cross_checked(self) -> None:
        memory = make_snapshot(0, actor_index=0x06)
        frame = pa.parse_frame(memory, 0, 100)
        observed = frame.records[0x37]
        self.assertEqual(0x06, observed["bookmark"])
        self.assertEqual(3, observed["actor"]["slot"])
        self.assertEqual(0x37, observed["actor"]["sourceRecordCanonical"])
        self.assertTrue(observed["mappingConsistent"])

    def test_negative_source_record_is_canonicalized(self) -> None:
        memory = bytearray(make_snapshot(0, actor_index=0x02))
        put16(memory, 0x15FD + 0x02, (-0x37) & 0xFFFF)
        frame = pa.parse_frame(bytes(memory), 0, 100)
        self.assertEqual(0x37, frame.records[0x37]["actor"]["sourceRecordCanonical"])

    def test_group_marker_is_active_but_not_an_actor_allocation(self) -> None:
        memory = bytearray(make_snapshot(0, actor_index=None))
        memory[pa.BOOKKEEPING + 0x35] = 0xFF
        frame = pa.parse_frame(bytes(memory), 0, 100)
        self.assertTrue(frame.records[0x35]["active"])
        self.assertTrue(frame.records[0x35]["groupMarker"])
        self.assertFalse(frame.records[0x35]["allocated"])


class ClassificationTests(unittest.TestCase):
    STATE = {
        "id": "szst1",
        "records": [{"index": "0x37", "type": "0x0F", "x": "0x0180", "label": "Mincer"}],
    }

    def test_behavior_phase_advancement_and_eligibility(self) -> None:
        stock = parse_sequence(
            [make_snapshot(0), make_snapshot(1), make_snapshot(2), make_snapshot(3, actor_index=0x02)]
        )
        wide = parse_sequence(
            [
                make_snapshot(0),
                make_snapshot(1, actor_index=0x06),
                make_snapshot(2, actor_index=0x06),
                make_snapshot(3, actor_index=0x06, x=0x0190, state=2),
            ]
        )
        analysis = pa.analyze_replays(stock, wide, self.STATE, "stock.gz", "wide.gz", "stock.jsonl", "wide.jsonl")
        row = analysis["records"][0]
        self.assertEqual(1, row["firstWideAllocationFrame"])
        self.assertEqual(3, row["firstStockAllocationFrame"])
        self.assertEqual(3, row["firstStockEligibleFrame"])
        self.assertEqual(0, row["firstWideEligibleFrame"])
        self.assertEqual(2, row["earlyActiveFrameCount"])
        self.assertEqual(2, row["earlyActiveBeforeStockEligibilityFrameCount"])
        self.assertEqual("behavior_phase_advancement", row["classification"])
        self.assertEqual(0x06, row["firstWideAllocationMapping"]["actorIndex"])
        self.assertEqual(0x02, row["firstStockAllocationMapping"]["actorIndex"])
        self.assertEqual(0x37, row["firstWideAllocationMapping"]["sourceRecordCanonical"])
        comparison = row["actorComparisonAtStockAllocationFrame"]
        self.assertTrue(comparison["slotChanged"])
        self.assertIn("position", comparison["differentCategories"])
        self.assertIn("state", comparison["differentCategories"])

    def test_animation_only_drift_is_harmless_visual_prefetch(self) -> None:
        stock = parse_sequence(
            [make_snapshot(0), make_snapshot(1), make_snapshot(2), make_snapshot(3, actor_index=0x02, animation=1)]
        )
        wide = parse_sequence(
            [
                make_snapshot(0),
                make_snapshot(1, actor_index=0x06),
                make_snapshot(2, actor_index=0x06),
                make_snapshot(3, actor_index=0x06, animation=2),
            ]
        )
        row = pa.analyze_replays(stock, wide, self.STATE, "s.gz", "w.gz", "s.jsonl", "w.jsonl")["records"][0]
        self.assertEqual("harmless_visual_prefetch", row["classification"])
        self.assertTrue(row["actorComparisonAtStockAllocationFrame"]["animationOrRenderOnly"])

    def test_wide_only_is_indeterminate_not_declared_harmless(self) -> None:
        stock = parse_sequence([make_snapshot(frame) for frame in range(4)])
        wide = parse_sequence([make_snapshot(0)] + [make_snapshot(frame, actor_index=0x06) for frame in range(1, 4)])
        row = pa.analyze_replays(stock, wide, self.STATE, "s.gz", "w.gz", "s.jsonl", "w.jsonl")["records"][0]
        self.assertEqual("indeterminate_without_stock_allocation", row["classification"])

    def test_left_censored_root_stock_cull_and_reallocation_uses_later_episode(self) -> None:
        state = {
            "id": "szst1",
            "records": [{"index": "0x38", "type": "0x0F", "x": "0x0180", "label": "live record 56"}],
        }
        stock_memory = []
        wide_memory = []
        for frame in range(19):
            layer_x = 0x60 if frame == 18 else 0
            stock_memory.append(
                make_snapshot(
                    frame,
                    record=0x38,
                    actor_index=0x02 if frame == 0 else 0x04 if frame == 18 else None,
                    layer_x=layer_x,
                    x=0x0180,
                    state=0,
                )
            )
            wide_memory.append(
                make_snapshot(
                    frame,
                    record=0x38,
                    actor_index=0x06,
                    layer_x=layer_x,
                    x=0x0190 if frame == 18 else 0x0180,
                    state=2 if frame == 18 else 0,
                )
            )
        row = pa.analyze_replays(
            parse_sequence(stock_memory),
            parse_sequence(wide_memory),
            state,
            "stock.gz",
            "wide.gz",
            "stock.jsonl",
            "wide.jsonl",
        )["records"][0]
        self.assertEqual(0, row["firstStockAllocationFrame"])
        self.assertEqual(0, row["firstWideAllocationFrame"])
        self.assertTrue(row["stockAllocationEpisodes"][0]["leftCensored"])
        self.assertEqual(1, row["stockAllocationEpisodes"][0]["releaseFrame"])
        self.assertEqual(18, row["stockAllocationEpisodes"][1]["startFrame"])
        self.assertEqual([1], row["stockReleaseFrames"])
        self.assertEqual([18], row["stockReallocationFrames"])
        self.assertEqual([18], row["stockBecameEligibleFrames"])
        self.assertEqual(17, row["continuousEarlyActiveFrames"])
        gap = row["stockCullGaps"][0]
        self.assertTrue(gap["wideSameContinuousActorAcrossGap"])
        self.assertEqual(18, gap["comparisonFrame"])
        self.assertEqual("first_subsequent_stock_eligible_allocation", gap["comparisonReason"])
        self.assertEqual("behavior_phase_advancement", row["classification"])
        self.assertEqual(18, row["classificationComparisonFrame"])
        self.assertIn("position", row["classificationActorComparison"]["differentCategories"])
        self.assertIn("state", row["classificationActorComparison"]["differentCategories"])

    def test_wide_persists_after_stock_cull_without_reload_is_indeterminate(self) -> None:
        stock = parse_sequence([make_snapshot(0, actor_index=0x02)] + [make_snapshot(frame) for frame in range(1, 6)])
        wide = parse_sequence([make_snapshot(frame, actor_index=0x06) for frame in range(6)])
        row = pa.analyze_replays(stock, wide, self.STATE, "s.gz", "w.gz", "s.jsonl", "w.jsonl")["records"][0]
        self.assertEqual("wide_persists_stock_culls", row["classification"])
        self.assertEqual("indeterminate", row["classificationDisposition"])
        self.assertEqual(5, row["continuousEarlyActiveFrames"])
        self.assertIsNone(row["stockCullGaps"][0]["stockReallocationFrame"])


class ArchiveTests(unittest.TestCase):
    def test_archive_round_trip_and_hash_verification(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            memories = [make_snapshot(0), make_snapshot(1, actor_index=0x02)]
            with pa.SnapshotArchive(root, "stock") as archive:
                for frame, memory in enumerate(memories):
                    archive.append(memory, {"emulatorFrame": 100 + frame, "sha256": hashlib.sha256(memory).hexdigest().upper()})
            extracted, row = extractor.extract(archive.archive_path, archive.index_path, 1)
            self.assertEqual(memories[1], extracted)
            self.assertEqual(pa.WRAM_SIZE, row["length"])


class FakeBridge:
    def __init__(self, stock_rom: Path, wide_rom: Path) -> None:
        self.stock_rom = str(stock_rom)
        self.wide_rom = str(wide_rom)
        self.variant = ""
        self.frame = 0
        self.commands: list[tuple[str, dict]] = []

    def request(self, command: str, arguments: dict | None = None):
        arguments = arguments or {}
        self.commands.append((command, dict(arguments)))
        if command == "load_rom":
            self.variant = "stock" if arguments["path"] == self.stock_rom else "wide"
            self.frame = 0
            return {}
        if command == "load_state_file":
            self.frame = 0
            return {}
        if command in {"pause", "schedule", "clear_schedule"}:
            return {}
        if command == "step_frames":
            self.frame += int(arguments["count"])
            return {}
        if command == "snapshot_wram":
            actor = None
            x = 0x0180
            state = 0
            if self.variant == "stock" and self.frame == 3:
                actor = 0x02
            if self.variant == "wide" and self.frame >= 1:
                actor = 0x06
                if self.frame == 3:
                    x = 0x0190
                    state = 2
            memory = make_snapshot(self.frame, actor_index=actor, x=x, state=state)
            return {
                "encoding": "base64",
                "data": base64.b64encode(memory).decode("ascii"),
                "sha256": hashlib.sha256(memory).hexdigest().upper(),
                "frame": 100 + self.frame,
                "paused": True,
            }
        raise AssertionError(f"unexpected command {command}")


class RunnerTests(unittest.TestCase):
    def test_runner_completes_stock_before_wide_and_preserves_raw_frames(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            stock_rom = root / "stock.sfc"
            wide_rom = root / "wide.sfc"
            state_path = root / "state.szst1"
            stock_rom.write_bytes(b"stock")
            wide_rom.write_bytes(b"wide")
            state_path.write_bytes(b"state")
            recipe = {
                "schemaVersion": 1,
                "name": "synthetic",
                "states": [
                    {
                        "id": "szst1",
                        "file": state_path.name,
                        "expectedLevel": "0x17",
                        "records": [{"index": "0x37", "type": "0x0F", "x": "0x0180"}],
                        "scenarios": [{"id": "route", "maxFrame": 3, "inputs": {"1": "0-2=RIGHT"}}],
                    }
                ],
            }
            pa.validate_recipe(recipe)
            bridge = FakeBridge(stock_rom, wide_rom)
            output = root / "out"
            report = pa.PrefetchRunner(recipe, bridge, output).run(
                stock_rom, wide_rom, {"szst1": state_path}, set()
            )
            loaded = [args["path"] for command, args in bridge.commands if command == "load_rom"]
            self.assertEqual([str(stock_rom), str(wide_rom)], loaded)
            row = report["cases"][0]["analysis"]["records"][0]
            self.assertEqual("behavior_phase_advancement", row["classification"])
            case = report["cases"][0]
            for replay in (case["stockReplay"], case["wideReplay"]):
                self.assertTrue(Path(replay["archive"]).is_file())
                self.assertTrue(Path(replay["index"]).is_file())
                with gzip.open(replay["archive"], "rb") as handle:
                    self.assertEqual(4 * pa.WRAM_SIZE, len(handle.read()))

    def test_validate_only_never_constructs_bridge(self) -> None:
        original = pa.BridgeClient

        class ForbiddenBridge:
            def __init__(self, *_args, **_kwargs):
                raise AssertionError("validate-only attempted endpoint access")

        pa.BridgeClient = ForbiddenBridge
        output = io.StringIO()
        try:
            with contextlib.redirect_stdout(output):
                code = pa.main(["--validate-only", "--recipe", "poison-pond-prefetch.sample"])
        finally:
            pa.BridgeClient = original
        self.assertEqual(0, code)
        self.assertFalse(json.loads(output.getvalue())["automationContacted"])


class RecipeTests(unittest.TestCase):
    def test_bundled_four_state_and_poison_recipes_validate(self) -> None:
        four = pa.validate_recipe(json.loads((ROOT / "recipes" / "four-user-states-prefetch.sample.json").read_text()))
        poison = pa.validate_recipe(json.loads((ROOT / "recipes" / "poison-pond-prefetch.sample.json").read_text()))
        self.assertEqual({"szst0", "szst1", "szst2", "szst3"}, {state["id"] for state in four["states"]})
        self.assertEqual(3, len(poison["states"][0]["scenarios"]))


if __name__ == "__main__":
    unittest.main()
