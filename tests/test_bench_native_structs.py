import ctypes
import importlib.util
from pathlib import Path
import queue
import unittest


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("dcu_bench", ROOT / "bench" / "bench.py")
assert SPEC and SPEC.loader
BENCH = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BENCH)


class BenchNativeStructTests(unittest.TestCase):
    def test_mcp_reader_has_a_bounded_wait(self):
        client = BENCH.Mcp.__new__(BENCH.Mcp)
        client._responses = queue.Queue()

        with self.assertRaises(TimeoutError):
            client._read_response(0.01)

    def test_threadentry32_matches_windows_layout(self):
        self.assertEqual(
            [name for name, _ in BENCH.THREADENTRY32._fields_],
            [
                "dwSize",
                "cntUsage",
                "th32ThreadID",
                "th32OwnerProcessID",
                "tpBasePri",
                "tpDeltaPri",
                "dwFlags",
            ],
        )
        self.assertEqual(ctypes.sizeof(BENCH.THREADENTRY32), 28)


if __name__ == "__main__":
    unittest.main()
