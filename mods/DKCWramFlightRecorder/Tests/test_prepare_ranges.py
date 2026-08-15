import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import prepare_ranges as subject


def report(*cases):
    return {"schemaVersion": 1, "tool": "DKCFirstDivergenceLocator", "cases": list(cases)}


def located(state="s0", scenario="right", kind="selectedMemory", truncated=False):
    return {
        "state": state,
        "scenario": scenario,
        "difference": {
            kind: {
                "changedByteCount": 4,
                "changedRangeCount": 2,
                "rangesTruncated": truncated,
                "ranges": [
                    {"start": "0x7E192B", "end": "0x7E192C", "length": 2},
                    {"start": "0x7F0010", "end": "0x7F0011", "length": 2},
                ],
            }
        },
    }


class PrepareRangesTests(unittest.TestCase):
    def test_extracts_selected_memory(self):
        key, ranges = subject.extract_ranges(report(located()), None, "selectedMemory", 64, 4096)
        self.assertEqual("s0/right", key)
        self.assertEqual([(0x7E192B, 0x7E192C), (0x7F0010, 0x7F0011)], ranges)

    def test_render_is_recorder_grammar(self):
        text = subject.render_ranges("s0/right", "selectedMemory", [(0x7E192B, 0x7E192C)])
        self.assertIn("$7E192B-$7E192C first-divergence-selectedMemory-001", text)

    def test_requires_case_when_multiple_located(self):
        with self.assertRaises(subject.RangePreparationError):
            subject.extract_ranges(report(located(), located("s1", "left")), None, "selectedMemory", 64, 4096)

    def test_exact_case_selection(self):
        data = report(located(), located("s1", "left"))
        key, _ = subject.extract_ranges(data, "s1/left", "selectedMemory", 64, 4096)
        self.assertEqual("s1/left", key)

    def test_rejects_truncated_ranges(self):
        with self.assertRaises(subject.RangePreparationError):
            subject.extract_ranges(report(located(truncated=True)), None, "selectedMemory", 64, 4096)

    def test_enforces_byte_bound(self):
        with self.assertRaises(subject.RangePreparationError):
            subject.extract_ranges(report(located()), None, "selectedMemory", 64, 3)

    def test_rejects_non_wram_address(self):
        data = report(located())
        data["cases"][0]["difference"]["selectedMemory"]["ranges"][0]["start"] = "0x80192B"
        with self.assertRaises(subject.RangePreparationError):
            subject.extract_ranges(data, None, "selectedMemory", 64, 4096)

    def test_cli_writes_atomically_shaped_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "report.json"
            output = root / "control" / "ranges.txt"
            source.write_text(json.dumps(report(located())), encoding="utf-8")
            self.assertEqual(0, subject.main([str(source), "--output", str(output)]))
            self.assertTrue(output.exists())
            self.assertEqual([], list(output.parent.glob("ranges.txt.tmp-*")))
            self.assertIn("first-divergence-selectedMemory", output.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
