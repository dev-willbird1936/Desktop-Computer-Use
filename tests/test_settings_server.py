import importlib.util
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "dcu_settings_server", ROOT / "scripts" / "settings_server.py"
)
assert SPEC and SPEC.loader
SETTINGS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SETTINGS)


class SettingsServerTests(unittest.TestCase):
    def test_clean_settings_clamps_delay(self):
        low = SETTINGS.clean_settings({"PostActionDelayMs": -1})
        high = SETTINGS.clean_settings({"PostActionDelayMs": 9_000})

        self.assertEqual(low["PostActionDelayMs"], 0)
        self.assertEqual(high["PostActionDelayMs"], 5_000)

    def test_clean_settings_rejects_string_boolean(self):
        with self.assertRaises(ValueError):
            SETTINGS.clean_settings({"EnableFocusGuard": "false"})


if __name__ == "__main__":
    unittest.main()
