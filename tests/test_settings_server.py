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


class InputSettingsTests(unittest.TestCase):
    def test_omitted_settings_keep_virtual_cursor_and_disable_optional_input_modes(self):
        clean = SETTINGS.clean_settings({})
        self.assertTrue(clean["ShowVirtualCursor"])
        self.assertTrue(clean["EnableFocusGuard"])
        for key in ("UseRealCursor", "GameMode", "HumanizedInput", "FastInput"):
            self.assertFalse(clean[key])

    def test_every_toggle_combination_survives_settings_validation(self):
        keys = ("UseRealCursor", "GameMode", "HumanizedInput", "FastInput", "ShowVirtualCursor")
        for mask in range(32):
            values = {key: bool(mask & (1 << bit)) for bit, key in enumerate(keys)}
            with self.subTest(mask=mask):
                clean = SETTINGS.clean_settings(values)
                self.assertEqual(values, {key: clean[key] for key in keys})

    def test_cursor_mode_is_independent_of_other_toggles(self):
        clean = SETTINGS.clean_settings({"GameMode": True, "HumanizedInput": True, "FastInput": True})
        self.assertFalse(clean["UseRealCursor"])
        for key in ("GameMode", "HumanizedInput", "FastInput"):
            self.assertTrue(clean[key])

    def test_all_input_toggles_are_visible(self):
        keys = {toggle[0] for toggle in SETTINGS.TOGGLES}
        self.assertTrue({"UseRealCursor", "GameMode", "HumanizedInput", "FastInput"} <= keys)

    def test_humanized_numeric_settings_are_clamped(self):
        clean = SETTINGS.clean_settings({"HumanizedMouseSpeed": 99, "HumanizedTypingSpeed": -1, "HumanizedTypoRate": 1})
        self.assertEqual((4, 0.25, 0.2), (clean["HumanizedMouseSpeed"], clean["HumanizedTypingSpeed"], clean["HumanizedTypoRate"]))

    def test_nonfinite_humanized_settings_are_rejected(self):
        with self.assertRaises(ValueError):
            SETTINGS.clean_settings({"HumanizedMouseSpeed": float("nan")})


if __name__ == "__main__":
    unittest.main()
