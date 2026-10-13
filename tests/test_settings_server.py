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

    def test_clean_settings_validates_tool_exposure(self):
        self.assertEqual(SETTINGS.clean_settings({})["ToolExposure"], "core")
        self.assertEqual(SETTINGS.clean_settings({"ToolExposure": "all"})["ToolExposure"], "all")
        with self.assertRaises(ValueError):
            SETTINGS.clean_settings({"ToolExposure": "everything"})

    def test_clean_settings_game_and_jev_modes(self):
        clean = SETTINGS.clean_settings({})
        self.assertIs(clean["GameMode"], False)
        self.assertEqual(clean["JevMode"], "off")
        self.assertIs(SETTINGS.clean_settings({"GameMode": True})["GameMode"], True)
        self.assertEqual(SETTINGS.clean_settings({"JevMode": "assist"})["JevMode"], "assist")
        with self.assertRaises(ValueError):
            SETTINGS.clean_settings({"JevMode": "turbo"})
        with self.assertRaises(ValueError):
            SETTINGS.clean_settings({"GameMode": "yes"})
        self.assertIs(clean["FastInput"], False)
        self.assertIs(SETTINGS.clean_settings({"FastInput": True})["FastInput"], True)

    def test_clean_settings_decision_backend_defaults_and_validation(self):
        self.assertEqual(SETTINGS.clean_settings({})["DecisionBackend"], "jev")
        self.assertEqual(SETTINGS.clean_settings({"DecisionBackend": "openai"})["DecisionBackend"], "openai")
        with self.assertRaises(ValueError):
            SETTINGS.clean_settings({"DecisionBackend": "unknown"})
        self.assertEqual(SETTINGS.clean_settings({"OpenAiTimeoutMs": 500})["OpenAiTimeoutMs"], 1_000)
        self.assertEqual(SETTINGS.clean_settings({"OpenAiTimeoutMs": 100_000})["OpenAiTimeoutMs"], 60_000)
        with self.assertRaises(ValueError):
            SETTINGS.clean_settings({"OpenAiTimeoutMs": True})

    def test_clean_settings_humanized_defaults_and_clamps(self):
        clean = SETTINGS.clean_settings({})
        self.assertIs(clean["HumanizedInput"], False)
        self.assertEqual(clean["HumanizedTypoRate"], 0.015)
        clamped = SETTINGS.clean_settings({"HumanizedMouseSpeed": 99, "HumanizedTypoRate": -1})
        self.assertEqual(clamped["HumanizedMouseSpeed"], 4.0)
        self.assertEqual(clamped["HumanizedTypoRate"], 0.0)
        with self.assertRaises(ValueError):
            SETTINGS.clean_settings({"HumanizedTypingSpeed": "fast"})


if __name__ == "__main__":
    unittest.main()
