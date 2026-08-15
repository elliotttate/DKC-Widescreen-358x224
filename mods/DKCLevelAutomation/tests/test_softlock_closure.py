import importlib.util
import sys
import unittest
from pathlib import Path


CLI = Path(__file__).resolve().parents[1] / "cli"
sys.path.insert(0, str(CLI))
SPEC = importlib.util.spec_from_file_location("run_softlock_closure", CLI / "run_softlock_closure.py")
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class SoftlockClosureTests(unittest.TestCase):
    def test_exact_macro_boundaries(self) -> None:
        self.assertIn("0-119=B+RIGHT", MODULE.croctopus_macro())
        self.assertIn("1620-1859=B+RIGHT", MODULE.croctopus_macro())
        self.assertIn("900-1499=RIGHT+Y", MODULE.poison_macro())

    def test_pulse_is_eight_frame_pattern(self) -> None:
        self.assertEqual(
            ["10=B+UP", "11-17=UP", "18=B+UP", "19-25=UP"],
            MODULE.pulse(10, 16, "UP"),
        )

    def test_actor_source_assertion_handles_signed_backlink(self) -> None:
        summary = {"actors": [{"id": 0x5D, "source": 0xFFEF}]}
        MODULE.require_actor(summary, 0x5D, 0x11, "signed")

    def test_slipslide_transition_contract(self) -> None:
        summary = {
            "level": 0x51, "entrance": 0x6D, "lives": 5,
            "secondaryCursor": 0x25, "objectEnd": 0x2C,
            "sectionPointer": 0xD9A0, "sectionPendingEnd": 0x2C,
        }
        MODULE.assert_checkpoint("slipslide", 1, summary)


if __name__ == "__main__":
    unittest.main()
