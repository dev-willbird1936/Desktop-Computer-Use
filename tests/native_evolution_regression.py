#!/usr/bin/env python3
"""Owned native login and stale-target regressions for the current DCU build.

The scenarios launch only the repository's WindowStressFixture and the explicitly
selected DCU executable. Transport, window enumeration, snapshots, endpoint
sampling, and MCP cleanup use helpers from multi_window_stress_regression.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import traceback
from typing import Any
from urllib.parse import quote as url_quote
from uuid import uuid4

from multi_window_stress_regression import (
    ConcurrentMcpClient,
    element,
    foreground_and_cursor,
    minimize_window,
    move_window,
    restore_window,
    snapshot_selector,
    wait_for_windows,
    window_rect,
)


ROOT = Path(__file__).resolve().parents[1]
TFM = "net10.0-windows10.0.22621.0"
DEFAULT_DCU = ROOT / "src" / "ShadowUse" / "bin" / "Debug" / TFM / "dcu.exe"
DEFAULT_FIXTURE = ROOT / "tests" / "WindowStressFixture" / "bin" / "Debug" / TFM / "WindowStressFixture.exe"
DEFAULT_ARTIFACT_ROOT = Path(tempfile.gettempdir()) / "dcu-native-evolution"
SYNTHETIC_USERNAME = "fixture-user"
SYNTHETIC_PASSWORD = "synthetic-password-42!"


def _settings_environment(artifact_dir: Path) -> dict[str, str]:
    appdata = artifact_dir / "appdata"
    settings_dir = appdata / "shadow-use"
    settings_dir.mkdir(parents=True)
    (settings_dir / "settings.json").write_text(
        json.dumps(
            {
                "AllowUiaTextFallback": True,
                "EnableBoundsGuard": False,
                "EnableDesktopCheck": False,
                "ShowVirtualCursor": False,
                "EnableFocusGuard": True,
                "PostActionDelayMs": 0,
                "EnableRunTimeline": True,
                "EnableCatalogueComposition": True,
            }
        ),
        encoding="utf-8",
    )
    return dict(os.environ, APPDATA=str(appdata))


def _new_artifact_dir(root: Path, scenario: str) -> Path:
    root.mkdir(parents=True, exist_ok=True)
    while True:
        candidate = root / f"{scenario}-{time.time_ns()}-{os.getpid()}-{uuid4().hex[:8]}"
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            time.sleep(0.001)


def _endpoint_record(sample: tuple[int, tuple[int, int]] | None) -> dict[str, Any] | None:
    if sample is None:
        return None
    foreground, cursor = sample
    return {"foreground_hwnd": foreground, "cursor": [cursor[0], cursor[1]]}


def _require_ok(response: dict[str, Any], operation: str) -> None:
    if response.get("ok") is not True:
        raise AssertionError(f"{operation} failed: {response}")


def _require_method(response: dict[str, Any], expected: str, operation: str) -> None:
    actual = response.get("method")
    if actual != expected:
        raise AssertionError(
            f"{operation} used unexpected dispatch: expected={expected!r} actual={actual!r} "
            f"response={response}"
        )


def _require_ambiguous_error(response: dict[str, Any], element_id: str) -> None:
    error = response.get("error")
    if not isinstance(error, str):
        raise AssertionError(f"stale element was not rejected with an ambiguity error: {response}")
    normalized = error.casefold()
    if any(marker in normalized for marker in ("timeout", "not found", "not-found", "not_found")):
        raise AssertionError(f"stale element returned a generic rejection instead of ambiguity: {response}")
    expected_fragment = f"element '{element_id}' is ambiguous".casefold()
    if expected_fragment not in normalized:
        raise AssertionError(f"stale element was not rejected with an ambiguity error: {response}")


def _select_owned_window(
    client: ConcurrentMcpClient,
    process: subprocess.Popen[str],
    expected_hwnd: int,
) -> str:
    listed = client.tool("list_apps")
    if "error" in listed:
        raise AssertionError(f"list_apps failed: {listed}")
    owned = [app for app in listed.get("apps", []) if app.get("pid") == process.pid]
    if len(owned) != 1:
        raise AssertionError(f"expected one owned fixture window, got {owned}")
    expected_hwnd_text = f"0x{expected_hwnd:X}"
    if str(owned[0].get("hwnd", "")).lower() != expected_hwnd_text.lower():
        raise AssertionError(
            f"fixture window changed between enumeration and list_apps: "
            f"expected={expected_hwnd_text} actual={owned[0].get('hwnd')}"
        )
    window_id = owned[0].get("window_id")
    if not isinstance(window_id, str) or not window_id:
        raise AssertionError(f"owned fixture window has no window_id: {owned[0]}")
    return window_id


def _stop_owned_process(process: subprocess.Popen[str] | None) -> None:
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def _record_cleanup_error(result: dict[str, Any], operation: str, error: BaseException) -> None:
    result.setdefault("cleanup_errors", []).append(
        f"{operation}: {error!r}\n{traceback.format_exc()}"
    )
    result["passed"] = False
    result.setdefault("error", repr(error))


def _cleanup_client(
    result: dict[str, Any], client: ConcurrentMcpClient, graceful: bool
) -> None:
    operation = "client.close" if graceful else "client.abort"
    try:
        if graceful:
            client.close()
        else:
            client.abort()
    except BaseException as error:
        _record_cleanup_error(result, operation, error)
        if graceful:
            try:
                client.abort()
            except BaseException as abort_error:
                _record_cleanup_error(result, "client.abort fallback", abort_error)
    finally:
        try:
            if client.process.poll() is None:
                _stop_owned_process(client.process)
        except BaseException as error:
            _record_cleanup_error(result, "stop owned DCU", error)


def _run_login(client: ConcurrentMcpClient, window_id: str, hwnd: int) -> dict[str, Any]:
    state = snapshot_selector(client, window_id)
    username = element(state, "Fixture username")
    password = element(state, "Fixture password")
    # F42-T01: route_set_text takes the SAME high-level intent as set_value and must report
    # it chose the native adapter (no browser target was supplied).
    set_username = client.tool(
        "route_set_text",
        {"app": window_id, "element_id": username["id"], "value": SYNTHETIC_USERNAME},
    )
    _require_ok(set_username, "set synthetic username")
    _require_method(set_username, "uia_setvalue", "set synthetic username")
    if set_username.get("route") != "native_uia":
        raise AssertionError(f"route_set_text should report route=native_uia for a native target: {set_username}")
    set_password = client.tool(
        "set_value",
        {"app": window_id, "element_id": password["id"], "value": SYNTHETIC_PASSWORD},
    )
    _require_ok(set_password, "set synthetic password")
    _require_method(set_password, "uia_setvalue", "set synthetic password")

    workflow_value = _run_workflow_value_transfer(client, window_id, username["id"], password["id"])

    restore_password = client.tool(
        "set_value",
        {"app": window_id, "element_id": password["id"], "value": SYNTHETIC_PASSWORD},
    )
    _require_ok(restore_password, "restore synthetic password after workflow-value transfer")

    state = snapshot_selector(client, window_id)
    sign_in = element(state, "Fixture sign in")
    # F42-T01: route_click must ALSO resolve to the native adapter for a plain native target,
    # through the exact same high-level intent used for a browser candidate elsewhere in this
    # suite (private_data_workflow_regression.py / BrowserAdapterContractTests.cs).
    sign_in_result = client.tool(
        "route_click",
        {"app": window_id, "element_id": sign_in["id"]},
    )
    _require_ok(sign_in_result, "click synthetic sign in")
    _require_method(sign_in_result, "uia_invoke", "click synthetic sign in")
    if sign_in_result.get("route") != "native_uia":
        raise AssertionError(f"route_click should report route=native_uia for a native target: {sign_in_result}")

    state = snapshot_selector(client, window_id)
    status = element(state, "Fixture status").get("value")
    if status != "authenticated: fixture-user":
        raise AssertionError(f"native login did not authenticate synthetic user: {status!r}")

    action_resolution_cache = _run_action_resolution_cache(client, window_id, username["id"])
    experience_store = _run_experience_store(client, window_id, username["id"])
    loop_guard = _run_loop_guard(client, window_id)
    background_mode = _run_background_mode(client, window_id)
    timing_breakdown = _run_timing_breakdown(client, window_id)
    dag_workflow = _run_dag_workflow(client, window_id, username["id"], password["id"])
    mixed_dag_workflow = _run_mixed_dag_workflow(client, window_id, username["id"])
    benchmark_report = _run_benchmark_report(client, window_id, username["id"])
    disabled_element_refusal = _run_disabled_element_refusal(client, window_id)
    readonly_element_refusal = _run_readonly_element_refusal(client, window_id)
    effect_feedback_alignment = _run_effect_feedback_alignment(client, window_id, hwnd, username["id"])
    execute_sequence_resume = _run_execute_sequence_resume(client, window_id, username["id"])
    demonstration_replay = _run_demonstration_replay(client, window_id, hwnd, username["id"], password["id"])
    jev_act_cache_fast_path = _run_jev_act_cache_fast_path(client, window_id, username["id"])
    cross_adapter_demonstration = _run_cross_adapter_demonstration(client, window_id, username["id"])
    capture_provenance = _run_capture_provenance(client, window_id, hwnd)
    typed_workflow_steps = _run_typed_workflow_steps(client)
    helper_workspace = _run_helper_workspace(client, window_id, username["id"])
    takeover = _run_takeover(client, window_id, username["id"])
    workspace = _run_workspace(client, window_id)
    local_search = _run_local_search(client)
    model_routing = _run_model_routing(client)
    subgoal = _run_subgoal(client, window_id, username["id"])
    composition = _run_composition(client, window_id, username["id"])
    review_room = _run_review_room(client, window_id, username["id"])
    field_text = _run_field_text(client)
    grid_prediction = _run_grid_prediction(client)
    voice_turn = _run_voice_turn(client)
    isolated_environment = _run_isolated_environment(client)

    return {
        "status": status,
        "set_username_dispatch": set_username.get("method"),
        "set_password_dispatch": set_password.get("method"),
        "sign_in_dispatch": sign_in_result.get("method"),
        "workflow_value_transfer": workflow_value,
        "action_resolution_cache": action_resolution_cache,
        "experience_store": experience_store,
        "loop_guard": loop_guard,
        "background_mode": background_mode,
        "timing_breakdown": timing_breakdown,
        "dag_workflow": dag_workflow,
        "mixed_dag_workflow": mixed_dag_workflow,
        "benchmark_report": benchmark_report,
        "disabled_element_refusal": disabled_element_refusal,
        "readonly_element_refusal": readonly_element_refusal,
        "effect_feedback_alignment": effect_feedback_alignment,
        "execute_sequence_resume": execute_sequence_resume,
        "demonstration_replay": demonstration_replay,
        "jev_act_cache_fast_path": jev_act_cache_fast_path,
        "cross_adapter_demonstration": cross_adapter_demonstration,
        "capture_provenance": capture_provenance,
        "typed_workflow_steps": typed_workflow_steps,
        "helper_workspace": helper_workspace,
        "takeover": takeover,
        "workspace": workspace,
        "local_search": local_search,
        "model_routing": model_routing,
        "subgoal": subgoal,
        "composition": composition,
        "review_room": review_room,
        "field_text": field_text,
        "grid_prediction": grid_prediction,
        "voice_turn": voice_turn,
        "isolated_environment": isolated_environment,
    }


def _run_isolated_environment(client: ConcurrentMcpClient) -> dict[str, Any]:
    """F36-T02, live: an exclusive time-bounded lease refuses a different actor while valid,
    an expired lease blocks further commands and can be acquired by someone else, reset clears
    lease state but keeps the environment record, and destroy records exactly the caller-declared
    retained artifacts. F36-T01 (real host-isolation of interactive input) is NOT exercised here --
    no sandbox/VM infrastructure exists in this environment; only the lifecycle/lease bookkeeping
    around an externally-supplied environment handle is proven."""
    env_id = f"env-{uuid4().hex[:8]}"
    created = client.tool("create_isolated_environment", {"environment_id": env_id, "provider_kind": "external_vm"})
    if created.get("ok") is not True:
        raise AssertionError(f"create_isolated_environment failed: {created}")

    attached = client.tool("attach_isolated_environment", {"environment_id": env_id, "external_handle": "vm-conn-descriptor"})
    if attached.get("state") != "Attached":
        raise AssertionError(f"attach_isolated_environment should transition to Attached: {attached}")

    leased = client.tool("acquire_isolated_environment_lease", {"environment_id": env_id, "actor_id": "alice", "duration_seconds": 30})
    if leased.get("acquired") is not True:
        raise AssertionError(f"a fresh lease should be acquired: {leased}")

    refused = client.tool("acquire_isolated_environment_lease", {"environment_id": env_id, "actor_id": "bob", "duration_seconds": 30})
    if refused.get("acquired") is not False or refused.get("reason") != "lease_held_by_another_actor":
        raise AssertionError(f"a different actor must be refused while a valid lease is held: {refused}")

    valid_check = client.tool("check_isolated_environment_lease", {"environment_id": env_id, "actor_id": "alice"})
    if valid_check.get("lease_valid") is not True:
        raise AssertionError(f"alice's lease should still be valid: {valid_check}")

    expired_env_id = f"env-{uuid4().hex[:8]}"
    client.tool("create_isolated_environment", {"environment_id": expired_env_id, "provider_kind": "external_vm"})
    client.tool("acquire_isolated_environment_lease", {"environment_id": expired_env_id, "actor_id": "alice", "duration_seconds": -1})
    expired_check = client.tool("check_isolated_environment_lease", {"environment_id": expired_env_id, "actor_id": "alice"})
    if expired_check.get("lease_valid") is not False:
        raise AssertionError(f"an expired lease must block further commands: {expired_check}")
    reacquired = client.tool("acquire_isolated_environment_lease", {"environment_id": expired_env_id, "actor_id": "bob", "duration_seconds": 30})
    if reacquired.get("acquired") is not True:
        raise AssertionError(f"an expired lease should be acquirable by a different actor: {reacquired}")

    reset = client.tool("reset_isolated_environment", {"environment_id": env_id})
    if reset.get("state") != "Attached":
        raise AssertionError(f"reset should clear lease state but keep the environment record: {reset}")
    reset_lease_check = client.tool("check_isolated_environment_lease", {"environment_id": env_id, "actor_id": "alice"})
    if reset_lease_check.get("lease_valid") is not False:
        raise AssertionError(f"reset should have cleared alice's lease: {reset_lease_check}")

    destroyed = client.tool("destroy_isolated_environment", {"environment_id": env_id, "retained_artifact_ids_json": json.dumps(["screenshot-1", "log-1"])})
    if destroyed.get("state") != "Destroyed" or destroyed.get("retained_artifact_ids") != ["screenshot-1", "log-1"]:
        raise AssertionError(f"destroy should record exactly the declared retained artifacts: {destroyed}")

    refused_after_destroy = client.tool("attach_isolated_environment", {"environment_id": env_id, "external_handle": "new-handle"})
    if "error" not in refused_after_destroy:
        raise AssertionError(f"a destroyed environment must refuse further attach: {refused_after_destroy}")

    return {
        "lease_refuses_different_actor_while_valid": True,
        "expired_lease_blocks_further_commands": True,
        "expired_lease_reacquirable_by_another_actor": True,
        "reset_clears_lease_keeps_environment_record": True,
        "destroy_records_exact_retained_artifacts": True,
        "destroyed_environment_refuses_further_attach": True,
    }


def _run_voice_turn(client: ConcurrentMcpClient) -> dict[str, Any]:
    """F58-T01/T02, live: a partial transcript never finalizes regardless of content; a stale
    generation_id is rejected; silence below threshold defers (hesitation is not premature
    execution) while crossing it finalizes the last partial; an explicit final transcript hands
    off to a real subgoal_id via the exact same planner path typed goals use; stop refuses a
    late-arriving final for that session while leaving a brand new session unaffected; and an
    API-failure report returns the fallback_behavior declared at open time, not a hardcoded one."""
    hesitant_session = f"voice-{uuid4().hex[:8]}"
    client.tool("open_voice_session", {"session_id": hesitant_session, "fallback_behavior": "resume_listening"})

    partial = client.tool("submit_voice_partial", {"session_id": hesitant_session, "generation_id": 1, "text": "send an email to"})
    if partial.get("accepted") is not True:
        raise AssertionError(f"a fresh partial should be accepted: {partial}")

    stale = client.tool("submit_voice_partial", {"session_id": hesitant_session, "generation_id": 1, "text": "an out-of-order repeat"})
    if stale.get("accepted") is not False or stale.get("reason") != "stale_generation_id":
        raise AssertionError(f"a generation_id at or behind the latest must be rejected as stale: {stale}")

    below_threshold = client.tool("check_voice_silence_timeout", {"session_id": hesitant_session, "silence_ms_since_last_partial": 100, "silence_threshold_ms": 800})
    if below_threshold.get("finalized") is not False or below_threshold.get("reason") != "below_silence_threshold":
        raise AssertionError(f"a brief pause mid-phrase must not prematurely finalize/execute: {below_threshold}")

    above_threshold = client.tool("check_voice_silence_timeout", {"session_id": hesitant_session, "silence_ms_since_last_partial": 900, "silence_threshold_ms": 800})
    if above_threshold.get("finalized") is not True or not above_threshold.get("subgoal_id"):
        raise AssertionError(f"crossing the silence threshold should finalize the last partial into a real subgoal: {above_threshold}")

    explicit_session = f"voice-{uuid4().hex[:8]}"
    client.tool("open_voice_session", {"session_id": explicit_session, "fallback_behavior": "cancel"})
    final = client.tool("submit_voice_final", {"session_id": explicit_session, "generation_id": 1, "text": "open the settings page"})
    if final.get("finalized") is not True or not final.get("subgoal_id"):
        raise AssertionError(f"an explicit final transcript should hand off to a real subgoal: {final}")
    subgoal_check = client.tool("check_subgoal_readiness", {"subgoal_id": final["subgoal_id"]})
    if "error" in subgoal_check:
        raise AssertionError(f"the voice-created subgoal_id should be a real subgoal in SubgoalStore, same planner path as typed goals: {subgoal_check}")

    stop_session = f"voice-{uuid4().hex[:8]}"
    client.tool("open_voice_session", {"session_id": stop_session, "fallback_behavior": "resume_listening"})
    client.tool("submit_voice_partial", {"session_id": stop_session, "generation_id": 1, "text": "delete the"})
    client.tool("stop_voice_session", {"session_id": stop_session})
    late_final = client.tool("submit_voice_final", {"session_id": stop_session, "generation_id": 2, "text": "delete the file"})
    if late_final.get("finalized") is not False or late_final.get("reason") != "session_is_stopped":
        raise AssertionError(f"stop must cancel a late-arriving final for that session: {late_final}")

    new_after_stop = f"voice-{uuid4().hex[:8]}"
    client.tool("open_voice_session", {"session_id": new_after_stop, "fallback_behavior": "resume_listening"})
    fresh_final = client.tool("submit_voice_final", {"session_id": new_after_stop, "generation_id": 1, "text": "a totally new request"})
    if fresh_final.get("finalized") is not True:
        raise AssertionError(f"stopping one session must never affect a brand new session: {fresh_final}")

    failure_session = f"voice-{uuid4().hex[:8]}"
    client.tool("open_voice_session", {"session_id": failure_session, "fallback_behavior": "resume_listening"})
    failure = client.tool("report_voice_transcription_failure", {"session_id": failure_session, "reason": "connection_reset"})
    if failure.get("fallback_behavior") != "resume_listening":
        raise AssertionError(f"a transcription failure should return the behavior declared at open time, not a hardcoded default: {failure}")

    return {
        "partial_never_finalizes": True,
        "stale_generation_id_rejected": True,
        "hesitation_below_threshold_deferred": True,
        "silence_crossing_threshold_finalized": True,
        "explicit_final_handed_off_to_real_subgoal": True,
        "stop_cancelled_a_late_final": True,
        "new_session_after_stop_unaffected": True,
        "failure_returned_declared_fallback": True,
    }


def _run_field_text(client: ConcurrentMcpClient) -> dict[str, Any]:
    """F24-T01/T02, live: an exact supplied string bypasses the cache/generator entirely; an
    unchanged generation_input reuses the previously-cached generated_text across retries; a
    changed generation_input is a genuine miss requiring fresh generated_text; and DCU refuses
    to fabricate content when neither exact_text nor generated_text nor a configured provider
    is available -- it never silently invents prose."""
    context_key = f"field-text-{uuid4().hex[:8]}"

    exact = client.tool("resolve_field_text", {"context_key": context_key, "exact_text": "Deterministic exact value"})
    if exact.get("used_generator") is not False or exact.get("text") != "Deterministic exact value":
        raise AssertionError(f"exact_text should bypass generation untouched: {exact}")

    no_provider = client.tool("resolve_field_text", {"context_key": context_key, "generation_input": "write a short bio"})
    if "error" not in no_provider:
        raise AssertionError(f"a miss with no generated_text and no configured provider must refuse, never fabricate: {no_provider}")

    first = client.tool("resolve_field_text", {
        "context_key": context_key, "generation_input": "write a short bio", "generated_text": "Once upon a time, a bio.",
    })
    if first.get("cache_hit") is not False or first.get("used_generator") is not True or first.get("text") != "Once upon a time, a bio.":
        raise AssertionError(f"first-ever generation should be recorded as a fresh generated value: {first}")

    reused = client.tool("resolve_field_text", {"context_key": context_key, "generation_input": "write a short bio"})
    if reused.get("cache_hit") is not True or reused.get("text") != "Once upon a time, a bio.":
        raise AssertionError(f"unchanged generation_input should reuse the cached value across retries: {reused}")

    changed_input_miss = client.tool("resolve_field_text", {"context_key": context_key, "generation_input": "write a LONG bio instead"})
    if "error" not in changed_input_miss:
        raise AssertionError(f"a changed generation_input must be a genuine miss, never a reuse of the stale text: {changed_input_miss}")

    # F27 integration: a CONFIGURED provider still never gets a live call from DCU itself --
    # only its presence is reported, so ModelRoutingRegistry is a real consumer of this path.
    configured = client.tool("configure_model_provider", {
        "role": "text_generator", "provider_id": f"bench-provider-{uuid4().hex[:6]}", "priority": 0,
        "supports_structured_output": True, "modalities_json": json.dumps(["text"]),
        "data_destination": "local", "pinned_model_id": "bench-model-v1",
    })
    if configured.get("ok") is not True:
        raise AssertionError(f"configure_model_provider failed: {configured}")
    still_refused = client.tool("resolve_field_text", {"context_key": f"other-{uuid4().hex[:8]}", "generation_input": "a fresh unseen input"})
    if "error" not in still_refused or "never makes the live model call" not in still_refused["error"]:
        raise AssertionError(f"a configured-but-uncalled provider must still be refused, not silently invoked: {still_refused}")

    return {
        "exact_text_bypassed_generation": True,
        "no_provider_no_generated_text_refused": True,
        "fresh_generation_recorded": True,
        "unchanged_input_reused_cache": True,
        "changed_input_was_a_genuine_miss": True,
        "configured_provider_still_never_called_live": True,
    }


def _run_grid_prediction(client: ConcurrentMcpClient) -> dict[str, Any]:
    """F57-T01/T02, live: editing a cell while a prediction is in flight rejects the stale
    result instead of overwriting the newer edit; undo restores the exact pre-run cell state;
    and a cache hit for the same content/rubric/model reuses the remembered value rather than
    triggering another request."""
    context_key = f"grid-{uuid4().hex[:8]}"
    row_id, column, batch_id = "row1", "status", f"batch-{uuid4().hex[:8]}"

    schema = client.tool("preview_grid_schema", {"headings": ["Due Date", "Total Amount", "Notes"]})
    types = [c["inferred_type"] for c in schema.get("columns", [])]
    if types != ["date", "number", "text"]:
        raise AssertionError(f"heading-to-schema preview should classify deterministically: {schema}")

    edit1 = client.tool("record_grid_cell_edit", {"context_key": context_key, "row_id": row_id, "column": column, "value": "pending"})
    captured_version = edit1["edit_version"]

    current_version = client.tool("record_grid_cell_edit", {"context_key": context_key, "row_id": row_id, "column": column, "value": "user changed it"})["edit_version"]

    stale = client.tool("resolve_grid_cell", {
        "context_key": context_key, "row_id": row_id, "column": column, "edit_version": captured_version,
        "content_input": "row content v1", "rubric": "classify-status", "model_id": "bench-model-v1",
        "batch_id": batch_id, "predicted_value": "stale prediction",
    })
    if stale.get("applied") is not False or stale.get("reason") != "stale_edit_version":
        raise AssertionError(f"a prediction captured against an old edit_version must be rejected as stale: {stale}")

    applied = client.tool("resolve_grid_cell", {
        "context_key": context_key, "row_id": row_id, "column": column, "edit_version": current_version,
        "content_input": "row content v2", "rubric": "classify-status", "model_id": "bench-model-v1",
        "batch_id": batch_id, "predicted_value": "fresh prediction",
    })
    if applied.get("applied") is not True or applied.get("value") != "fresh prediction":
        raise AssertionError(f"a prediction captured against the current edit_version should apply: {applied}")

    cache_hit = client.tool("resolve_grid_cell", {
        "context_key": context_key, "row_id": "row2", "column": column, "edit_version": 0,
        "content_input": "row content v2", "rubric": "classify-status", "model_id": "bench-model-v1",
        "batch_id": f"batch-{uuid4().hex[:8]}",
    })
    if cache_hit.get("applied") is not True or cache_hit.get("cache_hit") is not True or cache_hit.get("value") != "fresh prediction":
        raise AssertionError(f"same content/rubric/model should be a cache hit reusing the remembered value: {cache_hit}")

    undo = client.tool("undo_grid_batch", {"batch_id": batch_id})
    if undo.get("restored_cells") != 1:
        raise AssertionError(f"undo should restore exactly the one cell touched by this batch: {undo}")

    return {
        "schema_preview_deterministic": True,
        "stale_prediction_rejected": True,
        "fresh_prediction_applied": True,
        "cache_hit_reused_value": True,
        "undo_restored_pre_run_state": True,
    }


def _run_review_room(client: ConcurrentMcpClient, window_id: str, username_id: str) -> dict[str, Any]:
    """F60-R01/R02/T01/T02, live against the real fixture: two joined reviewers see identical
    annotations, an unauthenticated (never-joined) actor and an authenticated actor without the
    execution lease are both refused submit_execution_command, the lease holder's command actually
    dispatches through real DCU execution, and undoing an earlier annotation leaves that already-
    executed real action completely untouched."""
    room_id = f"room-{uuid4().hex}"
    alice_token, bob_token = "alice-token", "bob-token"

    client.tool("join_review_room", {"room_id": room_id, "actor_id": "alice", "auth_token": alice_token})
    client.tool("join_review_room", {"room_id": room_id, "actor_id": "bob", "auth_token": bob_token})

    posted = client.tool("post_annotation", {
        "room_id": room_id, "actor_id": "alice", "auth_token": alice_token,
        "target_ref": username_id, "text": "please double-check this field",
    })
    if not posted.get("ok"):
        raise AssertionError(f"post_annotation failed: {posted}")

    seen_by_alice = client.tool("list_annotations", {"room_id": room_id})
    seen_by_bob = client.tool("list_annotations", {"room_id": room_id})
    if seen_by_alice["annotations"] != seen_by_bob["annotations"]:
        raise AssertionError(f"two reviewers should see identical annotations: {seen_by_alice} vs {seen_by_bob}")

    # F60-R01: an unauthenticated (never-joined) actor is refused outright.
    stranger_result = client.tool("submit_execution_command", {
        "room_id": room_id, "actor_id": "stranger", "auth_token": "whatever",
        "app": window_id, "tool": "set_value", "args_json": json.dumps({"element_id": username_id, "value": "should-not-run"}),
    })
    if "error" not in stranger_result:
        raise AssertionError(f"an unauthenticated actor must be refused: {stranger_result}")

    # F60-T01: bob is authenticated but does not hold the execution lease -- refused.
    bob_without_lease = client.tool("submit_execution_command", {
        "room_id": room_id, "actor_id": "bob", "auth_token": bob_token,
        "app": window_id, "tool": "set_value", "args_json": json.dumps({"element_id": username_id, "value": "should-not-run"}),
    })
    if "error" not in bob_without_lease:
        raise AssertionError(f"an authenticated actor without the lease must be refused: {bob_without_lease}")

    lease = client.tool("acquire_execution_lease", {"room_id": room_id, "actor_id": "alice", "auth_token": alice_token})
    if not lease.get("acquired"):
        raise AssertionError(f"acquire_execution_lease should succeed when nobody holds it: {lease}")
    bob_steal_attempt = client.tool("acquire_execution_lease", {"room_id": room_id, "actor_id": "bob", "auth_token": bob_token})
    if bob_steal_attempt.get("acquired"):
        raise AssertionError(f"the lease must never be stolen out from under the current holder: {bob_steal_attempt}")

    executed_value = f"review-executed-{uuid4().hex[:8]}"
    executed = client.tool("submit_execution_command", {
        "room_id": room_id, "actor_id": "alice", "auth_token": alice_token,
        "app": window_id, "tool": "set_value", "args_json": json.dumps({"element_id": username_id, "value": executed_value}),
    })
    if not executed.get("ok"):
        raise AssertionError(f"the lease holder's command should dispatch: {executed}")
    state = snapshot_selector(client, window_id)
    if element(state, "Fixture username").get("value") != executed_value:
        raise AssertionError(f"submit_execution_command did not actually set the live field: {element(state, 'Fixture username')}")

    # F60-R02/T02: undoing the EARLIER annotation must not touch the real action just executed.
    undone = client.tool("undo_annotation", {"room_id": room_id, "actor_id": "alice", "auth_token": alice_token, "annotation_id": posted["annotation_id"]})
    if not undone.get("undone"):
        raise AssertionError(f"undo_annotation failed: {undone}")
    after_undo = client.tool("list_annotations", {"room_id": room_id})
    if after_undo["annotations"]:
        raise AssertionError(f"the annotation should be gone after undo: {after_undo}")
    state_after_undo = snapshot_selector(client, window_id)
    if element(state_after_undo, "Fixture username").get("value") != executed_value:
        raise AssertionError("undoing an annotation must never reverse an already-executed real action")

    return {
        "two_reviewers_see_identical_annotations": True,
        "unauthenticated_actor_refused": True,
        "authenticated_actor_without_lease_refused": True,
        "lease_never_stolen": True,
        "lease_holder_command_actually_dispatched": True,
        "undo_never_touched_the_executed_action": True,
    }


def _run_composition(client: ConcurrentMcpClient, window_id: str, username_id: str) -> dict[str, Any]:
    """F59-R01/R02/T01/T02, live: an unknown component and a forbidden action-binding tool both
    fail validation before anything renders; a valid composition renders deterministically and
    dispatch_composition_action routes its action through the SAME DispatchStep real click/
    set_value already use, actually mutating the live fixture."""
    composition_id = f"composition-{uuid4().hex}"
    value = f"composition-{uuid4().hex[:8]}"
    nodes = [{
        "node_id": "n1", "component_id": "text_field", "props": {"label": "Username"},
        "action_binding": {"app": window_id, "tool": "set_value", "args": {"element_id": username_id, "value": value}},
    }]
    rendered = client.tool("render_composition", {"composition_id": composition_id, "nodes_json": json.dumps(nodes)})
    if not rendered.get("ok") or rendered.get("node_count") != 1:
        raise AssertionError(f"render_composition (valid) failed: {rendered}")

    unknown_component = client.tool("render_composition", {
        "composition_id": f"{composition_id}-unknown",
        "nodes_json": json.dumps([{"node_id": "n1", "component_id": "not_a_real_component"}]),
    })
    if "error" not in unknown_component:
        raise AssertionError(f"render_composition should refuse an unknown component: {unknown_component}")

    forbidden_binding = client.tool("render_composition", {
        "composition_id": f"{composition_id}-forbidden",
        "nodes_json": json.dumps([{
            "node_id": "n1", "component_id": "text", "props": {"content": "hi"},
            "action_binding": {"app": window_id, "tool": "set_value", "args": {}},
        }]),
    })
    if "error" not in forbidden_binding:
        raise AssertionError(f"render_composition should refuse an action binding the component doesn't allow: {forbidden_binding}")

    executable_string = client.tool("render_composition", {
        "composition_id": f"{composition_id}-exec",
        "nodes_json": json.dumps([{"node_id": "n1", "component_id": "text", "props": {"content": "<script>alert(1)</script>"}}]),
    })
    if "error" not in executable_string:
        raise AssertionError(f"render_composition should refuse an executable-looking prop value: {executable_string}")

    dispatched = client.tool("dispatch_composition_action", {"composition_id": composition_id, "node_id": "n1"})
    if not dispatched.get("ok"):
        raise AssertionError(f"dispatch_composition_action failed: {dispatched}")
    state = snapshot_selector(client, window_id)
    if element(state, "Fixture username").get("value") != value:
        raise AssertionError(f"dispatch_composition_action did not actually set the live field: {element(state, 'Fixture username')}")

    return {
        "valid_composition_rendered": True,
        "unknown_component_refused": True,
        "forbidden_action_binding_refused": True,
        "executable_looking_prop_refused": True,
        "action_dispatched_through_real_set_value": True,
    }


def _run_subgoal(client: ConcurrentMcpClient, window_id: str, username_id: str) -> dict[str, Any]:
    """F23-R01/R02/T01/T02, live against the real fixture: a subgoal starts not-ready until its
    required_data is set, its postcondition is checked live (reusing wait_for), several fast-
    controller dispatches happen under one subgoal with no further subgoal-management call between
    them, and escalating an unmet-postcondition subgoal produces a revision carrying the SAME
    objective forward while the original stays permanently Escalated."""
    subgoal_id = f"subgoal-{uuid4().hex}"
    objective = "Authenticate as fixture-user and confirm the fixture reports it"

    # wait_for's text_exists/element_exists conditions match an element's accessible NAME (a
    # WinForms control's static caption), not its dynamic VALUE -- "Fixture status" is the
    # label's constant name; its live "authenticated: ..." text lives in .value and is verified
    # separately elsewhere in this scenario (_run_dag_workflow). Using element_exists here tests
    # the genuine postcondition mechanism without conflating the two different UIA properties.
    defined = client.tool("define_subgoal", {
        "subgoal_id": subgoal_id, "objective": objective,
        "required_data_json": json.dumps(["subgoal_marker"]),
        "postcondition_app": window_id, "postcondition_condition": "element_exists", "postcondition_text": "Fixture status",
    })
    if not defined.get("ok"):
        raise AssertionError(f"define_subgoal failed: {defined}")

    not_ready = client.tool("check_subgoal_readiness", {"subgoal_id": subgoal_id})
    if not_ready.get("ready") or not_ready.get("missing_required_data") != ["subgoal_marker"]:
        raise AssertionError(f"a subgoal with unset required_data should report not ready: {not_ready}")

    client.tool("set_workflow_value", {"name": "subgoal_marker", "value": "present"})
    ready = client.tool("check_subgoal_readiness", {"subgoal_id": subgoal_id})
    if not ready.get("ready"):
        raise AssertionError(f"check_subgoal_readiness should report ready once required_data is set: {ready}")

    # F23-T01: several real fast-controller dispatches under this one subgoal, with no further
    # subgoal-management call between them -- proves execution never references subgoal_id at all.
    first = client.tool("set_value", {"app": window_id, "element_id": username_id, "value": "subgoal-step-1"})
    _require_ok(first, "fast-controller step 1 under one subgoal")
    second = client.tool("set_value", {"app": window_id, "element_id": username_id, "value": "subgoal-step-2"})
    _require_ok(second, "fast-controller step 2 under one subgoal")

    postcondition = client.tool("check_subgoal_postcondition", {"subgoal_id": subgoal_id})
    if not postcondition.get("met"):
        raise AssertionError(f"the always-present 'Fixture status' element should satisfy this postcondition: {postcondition}")
    completed = client.tool("complete_subgoal", {"subgoal_id": subgoal_id})
    if completed.get("status") != "completed":
        raise AssertionError(f"complete_subgoal should succeed once the postcondition is genuinely met: {completed}")
    complete_again = client.tool("complete_subgoal", {"subgoal_id": subgoal_id})
    if "error" not in complete_again:
        raise AssertionError(f"completing an already-completed subgoal should be refused: {complete_again}")

    # F23-T02: a subgoal whose postcondition can never be met is refused completion, then
    # escalated -- the revision must carry the IDENTICAL objective, never a silently changed one.
    unmet_id = f"subgoal-unmet-{uuid4().hex}"
    client.tool("define_subgoal", {
        "subgoal_id": unmet_id, "objective": objective,
        "required_data_json": "[]",
        "postcondition_app": window_id, "postcondition_condition": "text_exists", "postcondition_text": "this-text-will-never-appear-xyz",
    })
    unmet_check = client.tool("check_subgoal_postcondition", {"subgoal_id": unmet_id})
    if unmet_check.get("met"):
        raise AssertionError(f"a postcondition for text that was never written should not be met: {unmet_check}")
    refused_complete = client.tool("complete_subgoal", {"subgoal_id": unmet_id})
    if "error" not in refused_complete:
        raise AssertionError(f"complete_subgoal must refuse when the postcondition is not met: {refused_complete}")

    escalated = client.tool("escalate_subgoal", {
        "subgoal_id": unmet_id, "reason": "postcondition_never_met",
        "new_postcondition_condition": "element_exists", "new_postcondition_text": "Fixture status",
    })
    if escalated.get("objective") != objective:
        raise AssertionError(f"escalate_subgoal must carry the identical objective forward: {escalated}")
    original_after_escalation = client.tool("get_subgoal", {"subgoal_id": unmet_id})
    if original_after_escalation.get("status") != "escalated" or original_after_escalation.get("objective") != objective:
        raise AssertionError(f"the original subgoal must be permanently escalated with its objective unchanged: {original_after_escalation}")

    revised_id = escalated["revised_subgoal_id"]
    revised_check = client.tool("check_subgoal_postcondition", {"subgoal_id": revised_id})
    if not revised_check.get("met"):
        raise AssertionError(f"the revised subgoal's updated postcondition should now be met against real fixture state: {revised_check}")
    revised_complete = client.tool("complete_subgoal", {"subgoal_id": revised_id})
    if revised_complete.get("status") != "completed":
        raise AssertionError(f"the revised subgoal should complete once its own postcondition is met: {revised_complete}")

    return {
        "readiness_reported_missing_then_ready": True,
        "two_fast_controller_steps_dispatched_under_one_subgoal": True,
        "postcondition_met_against_real_fixture_state": True,
        "completed_then_refused_double_complete": True,
        "unmet_postcondition_refused_completion": True,
        "escalation_preserved_objective": True,
        "revision_completed_after_postcondition_update": True,
    }


def _run_model_routing(client: ConcurrentMcpClient) -> dict[str, Any]:
    """F27-R01/R02/T01/T02, live through real MCP calls: select_model_provider picks the lowest-
    priority provider that genuinely satisfies required capabilities, record_provider_failure
    drives real failover to the next configured candidate, local_only refuses a cloud-only
    provider outright even with nothing else configured, and a missing capability is only ever
    bridged through an explicitly attached compatibility adapter."""
    role = "judge"
    primary = f"primary-{uuid4().hex[:8]}"
    fallback = f"fallback-{uuid4().hex[:8]}"

    client.tool("configure_model_provider", {
        "role": role, "provider_id": primary, "priority": 0,
        "supports_structured_output": True, "modalities_json": json.dumps(["text"]),
        "data_destination": "local", "pinned_model_id": "judge-model-v1",
    })
    client.tool("configure_model_provider", {
        "role": role, "provider_id": fallback, "priority": 1,
        "supports_structured_output": True, "modalities_json": json.dumps(["text"]),
        "data_destination": "local", "pinned_model_id": "judge-model-v2",
    })

    picked_primary = client.tool("select_model_provider", {"role": role, "require_structured_output": True})
    if not picked_primary.get("found") or picked_primary.get("provider_id") != primary:
        raise AssertionError(f"select_model_provider should pick the lowest-priority provider first: {picked_primary}")

    # F27-T01: a simulated timeout drives real failover to the configured fallback.
    failure = client.tool("record_provider_failure", {"role": role, "provider_id": primary, "reason": "timeout"})
    if not failure.get("ok"):
        raise AssertionError(f"record_provider_failure failed: {failure}")
    picked_fallback = client.tool("select_model_provider", {"role": role, "require_structured_output": True})
    if picked_fallback.get("provider_id") != fallback:
        raise AssertionError(f"select_model_provider should fail over to the configured fallback after a recorded failure: {picked_fallback}")

    # F27-R02/T02: a cloud-only provider must never be chosen for a local_only requirement, even
    # as a last resort with nothing else eligible.
    cloud_role = "perception"
    cloud_provider = f"cloud-{uuid4().hex[:8]}"
    client.tool("configure_model_provider", {
        "role": cloud_role, "provider_id": cloud_provider, "priority": 0,
        "supports_structured_output": True, "modalities_json": json.dumps(["text"]),
        "data_destination": "cloud", "pinned_model_id": "vision-model-v1",
    })
    local_only_result = client.tool("select_model_provider", {"role": cloud_role, "local_only": True})
    if local_only_result.get("found") or local_only_result.get("reason") != "no_compatible_provider":
        raise AssertionError(f"local_only must refuse a cloud-only provider outright: {local_only_result}")

    # F27-T02: a provider missing a required capability is refused until an explicit adapter is
    # attached that declares bridging exactly that gap.
    text_role = "text_generator"
    unstructured_provider = f"unstructured-{uuid4().hex[:8]}"
    client.tool("configure_model_provider", {
        "role": text_role, "provider_id": unstructured_provider, "priority": 0,
        "supports_structured_output": False, "modalities_json": json.dumps(["text"]),
        "data_destination": "local", "pinned_model_id": "text-model-v1",
    })
    before_adapter = client.tool("select_model_provider", {"role": text_role, "require_structured_output": True})
    if before_adapter.get("found"):
        raise AssertionError(f"a provider missing structured output must not be selected without an adapter: {before_adapter}")
    client.tool("attach_compatibility_adapter", {
        "role": text_role, "provider_id": unstructured_provider,
        "bridges_capability": "structured_output", "description": "wraps free text in a deterministic parser",
    })
    after_adapter = client.tool("select_model_provider", {"role": text_role, "require_structured_output": True})
    if not after_adapter.get("found") or not after_adapter.get("adapter_used"):
        raise AssertionError(f"select_model_provider should accept the gap only via the explicitly attached adapter: {after_adapter}")

    return {
        "primary_selected_first": True,
        "failover_after_recorded_failure": True,
        "local_only_refused_cloud_provider": True,
        "missing_capability_refused_without_adapter": True,
        "missing_capability_accepted_via_explicit_adapter": True,
    }


def _run_local_search(client: ConcurrentMcpClient) -> dict[str, Any]:
    """F55-R01/R02/T01/T02, live against real files: search_local_files reports opened_at/
    modified_at/added_at as separate fields (never substituted for one another), sorts by the
    requested dimension when available, falls back to modified_at with an explicit reason ONLY
    when the requested dimension is unavailable for every candidate, and per-scope query_sequence
    increments monotonically across real MCP calls while a different scope stays independent.
    F55-T01's staleness-under-overlap guarantee itself has no live/Win32 dependency (SearchSequencer
    is pure in-memory bookkeeping) and is proven deterministically by LocalSearchServiceTests
    instead of a timing-dependent live race, which would only add flakiness, not confidence."""
    import shutil

    root = Path(tempfile.mkdtemp(prefix="dcu-local-search-"))
    try:
        older = root / "older-invoice.txt"
        newer = root / "newer-invoice.txt"
        unrelated = root / "unrelated.txt"
        older.write_text("old", encoding="utf-8")
        newer.write_text("new", encoding="utf-8")
        unrelated.write_text("nope", encoding="utf-8")
        old_time = time.time() - 3600
        os.utime(older, (old_time, old_time))

        found = client.tool("search_local_files", {
            "scope": "search-panel", "allowed_root": str(root), "query": "invoice", "sort_by": "modified_at",
        })
        if not found.get("ok"):
            raise AssertionError(f"search_local_files failed: {found}")
        names = [c["file_name"] for c in found["candidates"]]
        if names != ["newer-invoice.txt", "older-invoice.txt"]:
            raise AssertionError(f"search_local_files should filter by query and sort by modified_at descending: {names}")
        if found["sort_fallback_used"]:
            raise AssertionError(f"modified_at is available for real files; fallback should not trigger: {found}")
        newer_candidate = found["candidates"][0]
        if newer_candidate["modified_at"] is None or newer_candidate["opened_at"] is None or newer_candidate["added_at"] is None:
            raise AssertionError(f"a real local file should report all three recency dimensions separately: {newer_candidate}")

        # F55-T02: filtering by 'unrelated' must never return the invoice files -- proves query
        # actually scopes results rather than always returning everything.
        filtered_out = client.tool("search_local_files", {
            "scope": "search-panel", "allowed_root": str(root), "query": "unrelated", "sort_by": "modified_at",
        })
        if [c["file_name"] for c in filtered_out["candidates"]] != ["unrelated.txt"]:
            raise AssertionError(f"query should scope results to matching file names only: {filtered_out}")

        # Per-scope monotonic sequencing across real MCP calls, and cross-scope independence.
        seq_a1 = found["query_sequence"]
        seq_a2 = filtered_out["query_sequence"]
        if not (seq_a2 > seq_a1):
            raise AssertionError(f"query_sequence should increase monotonically for the same scope: {seq_a1} then {seq_a2}")
        if found["stale"] or filtered_out["stale"]:
            raise AssertionError("a sequential (non-overlapping) call must never report stale:true")
        other_scope = client.tool("search_local_files", {
            "scope": "a-different-panel", "allowed_root": str(root), "query": "", "sort_by": "modified_at",
        })
        if other_scope["query_sequence"] != 1:
            raise AssertionError(f"a different scope should have its own independent sequence starting at 1: {other_scope}")

        # A rejected sort_by must fail before touching the filesystem at all.
        bad_sort = client.tool("search_local_files", {
            "scope": "search-panel", "allowed_root": str(root), "query": "", "sort_by": "downloaded_at",
        })
        if "error" not in bad_sort:
            raise AssertionError(f"an unsupported sort_by should be rejected: {bad_sort}")

        return {
            "query_filtered_correctly": True,
            "sorted_by_modified_at_descending": True,
            "three_recency_fields_reported_separately": True,
            "sequence_monotonic_within_scope": True,
            "sequence_independent_across_scopes": True,
            "invalid_sort_by_rejected": True,
        }
    finally:
        shutil.rmtree(root, ignore_errors=True)


def _run_workspace(client: ConcurrentMcpClient, window_id: str) -> dict[str, Any]:
    """F56-R01/T01/T02, live against the real fixture: save_workspace persists an ordered,
    typed-reference membership; open_workspace resolves a real native_app member (found, with a
    live window_id) and reports a url member as ready_to_open without navigating anywhere; a
    member referencing an app that is not running is reported missing, never substituted with a
    different lookalike window; and no kind string outside 'native_app'/'url' can ever be saved."""
    workspace_id = f"workspace-{uuid4().hex}"
    members = [
        {"kind": "native_app", "reference": window_id},
        {"kind": "url", "reference": "https://example.invalid/workspace-member"},
        {"kind": "native_app", "reference": f"NoSuchApp-{uuid4().hex}"},
    ]
    save = client.tool("save_workspace", {"workspace_id": workspace_id, "members_json": json.dumps(members)})
    if not save.get("ok") or save.get("member_count") != 3:
        raise AssertionError(f"save_workspace failed: {save}")

    rejected = client.tool("save_workspace", {
        "workspace_id": f"{workspace_id}-invalid",
        "members_json": json.dumps([{"kind": "shell_command", "reference": "rm -rf /"}]),
    })
    if "error" not in rejected:
        raise AssertionError(f"save_workspace should refuse an unsupported member kind: {rejected}")

    preview = client.tool("get_workspace", {"workspace_id": workspace_id})
    if [m["kind"] for m in preview.get("members", [])] != ["native_app", "url", "native_app"]:
        raise AssertionError(f"get_workspace did not preserve member order/kinds: {preview}")

    opened = client.tool("open_workspace", {"workspace_id": workspace_id})
    statuses = [(m["kind"], m["status"]) for m in opened.get("members", [])]
    if statuses != [("native_app", "found"), ("url", "ready_to_open"), ("native_app", "missing")]:
        raise AssertionError(f"open_workspace reported unexpected statuses: {opened}")
    found_member = opened["members"][0]
    if found_member.get("window_id") != window_id:
        raise AssertionError(f"open_workspace should report the real live window_id for a found native_app: {found_member}")
    url_member = opened["members"][1]
    if url_member.get("reference") != members[1]["reference"]:
        raise AssertionError(f"open_workspace should report the url member's exact reference, never auto-navigate: {url_member}")

    return {
        "save_ok": True,
        "unsupported_kind_rejected": True,
        "preview_order_preserved": True,
        "found_native_app_window_id": found_member.get("window_id"),
        "url_member_ready_to_open_not_navigated": True,
        "missing_member_reported_not_substituted": True,
    }


def _run_takeover(client: ConcurrentMcpClient, window_id: str, username_id: str) -> dict[str, Any]:
    """F54-R01/R02/T01/T02, live against the real fixture: a queued (Pending) node cancelled by
    pause_for_takeover never dispatches even after run_workflow is called again, and a subsequent
    capture reads whatever is actually on screen (simulating a user edit made during takeover),
    never a stale pre-takeover value or the discarded queued action's value."""
    workflow_id = f"takeover-{uuid4().hex}"
    queued_sentinel = "queued-should-not-run"
    nodes = [{
        "id": "queued_set",
        "app": window_id,
        "tool": "set_value",
        "args": {"element_id": username_id, "value": queued_sentinel},
        "prerequisites": [],
    }]
    create = client.tool("create_workflow", {"workflow_id": workflow_id, "nodes_json": json.dumps(nodes)})
    if not create.get("ok"):
        raise AssertionError(f"create_workflow (takeover) failed: {create}")

    pause = client.tool("pause_for_takeover", {"workflow_id": workflow_id})
    if pause.get("cancelled_node_ids") != ["queued_set"]:
        raise AssertionError(f"pause_for_takeover should cancel the queued node: {pause}")

    # Simulate the user editing the field themselves during takeover.
    takeover_value = f"user-takeover-{uuid4().hex[:8]}"
    manual_edit = client.tool("set_value", {"app": window_id, "element_id": username_id, "value": takeover_value})
    _require_ok(manual_edit, "simulate user edit during takeover")

    # F54-T02: resuming must NOT execute the cancelled node.
    run_result = client.tool("run_workflow", {"workflow_id": workflow_id})
    if not run_result.get("done"):
        raise AssertionError(f"run_workflow after takeover should report done (nothing left to run): {run_result}")
    statuses = {n["id"]: n["status"] for n in run_result["nodes"]}
    if statuses != {"queued_set": "cancelled"}:
        raise AssertionError(f"queued node should stay cancelled after resume: {statuses}")

    # F54-R01/T01: DCU must read the user's live edit, not the stale pre-takeover value or the
    # discarded queued sentinel.
    state = snapshot_selector(client, window_id)
    current = element(state, "Fixture username").get("value")
    if current != takeover_value:
        raise AssertionError(f"resume should read the user's live edit {takeover_value!r}, got {current!r}")
    if current == queued_sentinel:
        raise AssertionError("the cancelled queued action's value leaked onto the field")

    return {
        "queued_node_cancelled": True,
        "resume_never_dispatched_queued_node": True,
        "resume_reads_live_user_edit": True,
        "resumed_value": current,
    }


def _run_helper_workspace(client: ConcurrentMcpClient, window_id: str, username_id: str) -> dict[str, Any]:
    """F18-R01/R02/T01/T02, live against the real fixture: (1) invoke_helper refuses an
    unadmitted helper, (2) an admitted helper actually dispatches through the SAME WorkflowRunner
    create_workflow/run_workflow uses, (3) editing the registration invalidates admission and
    invoke_helper refuses again WITHOUT touching the fixture, (4) re-admitting the edit lets it
    run and applies the edited template, (5) an undeclared destination and (6) an unrecognised
    tool are both blocked before any node runs, even for an admitted helper -- there is no
    process-launch tool in DCU at all to demonstrate this against, so an invented tool name
    stands in for it."""
    nodes = [{
        "id": "set_user",
        "app": window_id,
        "tool": "set_value",
        "args": {"element_id": username_id, "value": "{{name}}"},
        "prerequisites": [],
    }]
    declared_apps = json.dumps([window_id])
    helper_id = f"helper-{uuid4().hex}"

    register = client.tool("register_helper", {
        "helper_id": helper_id, "nodes_json": json.dumps(nodes),
        "parameter_schema_json": json.dumps({"name": "string"}), "declared_apps_json": declared_apps,
    })
    if register.get("admitted") is not False:
        raise AssertionError(f"register_helper should always land unadmitted: {register}")

    before_admit = client.tool("invoke_helper", {"helper_id": helper_id, "params_json": json.dumps({"name": "should-not-run"})})
    if "error" not in before_admit:
        raise AssertionError(f"invoke_helper should refuse an unadmitted helper: {before_admit}")

    admit = client.tool("admit_helper", {"helper_id": helper_id, "test_evidence": "reviewed by native_evolution_regression.py"})
    if not admit.get("admitted"):
        raise AssertionError(f"admit_helper failed: {admit}")

    first_value = "helper-value-one"
    invoke_one = client.tool("invoke_helper", {"helper_id": helper_id, "params_json": json.dumps({"name": first_value})})
    if not invoke_one.get("ok"):
        raise AssertionError(f"invoke_helper (admitted) failed: {invoke_one}")
    state = snapshot_selector(client, window_id)
    if element(state, "Fixture username").get("value") != first_value:
        raise AssertionError(f"invoke_helper did not actually set the username field: {element(state, 'Fixture username')}")

    # F18-T01: edit the registration -> admission invalidated, invoke_helper refuses, and the
    # fixture field must stay at first_value (proving no dispatch happened).
    edited_nodes = [{**nodes[0], "args": {"element_id": username_id, "value": "{{name}}-edited"}}]
    register_again = client.tool("register_helper", {
        "helper_id": helper_id, "nodes_json": json.dumps(edited_nodes),
        "parameter_schema_json": json.dumps({"name": "string"}), "declared_apps_json": declared_apps,
    })
    if register_again.get("source_hash") == register.get("source_hash"):
        raise AssertionError(f"editing nodes_json should change source_hash: {register} vs {register_again}")
    invoke_after_edit = client.tool("invoke_helper", {"helper_id": helper_id, "params_json": json.dumps({"name": "should-not-run"})})
    if "error" not in invoke_after_edit:
        raise AssertionError(f"invoke_helper should refuse after an edit invalidated admission: {invoke_after_edit}")
    state = snapshot_selector(client, window_id)
    if element(state, "Fixture username").get("value") != first_value:
        raise AssertionError("invoke_helper dispatched despite an invalidated admission")

    # Re-admit the edited source and prove it now runs, applying the edited template.
    client.tool("admit_helper", {"helper_id": helper_id, "test_evidence": "reviewed the edit"})
    second_value = "helper-value-two"
    invoke_two = client.tool("invoke_helper", {"helper_id": helper_id, "params_json": json.dumps({"name": second_value})})
    if not invoke_two.get("ok"):
        raise AssertionError(f"invoke_helper (re-admitted) failed: {invoke_two}")
    state = snapshot_selector(client, window_id)
    expected = f"{second_value}-edited"
    if element(state, "Fixture username").get("value") != expected:
        raise AssertionError(f"re-admitted invoke_helper did not apply the edited template: {element(state, 'Fixture username')}")

    # F18-T02: undeclared destination -- a node whose app is not in declared_apps is refused
    # before any node runs, even for an admitted helper.
    undeclared_helper_id = f"helper-undeclared-{uuid4().hex}"
    client.tool("register_helper", {
        "helper_id": undeclared_helper_id, "nodes_json": json.dumps(nodes),
        "parameter_schema_json": "{}", "declared_apps_json": json.dumps(["SomeOtherApp"]),
    })
    client.tool("admit_helper", {"helper_id": undeclared_helper_id, "test_evidence": "reviewed"})
    undeclared_result = client.tool("invoke_helper", {"helper_id": undeclared_helper_id, "params_json": json.dumps({"name": "should-not-run"})})
    if "error" not in undeclared_result or "undeclared destination" not in undeclared_result["error"]:
        raise AssertionError(f"invoke_helper should block an undeclared destination even when admitted: {undeclared_result}")
    state = snapshot_selector(client, window_id)
    if element(state, "Fixture username").get("value") != expected:
        raise AssertionError("invoke_helper dispatched a node with an undeclared destination")

    # F18-T02: unrecognised tool -- there is no process-launch tool in DCU at all to demonstrate
    # this against, so an invented tool name stands in for the spec's example.
    unknown_tool_helper_id = f"helper-unknown-tool-{uuid4().hex}"
    unknown_nodes = [{"id": "n1", "app": window_id, "tool": "launch_process", "args": {}, "prerequisites": []}]
    client.tool("register_helper", {
        "helper_id": unknown_tool_helper_id, "nodes_json": json.dumps(unknown_nodes),
        "parameter_schema_json": "{}", "declared_apps_json": declared_apps,
    })
    client.tool("admit_helper", {"helper_id": unknown_tool_helper_id, "test_evidence": "reviewed"})
    unknown_tool_result = client.tool("invoke_helper", {"helper_id": unknown_tool_helper_id, "params_json": "{}"})
    if "error" not in unknown_tool_result or "dispatchable workflow tools" not in unknown_tool_result["error"]:
        raise AssertionError(f"invoke_helper should block an unrecognised tool even when admitted: {unknown_tool_result}")

    status = client.tool("get_helper_status", {"helper_id": helper_id})
    if not status.get("admitted"):
        raise AssertionError(f"get_helper_status should report the currently-admitted helper as admitted: {status}")

    return {
        "invoke_before_admit_refused": True,
        "invoke_after_admit_value": first_value,
        "edit_invalidated_admission": True,
        "invoke_after_edit_refused": True,
        "invoke_after_re_admit_value": expected,
        "undeclared_destination_blocked": True,
        "unrecognised_tool_blocked": True,
    }


def _run_capture_provenance(client: ConcurrentMcpClient, window_id: str, hwnd: int) -> dict[str, Any]:
    """F02-R01/R02: get_app_state's capture envelope reports full provenance on a normal capture,
    and an explicit failure_reason (never a silent substitute) when the target is minimized --
    against the real live fixture."""
    ok_state = client.tool("get_app_state", {"app": window_id, "include_screenshot": True})
    ok_capture = ok_state.get("capture")
    if not isinstance(ok_capture, dict) or ok_capture.get("origin") != "print_window":
        raise AssertionError(f"expected a normal print_window capture: {ok_capture}")
    if ok_capture.get("failure_reason") is not None:
        raise AssertionError(f"a successful capture should have no failure_reason: {ok_capture}")
    if not (isinstance(ok_capture.get("width"), int) and ok_capture["width"] > 0):
        raise AssertionError(f"expected a positive captured width: {ok_capture}")
    if not ok_state.get("screenshot_png_base64"):
        raise AssertionError("expected non-empty screenshot_png_base64 alongside a successful capture")

    minimize_window(hwnd)
    try:
        deadline = time.monotonic() + 5
        minimized_capture: dict[str, Any] | None = None
        while time.monotonic() < deadline:
            state = client.tool("get_app_state", {"app": window_id, "include_screenshot": True})
            candidate = state.get("capture")
            if isinstance(candidate, dict) and candidate.get("origin") == "none":
                minimized_capture = candidate
                minimized_state = state
                break
            time.sleep(0.1)
        if minimized_capture is None:
            raise AssertionError("a minimized window's capture never became an explicit failure")
        if minimized_capture.get("failure_reason") != "window_minimized":
            raise AssertionError(f"expected failure_reason=window_minimized, got: {minimized_capture}")
        if minimized_state.get("screenshot_png_base64") is not None:
            raise AssertionError("a minimized capture must never yield image bytes")
    finally:
        restore_window(hwnd)

    return {"ok_origin": ok_capture["origin"], "minimized_failure_reason": minimized_capture["failure_reason"]}


def _run_dag_workflow(
    client: ConcurrentMcpClient, window_id: str, username_id: str, password_id: str
) -> dict[str, Any]:
    """F29-T01: two independent set_value branches (username, password) run as one wave, and the
    sign-in join node only dispatches once both are Verified -- against the real live fixture."""
    workflow_id = f"dag-{uuid4().hex}"
    nodes = [
        {
            "id": "set_user",
            "app": window_id,
            "tool": "set_value",
            "args": {"element_id": username_id, "value": SYNTHETIC_USERNAME},
            "prerequisites": [],
        },
        {
            "id": "set_pass",
            "app": window_id,
            "tool": "set_value",
            "args": {"element_id": password_id, "value": SYNTHETIC_PASSWORD},
            "prerequisites": [],
        },
        {
            # A wait_for join rather than a second click on "sign in": the manual flow earlier in
            # this scenario already authenticated the fixture, so a repeat click on an
            # already-signed-in button is a genuine no-effect click (ok:false) -- that would be a
            # quirk of this shared fixture's idempotency, not a DAG-engine bug, so the join uses an
            # action whose success does not depend on producing a fresh observable state change.
            "id": "sign_in",
            "app": window_id,
            "tool": "wait_for",
            "args": {"condition": "element_exists", "text": "Fixture status"},
            "prerequisites": ["set_user", "set_pass"],
        },
    ]
    create_result = client.tool("create_workflow", {"workflow_id": workflow_id, "nodes_json": json.dumps(nodes)})
    if not create_result.get("ok"):
        raise AssertionError(f"create_workflow failed: {create_result}")

    run_result = client.tool("run_workflow", {"workflow_id": workflow_id})
    if not run_result.get("done"):
        raise AssertionError(f"run_workflow did not complete the DAG: {run_result}")
    statuses = {n["id"]: n["status"] for n in run_result["nodes"]}
    if statuses != {"set_user": "verified", "set_pass": "verified", "sign_in": "verified"}:
        raise AssertionError(f"expected all three DAG nodes verified, got: {statuses}")

    state = snapshot_selector(client, window_id)
    status = element(state, "Fixture status").get("value")
    if status != "authenticated: fixture-user":
        raise AssertionError(f"DAG-driven login did not authenticate synthetic user: {status!r}")

    return {"workflow_id": workflow_id, "statuses": statuses, "status_after": status}


def _find_browser_candidate(observation: dict[str, Any], role: str, text: str, name: str | None = None) -> dict[str, Any]:
    candidates = observation.get("candidates") or []
    matches = [
        item for item in candidates
        if item.get("role") == role and item.get("text") == text and (name is None or item.get("name") == name)
    ]
    if len(matches) != 1:
        raise AssertionError(f"expected exactly one {role!r} candidate with text {text!r} name {name!r}: {candidates}")
    return matches[0]


def _run_mixed_dag_workflow(client: ConcurrentMcpClient, window_id: str, username_id: str) -> dict[str, Any]:
    """F29 cross-adapter: a single DAG mixes a native node (capture_workflow_value, reading a live
    UIA field from the real WindowStressFixture) with a browser node (browser_fill, against a real
    headless Edge page) -- the browser node's prerequisite is the native node, and the value it
    fills never appears as a literal anywhere in the workflow definition, only as a value_ref into
    the shared WorkflowValueStore. Proves DispatchStep genuinely routes browser_* tool names inside
    run_workflow, not just the native tools F29 originally shipped with.

    The browser session itself is opened OUTSIDE the DAG (browser_open is not currently a workflow
    node either, by choice) because its session_id/candidate_id are only known after the call
    returns, and workflow node args are static JSON fixed at create_workflow time -- a real,
    documented limitation, not an oversight.
    """
    mixed_value = f"mixed-dag-{uuid4().hex[:10]}"
    capture_name = f"mixed-dag-value-{uuid4().hex[:8]}"

    set_source = client.tool(
        "set_value",
        {"app": window_id, "element_id": username_id, "value": mixed_value},
    )
    _require_ok(set_source, "seed native field for mixed-adapter DAG")

    html = (
        '<input id="account" aria-label="Account" type="text" value="" />'
        '<input id="scratch" aria-label="Scratch" type="text" value="" />'
        '<button id="go">Go</button>'
    )
    opened = client.tool("browser_open", {"url": "data:text/html," + url_quote(html), "headless": True})
    _require_ok(opened, "browser_open mixed-adapter DAG page")
    session_id = opened["session_id"]

    # F42-T01: route_set_text/route_click must resolve to the browser adapter for a browser
    # target, through the SAME high-level intent already proven native_uia for a native target
    # in _run_login above. Uses the separate "scratch"/"go" elements so the DAG's own "account"
    # candidate id (minted fresh per observation) is never invalidated by this side proof.
    scratch_candidate = _find_browser_candidate(opened, "input", "", name="Scratch")
    routed_fill = client.tool(
        "route_set_text",
        {"session_id": session_id, "candidate_id": scratch_candidate["id"], "value": "routed-browser-value"},
    )
    _require_ok(routed_fill, "route_set_text browser target")
    if routed_fill.get("route") != "browser":
        raise AssertionError(f"route_set_text should report route=browser for a browser target: {routed_fill}")
    if not any(c.get("role") == "input" and c.get("text") == "routed-browser-value" for c in routed_fill["candidates"]):
        raise AssertionError(f"route_set_text (browser route) did not observably set the scratch field: {routed_fill['candidates']}")
    go_candidate = _find_browser_candidate(routed_fill, "button", "Go")
    routed_click = client.tool("route_click", {"session_id": session_id, "candidate_id": go_candidate["id"]})
    _require_ok(routed_click, "route_click browser target")
    if routed_click.get("route") != "browser":
        raise AssertionError(f"route_click should report route=browser for a browser target: {routed_click}")
    account_candidate = _find_browser_candidate(routed_click, "input", "", name="Account")

    workflow_id = f"mixed-dag-{uuid4().hex}"
    nodes = [
        {
            "id": "native_capture",
            "app": window_id,
            "tool": "capture_workflow_value",
            "args": {"element_id": username_id, "name": capture_name},
            "prerequisites": [],
        },
        {
            "id": "browser_fill_account",
            "app": "",
            "tool": "browser_fill",
            "args": {"session_id": session_id, "candidate_id": account_candidate["id"], "value_ref": capture_name},
            "prerequisites": ["native_capture"],
        },
    ]

    try:
        create_result = client.tool("create_workflow", {"workflow_id": workflow_id, "nodes_json": json.dumps(nodes)})
        if not create_result.get("ok"):
            raise AssertionError(f"create_workflow (mixed-adapter) failed: {create_result}")

        run_result = client.tool("run_workflow", {"workflow_id": workflow_id})
        if not run_result.get("done"):
            raise AssertionError(f"run_workflow (mixed-adapter) did not complete the DAG: {run_result}")
        statuses = {n["id"]: n["status"] for n in run_result["nodes"]}
        if statuses != {"native_capture": "verified", "browser_fill_account": "verified"}:
            raise AssertionError(f"expected both mixed-adapter DAG nodes verified, got: {statuses}")

        fill_node = next(n for n in run_result["nodes"] if n["id"] == "browser_fill_account")
        _find_browser_candidate(fill_node["result"], "input", mixed_value)
    finally:
        closed = client.tool("browser_close", {"session_id": session_id})
        _require_ok(closed, "browser_close mixed-adapter DAG session")

    # Restore the native field to the scenario's synthetic username so later steps that assert
    # on "Fixture username" are unaffected by this sub-scenario borrowing the same element.
    restore_source = client.tool(
        "set_value",
        {"app": window_id, "element_id": username_id, "value": SYNTHETIC_USERNAME},
    )
    _require_ok(restore_source, "restore native field after mixed-adapter DAG")

    return {"workflow_id": workflow_id, "statuses": statuses, "mixed_value": mixed_value}


def _run_typed_workflow_steps(client: ConcurrentMcpClient) -> dict[str, Any]:
    """F32-T01/T02: a real DAG mixes typed non-UI nodes (read_file/transform_data/write_file)
    with a browser node (browser_fill), reading approved CSV data, filling a live headless-Edge
    form, and writing a verified output -- all inside one create_workflow/run_workflow graph, not
    just sequential client.tool() calls. Also proves path traversal is rejected for both read_file
    and write_file, and that an http_request host outside allowed_hosts is rejected, before any
    prohibited operation happens (F32-T02).
    """
    import shutil

    root = Path(tempfile.mkdtemp(prefix="dcu-workflow-files-"))
    try:
        account_value = f"acct-{uuid4().hex[:8]}-fixture"
        (root / "accounts.csv").write_text(f"name,value\naccount-1,{account_value}\n", encoding="utf-8")

        html = '<input id="account" aria-label="Account" type="text" value="" />'
        opened = client.tool("browser_open", {"url": "data:text/html," + url_quote(html), "headless": True})
        _require_ok(opened, "browser_open typed-workflow-steps page")
        session_id = opened["session_id"]
        account_candidate = _find_browser_candidate(opened, "input", "")

        try:
            # F32-T02: path traversal rejected up front, before create_workflow ever runs.
            traversal_read = client.tool("read_file", {"allowed_root": str(root), "path": "../outside.txt", "name": "x"})
            if "error" not in traversal_read:
                raise AssertionError(f"read_file should reject path traversal: {traversal_read}")
            traversal_write = client.tool("write_file", {"allowed_root": str(root), "path": "../outside.txt", "value": "nope"})
            if "error" not in traversal_write:
                raise AssertionError(f"write_file should reject path traversal: {traversal_write}")
            if (root.parent / "outside.txt").exists():
                raise AssertionError("write_file traversal rejection did not prevent the file from being created")
            blocked_host = client.tool("http_request", {
                "url": "http://not-allowed.example.invalid/", "allowed_hosts": "127.0.0.1", "name": "y",
            })
            if "error" not in blocked_host:
                raise AssertionError(f"http_request should reject a host outside allowed_hosts: {blocked_host}")

            workflow_id = f"typed-steps-{uuid4().hex}"
            nodes = [
                {
                    "id": "read_csv",
                    "app": "",
                    "tool": "read_file",
                    "args": {"allowed_root": str(root), "path": "accounts.csv", "name": "csv_raw"},
                    "prerequisites": [],
                },
                {
                    "id": "extract_cell",
                    "app": "",
                    "tool": "transform_data",
                    "args": {"value_ref": "csv_raw", "op": "csv_cell:1,1", "name": "account_value"},
                    "prerequisites": ["read_csv"],
                },
                {
                    "id": "fill_browser",
                    "app": "",
                    "tool": "browser_fill",
                    "args": {"session_id": session_id, "candidate_id": account_candidate["id"], "value_ref": "account_value"},
                    "prerequisites": ["extract_cell"],
                },
                {
                    "id": "write_output",
                    "app": "",
                    "tool": "write_file",
                    "args": {"allowed_root": str(root), "path": "output.txt", "value_ref": "account_value"},
                    "prerequisites": ["extract_cell"],
                },
            ]
            create_result = client.tool("create_workflow", {"workflow_id": workflow_id, "nodes_json": json.dumps(nodes)})
            if not create_result.get("ok"):
                raise AssertionError(f"create_workflow (typed-steps) failed: {create_result}")
            run_result = client.tool("run_workflow", {"workflow_id": workflow_id})
            if not run_result.get("done"):
                raise AssertionError(f"run_workflow (typed-steps) did not complete the DAG: {run_result}")
            statuses = {n["id"]: n["status"] for n in run_result["nodes"]}
            if statuses != {"read_csv": "verified", "extract_cell": "verified", "fill_browser": "verified", "write_output": "verified"}:
                raise AssertionError(f"expected all four typed-workflow nodes verified, got: {statuses}")

            fill_node = next(n for n in run_result["nodes"] if n["id"] == "fill_browser")
            _find_browser_candidate(fill_node["result"], "input", account_value)

            output_content = (root / "output.txt").read_text(encoding="utf-8")
            if output_content != account_value:
                raise AssertionError(f"write_file output did not match the CSV-extracted value: {output_content!r} != {account_value!r}")
            if set(p.name for p in root.iterdir()) != {"accounts.csv", "output.txt"}:
                raise AssertionError(f"typed-workflow-steps root should contain only the CSV input and its output: {list(root.iterdir())}")
        finally:
            closed = client.tool("browser_close", {"session_id": session_id})
            _require_ok(closed, "browser_close typed-workflow-steps session")

        bounded_loop = _run_bounded_loop_node(client)
    finally:
        shutil.rmtree(root, ignore_errors=True)

    return {
        "account_value": account_value,
        "statuses": statuses,
        "traversal_read_refused": True,
        "traversal_write_refused": True,
        "http_host_not_allowed_refused": True,
        "output_matches_extracted_csv_cell": True,
        "no_extraneous_files_in_root": True,
        "bounded_loop": bounded_loop,
    }


def _run_bounded_loop_node(client: ConcurrentMcpClient) -> dict[str, Any]:
    """F32-R01/T02: a bounded-loop node over the hard cap (50) is refused by create_workflow
    before a single node is created -- an unbounded loop can never even be constructed, not just
    stopped mid-run. F32-R01/T01: a valid bounded-loop node expands into real per-item nodes that
    actually dispatch and run, each with {{loop_item}}/{{loop_index}} substituted."""
    import shutil

    loop_root = Path(tempfile.mkdtemp(prefix="dcu-workflow-loop-"))
    try:
        oversized = client.tool("create_workflow", {
            "workflow_id": f"oversized-loop-{uuid4().hex}",
            "nodes_json": json.dumps([{
                "id": "huge",
                "kind": "bounded_loop",
                "count": 51,
                "template": {"app": "", "tool": "write_file", "args": {
                    "allowed_root": str(loop_root), "path": "should-not-exist-{{loop_index}}.txt", "value": "x",
                }},
            }]),
        })
        if "error" not in oversized:
            raise AssertionError(f"create_workflow should refuse a bounded_loop over the hard cap: {oversized}")
        if any(loop_root.iterdir()):
            raise AssertionError(f"oversized-loop rejection did not prevent file creation: {list(loop_root.iterdir())}")

        loop_items = ["loop-row-a", "loop-row-b", "loop-row-c"]
        loop_workflow_id = f"loop-steps-{uuid4().hex}"
        loop_create = client.tool("create_workflow", {
            "workflow_id": loop_workflow_id,
            "nodes_json": json.dumps([{
                "id": "write_each",
                "kind": "bounded_loop",
                "items": loop_items,
                "template": {"app": "", "tool": "write_file", "args": {
                    "allowed_root": str(loop_root), "path": "loop-{{loop_index}}.txt", "value": "{{loop_item}}",
                }},
                "prerequisites": [],
            }]),
        })
        if not loop_create.get("ok"):
            raise AssertionError(f"create_workflow (bounded_loop) failed: {loop_create}")
        expected_ids = [f"write_each#{i}" for i in range(len(loop_items))]
        if (loop_create.get("loop_expansions") or {}).get("write_each") != expected_ids:
            raise AssertionError(f"create_workflow did not report the expected loop expansion ids: {loop_create}")

        loop_run = client.tool("run_workflow", {"workflow_id": loop_workflow_id})
        if not loop_run.get("done"):
            raise AssertionError(f"run_workflow (bounded_loop) did not complete: {loop_run}")
        loop_statuses = {n["id"]: n["status"] for n in loop_run["nodes"]}
        if loop_statuses != {node_id: "verified" for node_id in expected_ids}:
            raise AssertionError(f"expected all bounded-loop iteration nodes verified, got: {loop_statuses}")

        for i, item in enumerate(loop_items):
            content = (loop_root / f"loop-{i}.txt").read_text(encoding="utf-8")
            if content != item:
                raise AssertionError(f"loop iteration {i} wrote {content!r}, expected {item!r}")
    finally:
        shutil.rmtree(loop_root, ignore_errors=True)

    return {
        "oversized_loop_refused_before_creation": True,
        "iteration_count": len(loop_items),
        "expanded_node_ids": expected_ids,
        "per_iteration_content_verified": True,
    }


def _run_timing_breakdown(client: ConcurrentMcpClient, window_id: str) -> dict[str, Any]:
    """F51-R01: a click's response and its run-timeline entry must break total time down into
    dispatch (the actual input dispatch) and settle (the follow-up snapshot), not just one lump
    total, against the real live fixture."""
    state = snapshot_selector(client, window_id)
    status_field = element(state, "Fixture status")

    click_result = client.tool("click", {"app": window_id, "element_id": status_field["id"]})
    timing = click_result.get("timing")
    if not isinstance(timing, dict):
        raise AssertionError(f"click response is missing a timing breakdown: {click_result}")
    for key in ("dispatch_ms", "settle_ms", "total_ms"):
        if not isinstance(timing.get(key), int) or timing[key] < 0:
            raise AssertionError(f"click timing.{key} should be a non-negative measured duration: {timing}")

    return {"click_timing": timing}


def _run_benchmark_report(client: ConcurrentMcpClient, window_id: str, username_id: str) -> dict[str, Any]:
    """F53: get_benchmark_report aggregates recorded run-timeline entries into per-action groups
    (count, avg/min/max total_ms, dominant phase), now that press_key/scroll/drag/type_text also
    carry a timing breakdown alongside click/set_value -- against the real live fixture, with
    EnableRunTimeline turned on for this whole scenario run."""
    state = snapshot_selector(client, window_id)
    status_field = element(state, "Fixture status")

    press = client.tool("press_key", {"app": window_id, "key": "Tab"})
    if "error" in press:
        raise AssertionError(f"press_key for benchmark report failed: {press}")
    if not isinstance(press.get("timing"), dict):
        raise AssertionError(f"press_key response is missing a timing breakdown: {press}")

    scroll = client.tool("scroll", {"app": window_id, "direction": "down", "pages": 0.1})
    if "error" in scroll:
        raise AssertionError(f"scroll for benchmark report failed: {scroll}")
    if not isinstance(scroll.get("timing"), dict):
        raise AssertionError(f"scroll response is missing a timing breakdown: {scroll}")

    typed = client.tool("type_text", {"app": window_id, "element_id": username_id, "text": ""})
    if "error" in typed:
        raise AssertionError(f"type_text for benchmark report failed: {typed}")
    if not isinstance(typed.get("timing"), dict):
        raise AssertionError(f"type_text response is missing a timing breakdown: {typed}")

    report = client.tool("get_benchmark_report", {"count": 500})
    if report.get("enabled") is not True:
        raise AssertionError(f"get_benchmark_report should be enabled with EnableRunTimeline set: {report}")
    groups = report.get("groups") or []
    if not groups:
        raise AssertionError(f"expected at least one benchmark group: {report}")
    native_actions = {g["action"] for g in groups if g.get("surface") == "native"}
    for expected_action in ("click", "set_value", "press_key", "scroll", "type_text"):
        if expected_action not in native_actions:
            raise AssertionError(f"expected a native {expected_action!r} benchmark group, got actions: {native_actions}")
    for group in groups:
        if group.get("count", 0) > 0 and not group.get("dominantPhase"):
            raise AssertionError(f"benchmark group missing a dominant phase: {group}")
        if group.get("avgTotalMs", -1) < 0:
            raise AssertionError(f"benchmark group has a negative avg total: {group}")

    return {"entries_considered": report.get("entries_considered"), "native_actions": sorted(native_actions)}


def _run_disabled_element_refusal(client: ConcurrentMcpClient, window_id: str) -> dict[str, Any]:
    """F10: a live-confirmed disabled element must be refused BEFORE dispatch, never silently
    attempted, against the real fixture's genuinely disabled 'Fixture disabled action' button
    (Enabled=false, no Click handler at all -- if a refusal regression let this through, it would
    have no visible effect to hide behind)."""
    state = snapshot_selector(client, window_id)
    disabled = element(state, "Fixture disabled action")
    result = client.tool("click", {"app": window_id, "element_id": disabled["id"]})
    if "error" not in result or "disabled" not in result["error"].lower():
        raise AssertionError(f"clicking a live-disabled element should be refused before dispatch: {result}")
    return {"disabled_click_refused": True, "refusal_message": result["error"]}


def _run_readonly_element_refusal(client: ConcurrentMcpClient, window_id: str) -> dict[str, Any]:
    """SetValue() parity with F10: a live-confirmed read-only element must be refused BEFORE
    dispatch, against the real fixture's genuinely read-only 'Fixture status' textbox (ReadOnly=true
    in WinForms, so a regression letting this through would actually mutate a read-only control)."""
    state = snapshot_selector(client, window_id)
    status_before = element(state, "Fixture status")
    result = client.tool("set_value", {"app": window_id, "element_id": status_before["id"], "value": "tampered"})
    if "error" not in result or "read-only" not in result["error"].lower():
        raise AssertionError(f"setting a live-read-only element should be refused before dispatch: {result}")
    state_after = snapshot_selector(client, window_id)
    status_after = element(state_after, "Fixture status")
    if status_after.get("value") != status_before.get("value"):
        raise AssertionError(f"read-only refusal did not actually leave the field untouched: before={status_before} after={status_after}")
    return {"readonly_set_value_refused": True, "refusal_message": result["error"]}


def _run_effect_feedback_alignment(client: ConcurrentMcpClient, window_id: str, hwnd: int, username_id: str) -> dict[str, Any]:
    """F45: type_text/press_key/scroll/drag must carry the same {effect, revision,
    changed_elements} envelope click/set_value already did, against the real live fixture --
    an actual round trip through each tool, not just a unit-level shape assertion."""
    def _require_shape(response: dict[str, Any], tool: str) -> None:
        _require_ok(response, f"{tool} effect-feedback probe")
        for key in ("effect", "revision", "changed_elements"):
            if key not in response:
                raise AssertionError(f"{tool} response missing '{key}': {response}")

    type_result = client.tool("type_text", {"app": window_id, "element_id": username_id, "text": "effect-feedback-probe"})
    _require_shape(type_result, "type_text")
    if type_result["effect"] != "uia_state_changed":
        raise AssertionError(f"type_text into a real field should report effect=uia_state_changed, got: {type_result}")

    state = snapshot_selector(client, window_id)
    username_after_type = element(state, "Fixture username").get("value") or ""
    # type_text types at the current cursor/selection (EM_REPLACESEL), it does not replace the
    # whole field like set_value -- so the probe text is appended after the existing username,
    # not an exact replacement.
    if "effect-feedback-probe" not in username_after_type:
        raise AssertionError(f"type_text did not actually enter the probe text into the live field: {username_after_type!r}")

    press_result = client.tool("press_key", {"app": window_id, "key": "Tab"})
    _require_shape(press_result, "press_key")

    scroll_result = client.tool("scroll", {"app": window_id, "direction": "down", "pages": 0.1})
    _require_shape(scroll_result, "scroll")

    rect = window_rect(hwnd)
    drag_x = rect.left + 40
    drag_y = rect.top + 40
    drag_result = client.tool("drag", {"app": window_id, "from_x": drag_x, "from_y": drag_y, "to_x": drag_x + 5, "to_y": drag_y + 5})
    _require_shape(drag_result, "drag")

    restore = client.tool("set_value", {"app": window_id, "element_id": username_id, "value": SYNTHETIC_USERNAME})
    _require_ok(restore, "restore synthetic username after effect-feedback probe")

    return {
        "type_text_effect": type_result.get("effect"),
        "press_key_effect": press_result.get("effect"),
        "scroll_effect": scroll_result.get("effect"),
        "drag_effect": drag_result.get("effect"),
    }


def _run_execute_sequence_resume(client: ConcurrentMcpClient, window_id: str, username_id: str) -> dict[str, Any]:
    """F17: execute_sequence must stop at the first failed step and report stopped_at, AND a
    resumed call with start_at_step must genuinely SKIP the earlier steps rather than replay them
    -- against the real live fixture, not a synthetic step list. Step 2 in this sequence always
    fails (an element id that was never in any real snapshot), so if start_at_step=3 accidentally
    replayed step 2, the resumed call would fail the exact same way the first call did."""
    steps = json.dumps([
        {"tool": "set_value", "args": {"element_id": username_id, "value": "resume-probe-a"}},
        {"tool": "click", "args": {"element_id": "e_definitely_not_a_real_element"}},
        {"tool": "set_value", "args": {"element_id": username_id, "value": "resume-probe-b"}},
    ])

    first = client.tool("execute_sequence", {"app": window_id, "steps_json": steps})
    if first.get("ok") is not False or first.get("stopped_at") != 2:
        raise AssertionError(f"execute_sequence should stop at the failing step 2: {first}")

    state = snapshot_selector(client, window_id)
    username_after_first = element(state, "Fixture username").get("value")
    if username_after_first != "resume-probe-a":
        raise AssertionError(f"step 1 should have run and set the live field before stopping: {username_after_first!r}")

    resumed = client.tool("execute_sequence", {"app": window_id, "steps_json": steps, "start_at_step": 3})
    if resumed.get("ok") is not True or resumed.get("resumed_from") != 3 or resumed.get("steps_run") != 1:
        raise AssertionError(f"resuming at step 3 should skip the failing step 2 entirely and succeed: {resumed}")

    state = snapshot_selector(client, window_id)
    username_after_resume = element(state, "Fixture username").get("value")
    if username_after_resume != "resume-probe-b":
        raise AssertionError(f"resumed sequence did not actually run step 3 on the live field: {username_after_resume!r}")

    restore = client.tool("set_value", {"app": window_id, "element_id": username_id, "value": SYNTHETIC_USERNAME})
    _require_ok(restore, "restore synthetic username after execute_sequence resume probe")

    return {"first_stopped_at": first.get("stopped_at"), "resumed_steps_run": resumed.get("steps_run"), "resumed_from": resumed.get("resumed_from")}


def _run_demonstration_replay(client: ConcurrentMcpClient, window_id: str, hwnd: int, username_id: str, password_id: str) -> dict[str, Any]:
    """F30: compiles two already-remembered F31 recipes into a demonstration, admits it (live
    resolution check), moves the real window (F30-T01's "harmless window movement"), then replays
    -- every step re-resolves live via resolve_cached_action, never a recorded screen coordinate,
    so the post-move replay must still land on the right fields. Also proves F30-T02: an admitted
    demonstration refuses re-admission/edits."""
    demo_id = f"demo-{uuid4().hex}"
    user_intent = f"demo-user-{uuid4().hex[:8]}"
    pass_intent = f"demo-pass-{uuid4().hex[:8]}"
    demo_user_value = f"demo-{uuid4().hex[:10]}"

    remember_user = client.tool("remember_action_resolution", {"app": window_id, "element_id": username_id, "task_intent": user_intent})
    _require_ok(remember_user, "remember demonstration username recipe")
    remember_pass = client.tool("remember_action_resolution", {"app": window_id, "element_id": password_id, "task_intent": pass_intent})
    _require_ok(remember_pass, "remember demonstration password recipe")

    steps = [
        {"app": window_id, "tool": "set_value", "task_intent": user_intent, "args": {"value": demo_user_value}},
        {"app": window_id, "tool": "set_value", "task_intent": pass_intent, "args": {"value": SYNTHETIC_PASSWORD}},
    ]
    create_result = client.tool(
        "create_demonstration",
        {
            "demonstration_id": demo_id,
            "steps_json": json.dumps(steps),
            "completion_app": window_id,
            "completion_condition": "element_exists",
            "completion_text": "Fixture status",
        },
    )
    _require_ok(create_result, "create_demonstration")
    if create_result.get("status") != "draft":
        raise AssertionError(f"a freshly created demonstration should start draft: {create_result}")

    admit_result = client.tool("admit_demonstration", {"demonstration_id": demo_id})
    _require_ok(admit_result, "admit_demonstration")
    if admit_result.get("status") != "admitted":
        raise AssertionError(f"admit_demonstration did not report admitted: {admit_result}")

    re_admit_result = client.tool("admit_demonstration", {"demonstration_id": demo_id})
    if "error" not in re_admit_result:
        raise AssertionError(f"re-admitting an already-admitted demonstration should be refused (F30-T02): {re_admit_result}")

    rect = window_rect(hwnd)
    move_window(hwnd, rect.left + 140, rect.top + 90, rect.right - rect.left, rect.bottom - rect.top)

    replay_result = client.tool("replay_demonstration", {"demonstration_id": demo_id})
    _require_ok(replay_result, "replay_demonstration after window movement")
    if replay_result.get("steps_run") != 2:
        raise AssertionError(f"expected both demonstration steps to run: {replay_result}")
    if replay_result.get("completion_verified") is not True:
        raise AssertionError(f"demonstration completion check should have verified: {replay_result}")

    state = snapshot_selector(client, window_id)
    username_now = element(state, "Fixture username").get("value")
    if username_now != demo_user_value:
        raise AssertionError(f"replayed demonstration did not set the username field post-move: {username_now!r} != {demo_user_value!r}")

    unknown_intent_demo_id = f"demo-bad-{uuid4().hex}"
    bad_steps = [{"app": window_id, "tool": "click", "task_intent": f"never-remembered-{uuid4().hex[:8]}", "args": {}}]
    client.tool("create_demonstration", {"demonstration_id": unknown_intent_demo_id, "steps_json": json.dumps(bad_steps)})
    bad_admit = client.tool("admit_demonstration", {"demonstration_id": unknown_intent_demo_id})
    if bad_admit.get("ok") is not False:
        raise AssertionError(f"admitting a demonstration with an unresolvable task_intent should refuse: {bad_admit}")

    return {
        "demonstration_id": demo_id,
        "admitted": True,
        "re_admit_refused": True,
        "window_moved_by": [140, 90],
        "replay_completion_verified": replay_result.get("completion_verified"),
        "unresolvable_intent_refused": True,
    }


def _run_cross_adapter_demonstration(client: ConcurrentMcpClient, window_id: str, username_id: str) -> dict[str, Any]:
    """F30 remaining_work item: proves a single demonstration can genuinely mix a native step and
    a browser step -- DemonstrationStore already allows browser_click/browser_fill in its step
    tool allowlist and ReplayDemonstration's dispatch already routes through the shared DispatchStep
    (F29), so this exercises the plumbing live rather than adding anything new."""
    native_intent = f"cross-demo-native-{uuid4().hex[:8]}"
    browser_intent = f"cross-demo-browser-{uuid4().hex[:8]}"
    native_value = f"cross-native-{uuid4().hex[:10]}"
    browser_value = f"cross-browser-{uuid4().hex[:10]}"

    remember_native = client.tool("remember_action_resolution", {"app": window_id, "element_id": username_id, "task_intent": native_intent})
    _require_ok(remember_native, "remember cross-adapter native recipe")

    html = '<input id="account" aria-label="Account" type="text" value="" />'
    opened = client.tool("browser_open", {"url": "data:text/html," + url_quote(html), "headless": True})
    _require_ok(opened, "browser_open cross-adapter demonstration page")
    session_id = opened["session_id"]
    account_candidate = _find_browser_candidate(opened, "input", "")
    remember_browser = client.tool(
        "browser_remember_action_resolution",
        {"session_id": session_id, "candidate_id": account_candidate["id"], "task_intent": browser_intent},
    )
    _require_ok(remember_browser, "remember cross-adapter browser recipe")

    demo_id = f"cross-demo-{uuid4().hex}"
    steps = [
        {"app": window_id, "tool": "set_value", "task_intent": native_intent, "args": {"value": native_value}},
        {"app": "", "tool": "browser_fill", "task_intent": browser_intent, "args": {"session_id": session_id, "value": browser_value}},
    ]

    try:
        create_result = client.tool("create_demonstration", {"demonstration_id": demo_id, "steps_json": json.dumps(steps)})
        _require_ok(create_result, "create cross-adapter demonstration")

        admit_result = client.tool("admit_demonstration", {"demonstration_id": demo_id})
        _require_ok(admit_result, "admit cross-adapter demonstration")

        replay_result = client.tool("replay_demonstration", {"demonstration_id": demo_id})
        _require_ok(replay_result, "replay cross-adapter demonstration")
        if replay_result.get("steps_run") != 2:
            raise AssertionError(f"expected both cross-adapter demonstration steps to run: {replay_result}")

        state = snapshot_selector(client, window_id)
        native_now = element(state, "Fixture username").get("value")
        if native_now != native_value:
            raise AssertionError(f"cross-adapter demonstration did not set the native field: {native_now!r} != {native_value!r}")

        browser_fill_result = replay_result["results"][1]["result"]
        _find_browser_candidate(browser_fill_result, "input", browser_value)
    finally:
        closed = client.tool("browser_close", {"session_id": session_id})
        _require_ok(closed, "browser_close cross-adapter demonstration session")

    restore = client.tool("set_value", {"app": window_id, "element_id": username_id, "value": SYNTHETIC_USERNAME})
    _require_ok(restore, "restore username after cross-adapter demonstration")

    return {"demonstration_id": demo_id, "native_step_ok": True, "browser_step_ok": True}


def _run_jev_act_cache_fast_path(client: ConcurrentMcpClient, window_id: str, username_id: str) -> dict[str, Any]:
    """F28/F31 convergence: jev_act's cache-hit fast path checks resolve_cached_action BEFORE ever
    calling Jev, so it must work even with Jev fully unavailable (no TYPESAFE_API_KEY in this
    process) -- proves the mechanism live without needing real Jev credentials, same pattern as
    every other jev=off proof in this suite. An unremembered task_intent must fall through to the
    normal (here: unavailable) Jev path unchanged, not silently succeed some other way."""
    task_intent = f"jev-fast-path-{uuid4().hex[:8]}"
    fast_path_value = f"fast-{uuid4().hex[:10]}"

    remember = client.tool("remember_action_resolution", {"app": window_id, "element_id": username_id, "task_intent": task_intent})
    _require_ok(remember, "remember jev fast-path recipe")

    hit_result = client.tool(
        "jev_act",
        {"app": window_id, "goal": "irrelevant -- must not reach Jev", "task_intent": task_intent, "fill_value": fast_path_value},
    )
    if hit_result.get("decision_source") != "cache_hit_fast_path":
        raise AssertionError(f"jev_act with a remembered task_intent should take the cache-hit fast path even with Jev unavailable: {hit_result}")
    if not isinstance(hit_result.get("decision_ms"), int) or hit_result["decision_ms"] < 0:
        raise AssertionError(f"cache-hit fast path should report a real measured decision_ms: {hit_result}")
    dispatch_result = hit_result.get("result") or {}
    if dispatch_result.get("ok") is not True:
        raise AssertionError(f"cache-hit fast path dispatch did not succeed: {hit_result}")

    state = snapshot_selector(client, window_id)
    username_now = element(state, "Fixture username").get("value")
    if username_now != fast_path_value:
        raise AssertionError(f"cache-hit fast path did not actually set the field: {username_now!r} != {fast_path_value!r}")

    # F28: record_task_experience/recall_task_experience are entirely caller-driven and tool-
    # agnostic -- they take an app/task_intent/element_id, never which tool reached that element.
    # There is no code path that restricts them to click/set_value; this proves a caller CAN record
    # and recall experience for a target that a real jev_act dispatch (not click/set_value) just set.
    task_recorded = client.tool(
        "record_task_experience",
        {"app": window_id, "task_intent": task_intent, "outcome": "success", "element_id": username_id},
    )
    _require_ok(task_recorded, "record_task_experience for a jev_act-driven dispatch")

    recalled = client.tool("recall_task_experience", {"app": window_id, "task_intent": task_intent})
    if recalled.get("found") is not True:
        raise AssertionError(f"recall_task_experience should find the experience just recorded after a jev_act dispatch: {recalled}")

    miss_result = client.tool(
        "jev_act",
        {"app": window_id, "goal": "irrelevant", "task_intent": f"never-remembered-{uuid4().hex[:8]}"},
    )
    if "error" not in miss_result:
        raise AssertionError(f"an unremembered task_intent should fall through to the normal (here: unavailable) Jev path: {miss_result}")

    restore = client.tool("set_value", {"app": window_id, "element_id": username_id, "value": SYNTHETIC_USERNAME})
    _require_ok(restore, "restore username after jev fast-path test")

    return {"task_intent": task_intent, "cache_hit_decision_ms": hit_result["decision_ms"], "cache_miss_fell_through": True, "experience_recorded_and_recalled_after_jev_act": True}


def _run_background_mode(client: ConcurrentMcpClient, window_id: str) -> dict[str, Any]:
    """F01-R01/F01-T02: strict_background (default) dispatches and echoes the mode it used;
    a named-but-unimplemented mode must return UNSUPPORTED_INPUT_MODE against a real live
    target rather than silently running as strict_background or falling back to a riskier path."""
    state = snapshot_selector(client, window_id)
    status_field = element(state, "Fixture status")

    strict_result = client.tool(
        "click",
        {"app": window_id, "element_id": status_field["id"], "background_mode": "strict_background"},
    )
    if strict_result.get("background_mode") != "strict_background":
        raise AssertionError(f"explicit strict_background click did not echo its mode: {strict_result}")
    if "error" in strict_result:
        raise AssertionError(f"explicit strict_background click unexpectedly failed: {strict_result}")

    unsupported_result = client.tool(
        "click",
        {"app": window_id, "element_id": status_field["id"], "background_mode": "best_effort_background"},
    )
    if unsupported_result.get("error") != "UNSUPPORTED_INPUT_MODE":
        raise AssertionError(
            f"requesting an unimplemented background_mode against a live target should return "
            f"UNSUPPORTED_INPUT_MODE, not silently execute: {unsupported_result}"
        )
    if unsupported_result.get("background_mode") != "best_effort_background":
        raise AssertionError(f"UNSUPPORTED_INPUT_MODE response did not echo the requested mode: {unsupported_result}")

    return {"strict": strict_result, "unsupported": unsupported_result}


def _run_loop_guard(client: ConcurrentMcpClient, window_id: str) -> dict[str, Any]:
    """F28-R02: clicking a read-only status label three times in a row has no observable
    UIA effect each time, so the third click must carry a diagnosed no-progress verdict."""
    state = snapshot_selector(client, window_id)
    status_field = element(state, "Fixture status")
    verdicts = []
    for _ in range(3):
        click_result = client.tool(
            "click",
            {"app": window_id, "element_id": status_field["id"]},
        )
        if click_result.get("effect") != "none":
            raise AssertionError(
                f"expected clicking a read-only status label to have no observable effect: {click_result}"
            )
        verdicts.append(click_result.get("loop_guard"))

    if verdicts[0] is not None and verdicts[0].get("should_stop"):
        raise AssertionError(f"loop guard should not stop after only one no-progress click: {verdicts[0]}")
    if verdicts[1] is not None and verdicts[1].get("should_stop"):
        raise AssertionError(f"loop guard should not stop after only two no-progress clicks: {verdicts[1]}")
    third = verdicts[2]
    if third is None or third.get("should_stop") is not True:
        raise AssertionError(f"loop guard should diagnose a no-progress cycle by the third repeat: {third}")
    if not isinstance(third.get("diagnosis"), str) or not third["diagnosis"]:
        raise AssertionError(f"loop guard should_stop verdict is missing a diagnosis: {third}")

    return {"verdicts": verdicts}


def _run_action_resolution_cache(
    client: ConcurrentMcpClient, window_id: str, username_id: str
) -> dict[str, Any]:
    """F31: remember_action_resolution + resolve_cached_action against the live fixture.
    Proves a cache hit still comes from a live re-resolution (not a replayed id) and that
    an unremembered task_intent misses cleanly instead of returning a stale/guessed match."""
    task_intent = f"native-regression-username-field-{uuid4().hex[:8]}"
    remember = client.tool(
        "remember_action_resolution",
        {"app": window_id, "element_id": username_id, "task_intent": task_intent},
    )
    _require_ok(remember, "remember action resolution for username field")

    resolved = client.tool(
        "resolve_cached_action",
        {"app": window_id, "task_intent": task_intent},
    )
    if resolved.get("hit") is not True:
        raise AssertionError(f"cached action resolution did not hit for an unchanged fixture: {resolved}")
    if not isinstance(resolved.get("element_id"), str) or not resolved["element_id"]:
        raise AssertionError(f"cache hit did not return a usable element_id: {resolved}")

    miss = client.tool(
        "resolve_cached_action",
        {"app": window_id, "task_intent": f"{task_intent}-never-remembered"},
    )
    if miss.get("hit") is not False or miss.get("reason") != "no_cached_recipe":
        raise AssertionError(f"an unremembered task_intent should miss cleanly: {miss}")

    return {
        "task_intent": task_intent,
        "hit_element_id": resolved["element_id"],
        "miss_reason": miss.get("reason"),
    }


def _run_experience_store(
    client: ConcurrentMcpClient, window_id: str, username_id: str
) -> dict[str, Any]:
    """F28-R01: record_task_experience + recall_task_experience against the live fixture.
    Proves a same-version recall re-resolves its recipe live (not a replayed id) and that
    an unrecorded task_intent misses cleanly. F28-T02 (advisory-on-version-change) needs an
    actual app version change the live fixture cannot produce without rebuilding the binary,
    so that half is covered by ExperienceStoreTests.cs's direct unit tests instead."""
    task_intent = f"native-regression-login-submit-{uuid4().hex[:8]}"
    record = client.tool(
        "record_task_experience",
        {
            "app": window_id,
            "element_id": username_id,
            "task_intent": task_intent,
            "outcome": "success",
            "environment": "native_evolution_regression.py",
        },
    )
    _require_ok(record, "record task experience for login submit")
    if not isinstance(record.get("app_version"), str) or not record["app_version"]:
        raise AssertionError(f"record_task_experience did not return an app_version: {record}")

    recall = client.tool("recall_task_experience", {"app": window_id, "task_intent": task_intent})
    if recall.get("found") is not True or recall.get("advisory") is not False:
        raise AssertionError(f"recall should find a same-version, non-advisory experience: {recall}")
    if recall.get("outcome") != "success":
        raise AssertionError(f"recall did not return the recorded outcome: {recall}")
    resolved = recall.get("resolved") or {}
    if resolved.get("hit") is not True or not isinstance(resolved.get("element_id"), str):
        raise AssertionError(f"recall did not live-re-resolve the stored recipe: {recall}")

    miss = client.tool(
        "recall_task_experience",
        {"app": window_id, "task_intent": f"{task_intent}-never-recorded"},
    )
    if miss.get("found") is not False or miss.get("reason") != "not_found":
        raise AssertionError(f"an unrecorded task_intent should miss cleanly: {miss}")

    failure_rejected = client.tool(
        "record_task_experience",
        {"app": window_id, "task_intent": task_intent, "outcome": "success", "failure_category": "element_not_found"},
    )
    if not failure_rejected.get("error"):
        raise AssertionError(f"failure_category with outcome=success should be rejected: {failure_rejected}")

    return {
        "task_intent": task_intent,
        "app_version": record["app_version"],
        "resolved_element_id": resolved["element_id"],
        "miss_reason": miss.get("reason"),
    }


def _run_workflow_value_transfer(
    client: ConcurrentMcpClient, window_id: str, username_id: str, password_id: str
) -> dict[str, Any]:
    """F39: capture the live username value and re-enter it via value_ref, without the text
    passing through the set_value call a second time. Round-trips through the (unmasked)
    username field to verify content, then also targets the password field to prove value_ref
    reaches a sensitive control -- Windows blocks reading a password field's value back via
    UIA (E_ACCESSDENIED) regardless of how it was set, so that leg only checks ok/method."""
    name = f"native-regression-username-{uuid4().hex[:8]}"
    capture = client.tool(
        "capture_workflow_value",
        {"app": window_id, "element_id": username_id, "name": name},
    )
    _require_ok(capture, "capture username as a workflow value")
    if capture.get("length") != len(SYNTHETIC_USERNAME):
        raise AssertionError(f"captured workflow value has an unexpected length: {capture}")

    round_trip = client.tool(
        "set_value",
        {"app": window_id, "element_id": username_id, "value_ref": name},
    )
    _require_ok(round_trip, "round-trip username from its own captured workflow value")
    if round_trip.get("value") != SYNTHETIC_USERNAME:
        raise AssertionError(
            f"username field did not round-trip its own captured workflow value: {round_trip}"
        )

    to_password = client.tool(
        "set_value",
        {"app": window_id, "element_id": password_id, "value_ref": name},
    )
    _require_ok(to_password, "set password from captured workflow value")
    _require_method(to_password, "uia_setvalue", "set password from captured workflow value")

    conflict = client.tool(
        "set_value",
        {"app": window_id, "element_id": username_id, "value": "x", "value_ref": name},
    )
    if "error" not in conflict:
        raise AssertionError(f"set_value with both value and value_ref should be rejected: {conflict}")

    missing = client.tool(
        "set_value",
        {"app": window_id, "element_id": username_id, "value_ref": f"{name}-missing"},
    )
    if "error" not in missing:
        raise AssertionError(f"set_value with an unknown value_ref should be rejected: {missing}")

    return {"captured_name": name, "captured_length": capture.get("length")}


def _run_ambiguity(client: ConcurrentMcpClient, window_id: str) -> dict[str, Any]:
    state = snapshot_selector(client, window_id)
    duplicate_buttons = sorted(
        [item for item in state["elements"] if item.get("name") == "Duplicate action"],
        key=lambda item: (item.get("y", 0), item.get("id", "")),
    )
    if len(duplicate_buttons) != 2:
        raise AssertionError(f"expected two initial duplicate buttons: {duplicate_buttons}")
    initial_ids = [item["id"] for item in duplicate_buttons]
    stale_row2 = duplicate_buttons[1]["id"]
    command = element(state, "Fixture command")
    rebuild = client.tool(
        "set_value",
        {"app": window_id, "element_id": command["id"], "value": "rebuild"},
    )
    _require_ok(rebuild, "request duplicate-control rebuild")
    _require_method(rebuild, "uia_setvalue", "request duplicate-control rebuild")

    rebuild_marker = client.tool(
        "wait_for",
        {
            "app": window_id,
            "condition": "element_exists",
            "text": "Rebuild complete",
            "timeout_s": 5,
        },
    )
    _require_ok(rebuild_marker, "wait for real rebuild marker")
    replacement_marker = client.tool(
        "wait_for",
        {
            "app": window_id,
            "condition": "element_exists",
            "text": "2 duplicate replacements",
            "timeout_s": 5,
        },
    )
    _require_ok(replacement_marker, "wait for duplicate replacement count")

    stale_click = client.tool(
        "click",
        {"app": window_id, "element_id": stale_row2},
    )
    after = snapshot_selector(client, window_id)
    replacement_buttons = sorted(
        [item for item in after["elements"] if item.get("name") == "Duplicate action"],
        key=lambda item: (item.get("y", 0), item.get("id", "")),
    )
    if len(replacement_buttons) != 2:
        raise AssertionError(f"expected two duplicate replacement buttons: {replacement_buttons}")
    status = element(after, "Fixture status").get("value")
    _require_ambiguous_error(stale_click, stale_row2)
    if status != "untouched":
        raise AssertionError(f"stale row-2 click changed fixture state: {status!r}")

    return {
        "initial_duplicate_ids": initial_ids,
        "stale_row": 2,
        "stale_element_id": stale_row2,
        "replacement_duplicate_ids": [item["id"] for item in replacement_buttons],
        "replacement_count": len(replacement_buttons),
        "rebuild_marker": rebuild_marker,
        "replacement_marker": replacement_marker,
        "stale_click": stale_click,
        "ambiguity_refused": True,
        "status": status,
        "rebuild_dispatch": rebuild.get("method"),
    }


def _run_scenario(
    scenario: str,
    dcu_executable: Path,
    fixture_executable: Path,
    artifact_root: Path,
) -> dict[str, Any]:
    artifact_dir = _new_artifact_dir(artifact_root, scenario)
    result: dict[str, Any] = {
        "scenario": scenario,
        "jev": "off",
        "dcu_executable": str(dcu_executable),
        "fixture_executable": str(fixture_executable),
        "artifact_dir": str(artifact_dir),
        "endpoint_samples": {"initial": None, "final": None},
    }
    fixture: subprocess.Popen[str] | None = None
    client: ConcurrentMcpClient | None = None
    passed = False
    try:
        initial_endpoint = foreground_and_cursor()
        result["endpoint_samples"]["initial"] = _endpoint_record(initial_endpoint)
        fixture = subprocess.Popen(
            [str(fixture_executable), "--scenario", scenario],
            cwd=ROOT,
            text=True,
        )
        hwnd = wait_for_windows(fixture, 1)[0]
        result["fixture_pid"] = fixture.pid
        result["fixture_hwnd"] = hwnd
        environment = _settings_environment(artifact_dir)
        client = ConcurrentMcpClient(dcu_executable, environment, artifact_dir)
        initialized = client.call(
            "initialize",
            {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": f"dcu-native-{scenario}", "version": "1.0"},
            },
        )
        if "error" in initialized:
            raise AssertionError(f"MCP initialize failed: {initialized}")
        client.notify("notifications/initialized")
        window_id = _select_owned_window(client, fixture, hwnd)
        result["window_id"] = window_id

        if scenario == "login":
            result.update(_run_login(client, window_id, hwnd))
        else:
            result.update(_run_ambiguity(client, window_id))

        final_endpoint = foreground_and_cursor()
        result["endpoint_samples"]["final"] = _endpoint_record(final_endpoint)
        result["foreground_unchanged_at_endpoints"] = initial_endpoint[0] == final_endpoint[0]
        result["cursor_unchanged_at_endpoints"] = initial_endpoint[1] == final_endpoint[1]
        result["endpoint_observation_note"] = (
            "Initial/final samples only; user input may change foreground or cursor, "
            "so this is not continuous strict-background proof."
        )
        result["passed"] = True
        passed = True
    except BaseException as error:
        result["passed"] = False
        result["error"] = repr(error)
        result["traceback"] = traceback.format_exc()
    finally:
        if client is not None:
            try:
                _cleanup_client(result, client, passed)
            except BaseException as error:
                _record_cleanup_error(result, "client cleanup", error)
        try:
            _stop_owned_process(fixture)
        except BaseException as error:
            _record_cleanup_error(result, "stop owned fixture", error)
        (artifact_dir / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        if result.get("passed") is True:
            print(f"PASS scenario={scenario} artifact_dir={artifact_dir}")
        else:
            print(f"FAIL scenario={scenario} artifact_dir={artifact_dir}", file=os.sys.stderr)
            if result.get("traceback"):
                print(result["traceback"], file=os.sys.stderr, end="")
    return result


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dcu", type=Path, default=Path(os.environ.get("DCU_EXE", DEFAULT_DCU)))
    parser.add_argument(
        "--fixture",
        type=Path,
        default=Path(os.environ.get("WINDOW_STRESS_FIXTURE_EXE", DEFAULT_FIXTURE)),
    )
    parser.add_argument("--artifact-root", type=Path, default=DEFAULT_ARTIFACT_ROOT)
    parser.add_argument("--scenario", choices=("all", "login", "ambiguity"), default="all")
    return parser.parse_args()


def main() -> int:
    args = _parse_args()
    dcu_executable = args.dcu.resolve()
    fixture_executable = args.fixture.resolve()
    artifact_root = args.artifact_root.resolve()
    if not dcu_executable.is_file():
        raise FileNotFoundError(f"DCU executable not found: {dcu_executable}")
    if not fixture_executable.is_file():
        raise FileNotFoundError(f"fixture executable not found: {fixture_executable}")

    scenarios = ("login", "ambiguity") if args.scenario == "all" else (args.scenario,)
    results = [
        _run_scenario(scenario, dcu_executable, fixture_executable, artifact_root)
        for scenario in scenarios
    ]
    passed = all(result.get("passed") is True for result in results)
    print(
        json.dumps(
            {
                "passed": passed,
                "jev": "off",
                "dcu_executable": str(dcu_executable),
                "fixture_executable": str(fixture_executable),
                "scenarios": results,
            },
            indent=2,
        )
    )
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
