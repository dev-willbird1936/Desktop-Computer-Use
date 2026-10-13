#!/usr/bin/env python3
"""One explicitly requested Anvil rerun; no retries, fallback, input or Jev."""
import json
from pathlib import Path
import time
import urllib.error
import urllib.request
from ui_openrouter_compare import API, NoRedirect, key_from_config, payload_for
from ui_openrouter_normalized_compare import CONFIGS, FOLDER, INPUTS, parse_normalized


def main():
    config = CONFIGS[0]
    original = json.loads((FOLDER / 'openrouter-normalized' / f"anvil-{config['key']}.json").read_text(encoding='utf-8'))
    payload, frame, width, height = payload_for(config, INPUTS['anvil'])
    if frame not in original['prompt']:
        raise RuntimeError('Source image changed; refusing to transmit a different frame')
    prompt = original['prompt'] + '\nReturn exactly one well-formed JSON object. Each candidate contains only label and box_2d. No trailing fields or data after the root object. Check commas and closing brackets before finishing.'
    payload['response_format'] = {'type': 'json_object'}
    payload['messages'][1]['content'][0]['text'] = prompt
    out = FOLDER / 'openrouter-anvil-redo-r1'
    out.mkdir(exist_ok=False)  # Existing artifact refuses a duplicate paid run.
    key = key_from_config()
    opener = urllib.request.build_opener(NoRedirect())
    request = urllib.request.Request(API, data=json.dumps(payload).encode(), method='POST', headers={
        'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json',
        'X-OpenRouter-Title': 'DCU user-requested one-call Anvil redo'})
    trial = {'config': config, 'image': 'anvil', 'repeat': 2, 'frame_id': frame,
             'request_mode': 'json_object; explicit one-object syntax reminder',
             'coordinate_space': config['coordinate_space'], 'prompt': prompt,
             'mapping': 'Declared normalized yxyx to original pixels; floor left/top, ceil right/bottom',
             'maximum_posts': 1, 'provider_policy': payload['provider'],
             'user_authorization': 'redo anvil one with gemini, then tell me cost of noth gemini calls'}
    started = time.perf_counter_ns()
    try:
        with opener.open(request, timeout=120) as response:
            raw = json.load(response)
        trial.update(http_ms=round((time.perf_counter_ns() - started) / 1e6, 3),
                     provider=raw.get('provider'), model_returned=raw.get('model'),
                     usage=raw.get('usage', {}), response=raw)
        try:
            boxes = parse_normalized(raw, frame, width, height, config['coordinate_space'])
            trial.update(status='valid', boxes=boxes, candidate_count=len(boxes), coverage_asserted=False,
                         coverage_issue='Host treats coverage, clickability and identity as unverified.')
        except (ValueError, KeyError, TypeError) as error:
            trial.update(status='validation failed', error=str(error))
    except urllib.error.HTTPError as error:
        try:
            detail = json.loads(error.read()).get('error', {})
            message = detail.get('message', 'Provider error').replace(key, '<redacted>')[:300]
        except Exception:
            message = 'Could not parse provider error'
        trial.update(status='HTTP failure', http_status=error.code, error=message,
                     http_ms=round((time.perf_counter_ns() - started) / 1e6, 3))
    except Exception as error:
        trial.update(status='request failure', error=type(error).__name__,
                     http_ms=round((time.perf_counter_ns() - started) / 1e6, 3))
    artifact = out / f"anvil-{config['key']}-r2.json"
    artifact.write_text(json.dumps(trial, indent=2), encoding='utf-8')
    btd = json.loads((FOLDER / 'openrouter-normalized' / f"btd-{config['key']}.json").read_text(encoding='utf-8'))
    print('ANVIL', trial['status'], 'count', trial.get('candidate_count'), 'ms', trial.get('http_ms'), flush=True)
    print('BTD_COST_USD', btd.get('usage', {}).get('cost'), 'NEW_ANVIL_COST_USD', trial.get('usage', {}).get('cost'), flush=True)
    print('ARTIFACT', artifact, 'No retries or fallback performed.', flush=True)


if __name__ == '__main__':
    main()
