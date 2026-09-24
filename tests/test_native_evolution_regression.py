from contextlib import redirect_stderr, redirect_stdout
import io
import subprocess
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

TESTS = Path(__file__).resolve().parent
if str(TESTS) not in sys.path:
    sys.path.insert(0, str(TESTS))
import native_evolution_regression as REGRESSION


class NativeEvolutionRegressionTests(unittest.TestCase):
    def test_close_timeout_aborts_owned_client_and_records_error(self):
        result = {"passed": True}
        process = Mock()
        alive = True

        def poll():
            return None if alive else 0

        def abort():
            nonlocal alive
            alive = False

        process.poll.side_effect = poll
        client = Mock()
        client.process = process
        client.close.side_effect = subprocess.TimeoutExpired(["dcu"], 15)
        client.abort.side_effect = abort

        with patch.object(REGRESSION, "_stop_owned_process") as stop_owned:
            REGRESSION._cleanup_client(result, client, graceful=True)

        client.close.assert_called_once_with()
        client.abort.assert_called_once_with()
        stop_owned.assert_not_called()
        self.assertFalse(result["passed"])
        self.assertIn("client.close", result["cleanup_errors"][0])
        self.assertIn("TimeoutExpired", result["cleanup_errors"][0])

    def test_abort_failure_uses_owned_process_fallback_and_still_stops_fixture(self):
        fixture = Mock()
        fixture.pid = 321
        client_process = Mock()
        client_process.poll.return_value = None
        client = Mock()
        client.process = client_process
        client.call.return_value = {}
        client.abort.side_effect = RuntimeError("abort failed")
        stopped = []

        def stop_owned(process):
            stopped.append(process)

        with tempfile.TemporaryDirectory() as temporary_directory:
            with (
                patch.object(REGRESSION.subprocess, "Popen", return_value=fixture),
                patch.object(REGRESSION, "wait_for_windows", return_value=[654]),
                patch.object(REGRESSION, "foreground_and_cursor", return_value=(1, (2, 3))),
                patch.object(REGRESSION, "ConcurrentMcpClient", return_value=client),
                patch.object(REGRESSION, "_select_owned_window", return_value="321:0x28E"),
                patch.object(
                    REGRESSION,
                    "_run_login",
                    side_effect=RuntimeError("scenario operation failed"),
                ),
                patch.object(REGRESSION, "_stop_owned_process", side_effect=stop_owned),
            ):
                with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                    result = REGRESSION._run_scenario(
                        "login",
                        Path("dcu.exe"),
                        Path("fixture.exe"),
                        Path(temporary_directory),
                    )

        self.assertFalse(result["passed"])
        self.assertEqual(result["error"], "RuntimeError('scenario operation failed')")
        self.assertTrue(any("client.abort" in error for error in result["cleanup_errors"]))
        self.assertEqual(stopped, [client_process, fixture])
        client.abort.assert_called_once_with()

    def _run_login_with_responses(self, responses):
        state = {"elements": []}
        client = Mock()
        client.tool.side_effect = responses
        elements = iter(
            [
                {"id": "username"},
                {"id": "password"},
                {"id": "sign-in"},
                {"value": "authenticated: fixture-user"},
                {"id": "status"},  # loop_guard's own element(state, "Fixture status") lookup
            ]
        )
        with (
            patch.object(REGRESSION, "snapshot_selector", return_value=state),
            patch.object(REGRESSION, "element", side_effect=lambda _state, _name: next(elements)),
        ):
            return REGRESSION._run_login(client, "fixture-window", 999)

    @staticmethod
    def _valid_login_call_responses():
        """One correct canned response per client.tool() call _run_login makes, in order,
        covering set_username/set_password plus the F39 workflow-value-transfer and
        password-restore calls that now sit between set_password and the sign-in click."""
        username_length = len(REGRESSION.SYNTHETIC_USERNAME)
        return [
            {"ok": True, "method": "uia_setvalue", "route": "native_uia"},  # set_username (route_set_text)
            {"ok": True, "method": "uia_setvalue"},  # set_password
            {"ok": True, "length": username_length},  # capture_workflow_value
            {"ok": True, "method": "uia_setvalue", "value": REGRESSION.SYNTHETIC_USERNAME},  # round-trip set_value
            {"ok": True, "method": "uia_setvalue"},  # password set_value from value_ref
            {"error": "value and value_ref both supplied"},  # rejected conflict
            {"error": "unknown value_ref"},  # rejected missing reference
            {"ok": True, "method": "uia_setvalue"},  # restore_password
        ]

    def test_login_rejects_false_positive_method_assertions(self):
        cases = (
            (
                "username",
                [{"ok": True, "method": "uia_invoke"}],
                "uia_setvalue",
            ),
            (
                "sign-in",
                [
                    *self._valid_login_call_responses(),
                    {"ok": True, "method": "uia_setvalue"},
                ],
                "uia_invoke",
            ),
        )
        for name, responses, expected_method in cases:
            with self.subTest(name=name):
                with self.assertRaisesRegex(AssertionError, expected_method):
                    self._run_login_with_responses(responses)

    def test_login_rejects_action_resolution_cache_miss_when_hit_expected(self):
        """F31: a repeated task_intent must hit; if the fixture ever reports hit:False for an
        unchanged snapshot, _run_login must surface it as a failure, not silently continue."""
        responses = [
            *self._valid_login_call_responses(),
            {"ok": True, "method": "uia_invoke", "route": "native_uia"},  # sign_in click (route_click)
            {"ok": True},  # remember_action_resolution
            {"hit": False, "reason": "no_cached_recipe"},  # resolve_cached_action, unexpectedly missed
        ]
        with self.assertRaisesRegex(AssertionError, "did not hit"):
            self._run_login_with_responses(responses)

    def test_login_rejects_action_resolution_cache_hit_for_unremembered_intent(self):
        """F31: an unremembered task_intent must miss cleanly (no_cached_recipe); a spurious
        hit would mean the cache key does not actually bind to task_intent."""
        responses = [
            *self._valid_login_call_responses(),
            {"ok": True, "method": "uia_invoke", "route": "native_uia"},  # sign_in click (route_click)
            {"ok": True},  # remember_action_resolution
            {"hit": True, "element_id": "e1"},  # resolve_cached_action, correct hit
            {"hit": True, "element_id": "e1"},  # resolve_cached_action for an unremembered intent -- wrong
        ]
        with self.assertRaisesRegex(AssertionError, "should miss cleanly"):
            self._run_login_with_responses(responses)

    @staticmethod
    def _valid_login_plus_cache_responses():
        return [
            *NativeEvolutionRegressionTests._valid_login_call_responses(),
            {"ok": True, "method": "uia_invoke", "route": "native_uia"},  # sign_in click (route_click)
            {"ok": True},  # remember_action_resolution
            {"hit": True, "element_id": "e1"},  # resolve_cached_action, correct hit
            {"hit": False, "reason": "no_cached_recipe"},  # resolve_cached_action for an unremembered intent, correct miss
            *NativeEvolutionRegressionTests._valid_experience_store_responses(),
        ]

    @staticmethod
    def _valid_experience_store_responses():
        """One correct canned response per client.tool() call _run_experience_store makes."""
        return [
            {"ok": True, "app_version": "1.0.0.0"},  # record_task_experience
            {"found": True, "advisory": False, "outcome": "success", "resolved": {"hit": True, "element_id": "e2"}},  # recall_task_experience, correct hit
            {"found": False, "reason": "not_found"},  # recall_task_experience for an unrecorded intent, correct miss
            {"error": "failure_category must not be set when outcome is 'success'"},  # record_task_experience, correctly rejected
        ]

    def test_login_rejects_record_task_experience_missing_app_version(self):
        """F28-R01: record_task_experience must report the app version it detected; a caller
        cannot trust experience without knowing what version it was observed under."""
        responses = [
            *self._valid_login_plus_cache_responses()[:-4],
            {"ok": True},  # record_task_experience, missing app_version -- wrong
        ]
        with self.assertRaisesRegex(AssertionError, "did not return an app_version"):
            self._run_login_with_responses(responses)

    def test_login_rejects_recall_task_experience_when_not_found_but_expected(self):
        """F28-R01: a just-recorded task_intent must be found on recall; if it is not, the
        record/recall roundtrip is broken."""
        responses = [
            *self._valid_login_plus_cache_responses()[:-3],
            {"found": False, "reason": "not_found"},  # recall_task_experience, unexpectedly missed
        ]
        with self.assertRaisesRegex(AssertionError, "should find a same-version, non-advisory experience"):
            self._run_login_with_responses(responses)

    def test_login_rejects_recall_task_experience_hit_for_unrecorded_intent(self):
        """F28-R01: an unrecorded task_intent must miss cleanly (not_found); a spurious hit
        would mean the experience key does not actually bind to task_intent."""
        responses = [
            *self._valid_login_plus_cache_responses()[:-2],
            {"found": True, "advisory": False},  # recall_task_experience for an unrecorded intent -- wrong
        ]
        with self.assertRaisesRegex(AssertionError, "should miss cleanly"):
            self._run_login_with_responses(responses)

    def test_login_rejects_record_task_experience_that_accepts_a_conflicting_failure_category(self):
        """F28-R02: outcome='success' with a failure_category is a contradictory call and
        must be rejected; a fixture that silently accepts it cannot be trusted."""
        responses = [
            *self._valid_login_plus_cache_responses()[:-1],
            {"ok": True},  # record_task_experience, should have been rejected
        ]
        with self.assertRaisesRegex(AssertionError, "should be rejected"):
            self._run_login_with_responses(responses)

    def test_login_rejects_loop_guard_that_never_diagnoses_a_repeated_no_progress_click(self):
        """F28-R02: three identical no-effect clicks must diagnose a no-progress cycle by the
        third; if the fixture never surfaces should_stop:true, _run_login must fail loudly."""
        responses = [
            *self._valid_login_plus_cache_responses(),
            {"effect": "none", "loop_guard": {"no_progress_count": 1, "should_stop": False}},
            {"effect": "none", "loop_guard": {"no_progress_count": 2, "should_stop": False}},
            {"effect": "none", "loop_guard": {"no_progress_count": 3, "should_stop": False}},  # should have stopped
        ]
        with self.assertRaisesRegex(AssertionError, "diagnose a no-progress cycle"):
            self._run_login_with_responses(responses)

    def test_login_rejects_loop_guard_that_diagnoses_too_early(self):
        """F28-R02: a should_stop verdict before the threshold would mean a single isolated
        no-effect click is being over-flagged as a cycle."""
        responses = [
            *self._valid_login_plus_cache_responses(),
            {"effect": "none", "loop_guard": {"no_progress_count": 1, "should_stop": True, "diagnosis": "too early"}},
            {"effect": "none", "loop_guard": {"no_progress_count": 2, "should_stop": False}},
            {"effect": "none", "loop_guard": {"no_progress_count": 3, "should_stop": True, "diagnosis": "real"}},
        ]
        with self.assertRaisesRegex(AssertionError, "should not stop after only one"):
            self._run_login_with_responses(responses)

    def test_login_rejects_loop_guard_click_that_unexpectedly_had_an_effect(self):
        """The oracle fixture (a read-only status label) must never register a real effect;
        if it does, the test's own assumption about the fixture is invalid and must fail
        loudly rather than silently accept a mismatched loop_guard verdict."""
        responses = [
            *self._valid_login_plus_cache_responses(),
            {"effect": "uia_state_changed", "loop_guard": None},
        ]
        with self.assertRaisesRegex(AssertionError, "no observable effect"):
            self._run_login_with_responses(responses)

    def _run_ambiguity_with_error(self, error):
        initial = {
            "elements": [
                {"id": "e3", "name": "Duplicate action", "y": 10},
                {"id": "e4", "name": "Duplicate action", "y": 20},
                {"id": "command", "name": "Fixture command", "y": 30},
            ]
        }
        replacement = {
            "elements": [
                {"id": "e12", "name": "Duplicate action", "y": 10},
                {"id": "e13", "name": "Duplicate action", "y": 20},
                {"id": "status", "name": "Fixture status", "value": "untouched"},
            ]
        }
        client = Mock()
        client.tool.side_effect = [
            {"ok": True, "method": "uia_setvalue"},
            {"ok": True, "found": True},
            {"ok": True, "found": True},
            {"error": error},
        ]
        elements = iter(
            [
                {"id": "command"},
                {"value": "untouched"},
            ]
        )
        with (
            patch.object(REGRESSION, "snapshot_selector", side_effect=[initial, replacement]),
            patch.object(REGRESSION, "element", side_effect=lambda _state, _name: next(elements)),
        ):
            return REGRESSION._run_ambiguity(client, "fixture-window")

    def test_ambiguity_rejects_generic_timeout_and_not_found_errors(self):
        for error in ("Element 'e4' was not found", "timeout waiting for element 'e4'"):
            with self.subTest(error=error):
                with self.assertRaisesRegex(AssertionError, "ambiguity"):
                    self._run_ambiguity_with_error(error)


if __name__ == "__main__":
    unittest.main()
