#!/usr/bin/env python3
"""Offline, repeatable comparison on operator-supplied files. No live capture/input/network.
Run with the isolated CUDA Python environment used by tools/ui_candidate_detect.py.
Unavailable methods are recorded, never substituted by assistant-generated boxes.
"""
from __future__ import annotations
import argparse
import base64
from contextlib import nullcontext
import copy
from decimal import Decimal, ROUND_HALF_UP
import hashlib
import html
import importlib.util
import io
import json
import os
from pathlib import Path
import statistics
import sys
import time

import numpy as np
from PIL import Image, ImageDraw, ImageFont
import torch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
sys.path.insert(0, str(ROOT / 'tests'))
from ui_candidate_detect import load_model, decode
from multi_window_stress_regression import ConcurrentMcpClient

class QuietClient(ConcurrentMcpClient):
    def _record(self, direction, message):
        # Do not persist screenshot bytes in the stdio transcript.
        value = copy.deepcopy(message)
        for block in value.get('result', {}).get('content', []):
            if block.get('type') == 'image':
                block['data'] = '<image omitted>'
        super()._record(direction, value)


def stats(values):
    return {'median_ms': round(statistics.median(values), 3),
            'p95_ms': round(float(np.percentile(values, 95)), 3),
            'min_ms': round(min(values), 3), 'max_ms': round(max(values), 3),
            'samples_ms': [round(v, 3) for v in values], 'repeats': len(values)}


def box_item(item):
    b = item.get('bbox', {})
    return {'key': item.get('key', ''), 'label': item.get('label', ''), 'bbox': b,
            'score': item.get('score', item.get('proposal_score')), 'role': item.get('role', 'region')}


def yolo_input(image, size, device):
    w, h = image.size
    scale = min(size/w, size/h)
    resized = image.resize((round(w*scale), round(h*scale)), Image.Resampling.BILINEAR)
    canvas = Image.new('RGB', (size, size), (114,114,114))
    canvas.paste(resized, (0,0))
    # Copy uint8 pixels first; float conversion/normalization runs on the GPU.
    x = torch.from_numpy(np.asarray(canvas).copy()).to(device).permute(2,0,1).unsqueeze(0).float().div_(255)
    return x, scale


def yolo_trial(model, image, size, amp, repeats):
    device = torch.device('cuda')
    def once():
        torch.cuda.synchronize()
        start = time.perf_counter_ns()
        x, scale = yolo_input(image, size, device)
        torch.cuda.synchronize()
        t1 = time.perf_counter_ns()
        with torch.inference_mode(), (torch.autocast('cuda', dtype=torch.float16) if amp else nullcontext()):
            heads = model(x)
        torch.cuda.synchronize()
        t2 = time.perf_counter_ns()
        boxes, _ = decode(heads, scale, *image.size, 0.05, 0.45)
        torch.cuda.synchronize()
        end = time.perf_counter_ns()
        for box in boxes:
            b = box['bbox']
            b['x'] = min(max(0,b['x']),image.width-1)
            b['y'] = min(max(0,b['y']),image.height-1)
            b['width'] = min(b['width'],image.width-b['x'])
            b['height'] = min(b['height'],image.height-b['y'])
        return boxes, [(t1-start)/1e6,(t2-t1)/1e6,(end-t2)/1e6,(end-start)/1e6]
    # Includes decode/NMS warmup, not just the forward pass.
    first_boxes, cold = once()
    for _ in range(4): once()
    timings, boxes = [], first_boxes
    torch.cuda.reset_peak_memory_stats()
    for _ in range(repeats):
        boxes, elapsed = once()
        timings.append(elapsed)
    return {'status':'tested', 'boxes':[box_item(b) for b in boxes],
            'latency':stats([v[3] for v in timings]),
            'stages':{'preprocess_ms':round(statistics.median([v[0] for v in timings]),3),
                      'forward_ms':round(statistics.median([v[1] for v in timings]),3),
                      'decode_nms_ms':round(statistics.median([v[2] for v in timings]),3)},
            'cold_first_pipeline_ms':round(cold[3],3),
            'peak_allocated_gpu_mib':round(torch.cuda.max_memory_allocated()/1048576,1),
            'note':f'Input {size} square, {"FP16 autocast" if amp else "FP32"}; confidence .05, NMS IoU .45. No semantic naming. Warm total includes resize/transfer/forward/decode/NMS; excludes file decode, startup and artwork.'}


def local_methods(images, output, repeats):
    exe = ROOT/'src/ShadowUse/bin/Release/net10.0-windows10.0.22621.0/dcu.exe'
    if (exe.parent/'settings.json').exists():
        raise RuntimeError('Refusing executable-local settings override')
    appdata = output/'isolated-appdata'
    settings_dir = appdata/'shadow-use'
    settings_dir.mkdir(parents=True,exist_ok=True)
    (settings_dir/'settings.json').write_text(json.dumps({'GameMode':False,'JevMode':'off','RemoteTransportUrl':None,'ShowVirtualCursor':False}),encoding='utf-8')
    environment = os.environ.copy()
    environment['APPDATA']=str(appdata)
    c = QuietClient(exe,environment,output)
    results={}
    try:
        init=c.call('initialize',{'protocolVersion':'2025-03-26','capabilities':{},'clientInfo':{'name':'offline-ui-benchmark','version':'1'}})
        if 'error' in init: raise RuntimeError(init['error'])
        c.notify('notifications/initialized')
        for key,path in images.items():
            results[key]={}
            for method in ('edge','ocr','combined','bloons-template'):
                runs=[]; wall=[]; first=None
                for i in range(repeats+1):
                    start=time.perf_counter_ns()
                    response=c.call('tools/call',{'name':'ui_candidates_detect_image','arguments':{'image_path':str(path),'method':method,'max_candidates':255}})
                    elapsed=(time.perf_counter_ns()-start)/1e6
                    if 'error' in response: raise RuntimeError(response['error'])
                    payload=json.loads(response['result']['content'][0]['text'])
                    if not payload.get('ok'): raise RuntimeError(payload)
                    if i==0: first=payload['perception_ms']
                    else: runs.append(payload['perception_ms']);wall.append(elapsed)
                results[key][method]={'name':{'edge':'Fast edge components (C#)','ocr':'Windows.Media.Ocr','combined':'Windows OCR + edge components','bloons-template':'Existing calibrated BTD2 layout'}[method],
                    'status':'tested','boxes':[box_item(b) for b in payload['candidates']],
                    'latency':stats(runs),'tool_roundtrip':stats(wall),'cold_first_pipeline_ms':first,
                    'coverage_claim':payload.get('candidate_set_complete',False),
                    'note':payload.get('candidate_set_issue') or 'Scoped BTD2 anchors passed; not general discovery.',
                    'observed_text':payload.get('observed_text',[])}
    finally:
        try:c.close()
        except Exception:c.abort()
    return results


def overlap(a,b):
    ax,ay,aw,ah=(a[k] for k in ('x','y','width','height'))
    bx,by,bw,bh=(b[k] for k in ('x','y','width','height'))
    inter=max(0,min(ax+aw,bx+bw)-max(ax,bx))*max(0,min(ay+ah,by+bh)-max(ay,by))
    return inter/max(1,aw*ah+bw*bh-inter)


def merge_ocr(detector, ocr):
    result=copy.deepcopy(detector)
    for region in result:
        b=region['bbox']
        texts=[]
        for line in ocr:
            t=line['bbox'];cx=t['x']+t['width']/2;cy=t['y']+t['height']/2
            if b['x']<=cx<=b['x']+b['width'] and b['y']<=cy<=b['y']+b['height']:
                texts.append(line['label'])
        if texts:region['label']=' / '.join(texts)[:240]
    for line in ocr:
        if not any(overlap(line['bbox'],r['bbox'])>.4 for r in result):result.append(copy.deepcopy(line))
    return result


def render(image, record, output):
    draw=ImageDraw.Draw(image)
    fontpath=r'C:\Windows\Fonts\arial.ttf'
    font=ImageFont.truetype(fontpath,max(12,image.width//125))
    for n,item in enumerate(record.get('boxes',[]),1):
        b=item['bbox'];x,y=b['x'],b['y'];w,h=b['width'],b['height']
        if w<=0 or h<=0:continue
        draw.rectangle((x,y,x+w-1,y+h-1),outline='#ffca49',width=max(2,image.width//700))
        label=str(n)
        if item.get('score') is not None:label+=f" ({item['score']:.2f})"
        rect=draw.textbbox((x,max(0,y-22)),label,font=font)
        draw.rectangle(rect,fill='#121a28');draw.text((x,max(0,y-22)),label,font=font,fill='white')
    # Add a separate header above the image, never cover screenshot pixels.
    header=Image.new('RGB',(image.width,74),'#101827')
    hd=ImageDraw.Draw(header)
    hd.text((12,8),record['name'],font=font,fill='white')
    if record.get('latency'):
        latency=record['latency']
        scope='full cloud HTTP' if record.get('model') else 'warm local'
        summary=f"{len(record.get('boxes',[]))} proposals | {scope} median {latency['median_ms']:.2f} ms | {record['status']}"
    else:summary=record['status']+' — no measured latency'
    hd.text((12,38),summary,font=font,fill='#a7c9ee')
    canvas=Image.new('RGB',(image.width,image.height+74))
    canvas.paste(header);canvas.paste(image,(0,74));canvas.save(output)


def report_html(report, output):
    sections=[]; rows=[]
    review_path=output/'comparison-ratings.json'
    review=json.loads(review_path.read_text(encoding='utf-8')) if review_path.exists() else {}
    fingerprint=hashlib.sha256(json.dumps(report,sort_keys=True,separators=(',',':')).encode()).hexdigest()
    if review and review.get('report_sha256')!=fingerprint:
        review={'scope':'Saved ratings were not applied because the benchmark data changed; a new review is required.'}
    ratings={}; overall={}
    for method in report['method_names']:
        reviewed=review.get('methods',{}).get(method,{})
        ratings[method]={}
        for imagekey,data in report['images'].items():
            record=data['methods'].get(method,{})
            assessment=reviewed.get(imagekey)
            if assessment is None and review.get('methods') and record.get('model') and record.get('runs_completed',0)>0 and record.get('valid_runs')==0:
                assessment={'overall':0.0,'note':'Attempted request produced no accepted inventory. Zero rates usable-map delivery under this request/validation contract; visual recognition capability is unassessed.'}
            ratings[method][imagekey]=assessment or {'overall':None,'note':'Not rated: no reviewed output for this image. Untested results are not scored as failures.'}
        values=[r['overall'] for r in ratings[method].values() if r['overall'] is not None]
        if len(values)==len(report['images']) and reviewed.get('kind')!='helper':
            overall[method]=float((sum(Decimal(str(v)) for v in values)/len(values)).quantize(Decimal('0.1'),rounding=ROUND_HALF_UP))
        else:overall[method]=None
    methods=sorted(report['method_names'].items(),key=lambda item: -(overall[item[0]] if overall[item[0]] is not None else -1))
    for method,method_name in methods:
        cards=[]
        group_score=f'{overall[method]:.1f}/10' if overall[method] is not None else 'Not rated'
        if review.get('methods',{}).get(method,{}).get('kind')=='helper':group_score='Helper only — not ranked as discovery'
        for imagekey,data in report['images'].items():
            record=data['methods'].get(method,{'name':method_name,'status':'not run','note':'No result recorded for this image/method.'})
            name=html.escape(record['name']);note=html.escape(record.get('note',''))
            assessment=ratings[method][imagekey]
            image_score=f"{assessment['overall']:.1f}/10" if assessment['overall'] is not None else 'Not rated'
            dimensions=' · '.join(f'{label}: {assessment[key]:.1f}/10' for key,label in (('localization','Coverage/localization'),('noise_control','Noise control'),('descriptions','Label usefulness')) if key in assessment)
            rating_detail=f'<p class="rating">Output quality: <strong>{image_score}</strong> — manual assessment</p>'
            rating_note=f'<p>{html.escape(assessment["note"])}</p>'
            if dimensions:rating_note=f'<p>{dimensions}</p>'+rating_note
            if record.get('latency'):
                l=record['latency'];lat=f"{l['median_ms']:.2f} ms";p95=f"{l['p95_ms']:.2f} ms"
                if record['status']=='composed estimate':lat+=' (sum estimate)';p95+=' (sum, not p95)' 
            else:lat=p95='Not measured'
            reference=record.get('btd_card_reference')
            refcount=f"{reference['matched']}/16" if reference else '—'
            sample_count=record.get('latency',{}).get('repeats')
            if sample_count==1:p95='Not meaningful (n=1)'
            rows.append(f"<tr><td>{imagekey}</td><td>{name}</td><td>{image_score}</td><td>{group_score}</td><td>{html.escape(record['status'])}</td><td>{len(record.get('boxes',[])) if 'boxes' in record else '—'}</td><td>{refcount}</td><td>{lat}</td><td>{p95}</td></tr>")
            extra=''
            if reference:extra+=f"<p>BTD character-card reference match: <strong>{refcount}</strong>, one-to-one IoU ≥ 0.5. Missing: {html.escape(', '.join(reference['missed']) or 'none')}. Reference is benchmark-only, not provided to models.</p>"
            if record.get('card_coverage_by_run'):extra+=f"<p>Character-card matches across valid runs: {[x['matched'] for x in record['card_coverage_by_run']]} / 16. Candidate counts: {record.get('candidate_counts_by_run')}.</p>"
            if record.get('model'):
                extra+=f"<p>Model ID: {html.escape(record['model'])}. Thinking requested: {html.escape(record['reasoning_effort'])}. Valid responses: {record['valid_runs']}/{record['runs_requested']}. Providers returned: {html.escape(', '.join(record['providers']))}. Timings are complete cloud requests, not TTFT.</p>"
                if record.get('failed_response_http_ms'):extra+=f"<p>Failed/rejected response timings (not usable-map latency): {record['failed_response_http_ms']} ms.</p>"
                reasoning=[trial.get('usage',{}).get('completion_tokens_details',{}).get('reasoning_tokens') for trial in record.get('trials',[])]
                if any(value is not None for value in reasoning):extra+=f"<p>API-reported reasoning tokens per recorded run: {reasoning}.</p>"
            if record.get('stages'):extra+=f"<p>Stages: {html.escape(json.dumps(record['stages']))}. Peak allocated GPU: {record['peak_allocated_gpu_mib']} MiB.</p>"
            if record.get('tool_roundtrip'):extra+=f"<p>Tool round-trip incl. file decode, annotation, serialization: {record['tool_roundtrip']['median_ms']:.2f} ms median. Detection-only number above excludes those costs.</p>"
            if record.get('parallel_budget_ms') is not None:extra+=f"<p>Ideal parallel budget: {record['parallel_budget_ms']:.2f} ms, <strong>not measured parallel execution</strong>.</p>"
            if record.get('overlay'):
                imagefile=output/record['overlay']
                with Image.open(imagefile) as preview:
                    preview.thumbnail((1600,1600))
                    buffer=io.BytesIO();preview.convert('RGB').save(buffer,format='JPEG',quality=85)
                    uri='data:image/jpeg;base64,'+base64.b64encode(buffer.getvalue()).decode()
                visual=f'<img src="{uri}" alt="{name} numbered bounding boxes"><p>Embedded preview · <a href="{html.escape(record["overlay"])}" target="_blank">Open full-resolution PNG</a></p>'
            else:
                state='NOT RUN' if record['status'].startswith('not run') else 'REQUEST FAILED / NO VALID INVENTORY' if record.get('model') else 'NO RECORDED OVERLAY'
                visual=f'<div class="unavailable">{state} — no fabricated boxes or inference timing</div>'
            table=''
            if record.get('boxes'):
                items=[]
                for n,item in enumerate(record['boxes'],1):items.append(f"<tr><td>{n}</td><td>{html.escape(item.get('label',''))}</td><td>{html.escape(str(item['bbox']))}</td></tr>")
                table='<details><summary>Labels and original-image bounding boxes</summary><table><thead><tr><th>ID</th><th>Label</th><th>x, y, width, height</th></tr></thead><tbody>'+''.join(items)+'</tbody></table></details>'
            cards.append(f'<article data-method="{html.escape(method)}" data-image="{html.escape(imagekey)}"><h3>{html.escape(data["title"])} — {data["dimensions"][0]} × {data["dimensions"][1]}</h3><p class="source">{html.escape(data["source"])}</p>{rating_detail}<p class="metric">Median: {lat} · p95: {p95} · {html.escape(record["status"])}</p>{visual}{rating_note}<p>{note}</p>{extra}{table}</article>')
        sections.append(f'<section id="method-{html.escape(method)}" data-method="{html.escape(method)}" data-rating="{overall[method] if overall[method] is not None else ""}"><h2>{html.escape(method_name)} <span class="rating">— Overall: {group_score}</span></h2><div class="grid">'+''.join(cards)+'</div></section>')
    options=''.join(f'<option value="{html.escape(k)}">{html.escape(v)}</option>' for k,v in methods)
    document='''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>UI perception comparison — local tools and OpenRouter</title><style>
:root{color-scheme:dark}*{box-sizing:border-box}body{overflow-wrap:anywhere;font:16px/1.5 system-ui;margin:0;background:#0b1220;color:#e9effa}header,main{max-width:1720px;margin:auto;padding:24px}header{background:#111e32}h1{margin:0}a{color:#8bc6ff}.banner{border-left:4px solid #ffc75a;background:#201e1a;padding:16px}.grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:18px}article{border:1px solid #31415a;border-radius:12px;padding:16px;background:#131e30;overflow:hidden}article img{width:100%;height:auto;display:block}article h3{margin:0}.metric{color:#75deb9;font-weight:650}.rating{color:#ffd075}.source{overflow-wrap:anywhere;color:#aebdd2}table{border-collapse:collapse;width:100%;font-size:14px}td,th{text-align:left;padding:8px;border-bottom:1px solid #334157}th{background:#1b2a42}details{margin:12px 0;max-height:400px;overflow:auto}button,select{max-width:100%;font:inherit;padding:8px;background:#21324f;color:white;border:1px solid #53637b;border-radius:7px}.unavailable{padding:38px;background:#20212a;border:1px dashed #63616d}.controls{position:sticky;top:0;background:#0b1220ee;padding:12px;z-index:1}section{margin:30px 0}li{margin:6px 0}@media(max-width:950px){.grid{grid-template-columns:1fr}}
</style></head><body><header><h1>Screenshot → interaction-map comparison</h1><p>Actual tool outputs, measured on the supplied BTD2 and Anvil images. No assistant-labelled boxes.</p><p><a href="#comparisons">Compare paired overlays by method</a></p><p class="banner">Supplied still-image test: local tools plus explicitly authorized OpenRouter cloud trials. Boxes are visual proposals, not proof of clickability, enabled state or exhaustive recall. Lower latency and more boxes do not imply higher accuracy.</p></header><main>'''
    document+=f'<h2>Test conditions</h2><p>{html.escape(report["hardware"])}</p><ul><li>YOLO: 4 full-pipeline warmups after first run, then {report["repeats"]} synchronized runs; resident model. Local methods: first call excluded, {report["repeats"]} repeats per image/method.</li><li>Latency scope: warm pixels-to-boxes unless stated otherwise. No capture, model loading, disk decode, overlay rendering, MCP transport, embeddings, or Jev included in the YOLO total.</li><li>Model: {html.escape(report["weights_path"])}; SHA-256 {report["weights_sha256"]}. Microsoft detector MIT; installed PyTorch/TorchVision versions: {html.escape(report["torch_version"])} / {html.escape(report["torchvision_version"])}.</li><li>BTD has a separate manual reference for its 16 character-card rectangles, not supplied to models. No exhaustive Anvil interaction ground truth or verified hero-name labels exist. Two screenshots are a smoke comparison, not a general accuracy benchmark.</li></ul>'
    if report.get('openrouter_test'):
        cloud=report['openrouter_test']
        document+=f'<h2>OpenRouter cloud trial status</h2><p class="banner">{html.escape(cloud.get("state", "Cloud trials recorded"))}. Persisted responses: {cloud.get("responses_persisted", "see raw trials")}. API-reported cost for saved responses: ${cloud.get("reported_cost_usd_for_persisted_responses", 0):.6f}. {html.escape(cloud.get("note", ""))}</p><p>Cloud timings include upload, network, provider queue and complete JSON generation. Thinking settings appear in each model name. Failed HTTP requests and truncated/invalid inventories are not plotted as successful results. Provider data_collection=deny was requested; this is a routing privacy filter, not an independent audit of provider practices.</p>'
    if report.get('openrouter_test'):
        pair=report['openrouter_test'].get('gemini_current_pair')
        if pair:
            total=pair.get('two_call_total_usd')
            costs=f"BTD: ${pair['btd_cost_usd']:.8f}; new Anvil call: ${pair['anvil_redo_cost_usd']:.8f}; both: ${float(total):.8f} ({float(total)*100:.4f} cents)." if total is not None else 'At least one call has no API-reported cost.'
            document+=f'<h2>Gemini two-image call cost</h2><p><strong>{costs}</strong> Earlier failed Anvil call(s): ${float(pair["earlier_anvil_reported_cost_usd"]):.8f}, separate from the two-call total.</p><p>{html.escape(pair["note"])}</p>'
        winner=report['images']['btd']['methods'].get('or-gemini31-lite-minimal-norm',{})
        if winner.get('latency'):
            matched=winner.get('btd_card_reference',{}).get('matched',0)
            anvil=report['images']['anvil']['methods'].get('or-gemini31-lite-minimal-norm',{})
            anvil_result=f"The separately requested Anvil redo returned a valid-format inventory with {len(anvil.get('boxes',[]))} proposals in {anvil['latency']['median_ms']/1000:.2f} s; localization and completeness still need review." if anvil.get('latency') else 'Anvil failed JSON validation and produced no accepted inventory.'
            document+=f'<h2>Cloud result to inspect first</h2><p><strong>Gemini 3.1 Flash Lite, minimal thinking, declared normalized coordinates:</strong> BTD returned {len(winner.get("boxes",[]))} proposals in {winner["latency"]["median_ms"]/1000:.2f} s; {matched}/16 reference character-card boxes matched. This is one sample, not a reliability estimate. {anvil_result} The initial normalized trial stopped at the earlier Anvil failure; low-thinking and other normalized rows were not sent.</p><p>Initial original-pixel/strict-schema trials are preserved as separate failure modes, not silently rescored. The normalized trials use a smaller output contract with six-word labels, so their latency is not solely an effect of model size/thinking. <a href="https://ai.google.dev/gemini-api/docs/image-understanding#object-detection">Google documents the normalized 0–1000 box convention.</a></p>'
    document+=f'<h2>Output-quality ratings and ranking</h2><p class="banner">{html.escape(review.get("scope", "No manual ratings are available for this report."))}</p><p>{html.escape(review.get("formula", "Untested or unreviewed results are not rated."))}</p><p>Groups and the timing table are sorted best-rated first. Ratings are for these saved outputs, not model capability or production reliability. A 21-box count alone does not establish 21 correct targets. Original measurements and failed attempts are preserved; new calls are recorded separately. <a href="comparison-ratings.json">Rating data and rationale</a>.</p>'
    document+='<h2>Findings from these two screenshots</h2><ul><li>YOLO finds the BTD Cancel/Ready buttons and multiple hero cards, but the 1280px output misses some card/arrow targets and also includes broad background/container proposals. The 22-box count does not mean all 22 useful targets were found.</li><li>On Anvil, YOLO finds hotbar/chat-toolbar regions and text-like areas, including player names and chat messages. Whether those are clickable is not verified. Confidence is a detector score, not clickability probability.</li><li>Low-cost edge proposals include scene/decorative geometry. Windows OCR labels visible text, not textless icons. Neither is a general interactive-region detector.</li><li>YOLO + full-frame Windows OCR is not a 40–150 ms solution on this host: measured parallel tool-path costs are shown separately. Detector-only and cached-map timings exclude that OCR/tool overhead.</li><li>DOM/UIA/DXGI cannot be benchmarked from a still image. Local embeddings/captioning/GUI VLM routes remain unavailable. OpenRouter trials below are real cloud calls; failed/invalid responses are explicitly identified.</li></ul><h2>Measured timings</h2><div style="overflow:auto"><table><thead><tr><th>Image</th><th>Method</th><th>Image quality /10</th><th>Paired overall /10</th><th>Status</th><th>Boxes</th><th>BTD cards matched</th><th>Median ms (scope varies)</th><th>p95</th></tr></thead><tbody>'+''.join(rows)+'</tbody></table></div>'
    document+='<h2 id="comparisons">Paired overlays by method — best-rated first</h2><div class="controls"><label>Show method: <select id="method"><option value="all">All methods</option>'+options+'</select></label> · BTD2 left · Anvil right (stacked on narrow screens)</div>'+''.join(sections)
    document+='''<h2>What this comparison can and cannot decide</h2><ul><li>Cached-map timing tests repeated identical pixels with a seeded map and rejects the other screenshot. It is not new detection, tracking, scale invariance or skin recognition.</li><li>The fusion result actually merges local detector and OCR outputs. No DINOv2/SigLIP embedding model or verified visual-label database was tested.</li><li>DOM/UIA cannot be reconstructed from a PNG. DXGI capture requires a live surface, so it has no latency result in this still-file comparison.</li><li>Full local captioning and GUI VLM methods require additional checkpoints/runtime. The separately authorized OpenRouter comparison sends the source images to cloud providers; no provider claim is treated as proof of coverage.</li><li>Single-class YOLO proposes regions only. Without a verified label source, names stay unknown. Jev must not treat these as verified actions.</li></ul><h2>Sources</h2><p><a href="https://openrouter.ai/docs/guides/best-practices/reasoning-tokens">OpenRouter thinking controls</a> · <a href="https://openrouter.ai/docs/guides/routing/provider-selection">OpenRouter routing/privacy filters</a> · <a href="https://openrouter.ai/api/v1/models">Live model metadata</a>. Cloud usage/provider and per-run responses are stored locally in openrouter-runs.</p><p><a href="https://huggingface.co/microsoft/OmniParser-v2.0/blob/main/README.md">Microsoft model card and detector-only decoding</a> · <a href="https://huggingface.co/microsoft/OmniParser-v2.0/blob/main/icon_detect_v3/LICENSE">Detector MIT license</a>. Published A100/4090 and validation metrics are not measurements on this RTX 3080 or these screenshots.</p><p>Raw results and per-run samples: comparison.json alongside this standalone HTML. Image previews are embedded (max width 1600); full-resolution PNG overlays are alongside the HTML. This report makes no network requests.</p></main><script>document.getElementById('method').addEventListener('change',e=>document.querySelectorAll('section[data-method]').forEach(group=>group.hidden=e.target.value!=='all'&&group.dataset.method!==e.target.value));</script></body></html>'''
    (output/'comparison.html').write_text(document,encoding='utf-8')


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--btd',type=Path,required=True);parser.add_argument('--anvil',type=Path,required=True)
    parser.add_argument('--weights',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--repeats',type=int,default=7)
    args=parser.parse_args()
    args.output.mkdir(parents=True,exist_ok=False)
    paths={'btd':args.btd,'anvil':args.anvil}
    images={key:Image.open(path).convert('RGB') for key,path in paths.items()}
    torch.set_num_threads(2)
    torch.backends.cudnn.benchmark=True
    import torchvision
    report={'repeats':args.repeats,'hardware':torch.cuda.get_device_name(0)+'; GPU timings synchronized; concurrent host/GPU workloads not controlled.',
            'torch_version':torch.__version__,'torchvision_version':torchvision.__version__,
            'weights_path':str(args.weights),'weights_sha256':hashlib.sha256(args.weights.read_bytes()).hexdigest(),'images':{},'method_names':{}}
    local=local_methods(paths,args.output,args.repeats)
    load_start=time.perf_counter_ns();model=load_model(args.weights,torch.device('cuda'));torch.cuda.synchronize()
    report['model_load_ms']=(time.perf_counter_ns()-load_start)/1e6
    for key,image in images.items():
        methods=local[key]
        for size,amp in ((640,False),(960,False),(1280,False),(1280,True)):
            method=f'yolo-{size}-{ "fp16" if amp else "fp32"}'
            name=f'OmniParser YOLOv9-E {size}px {"FP16" if amp else "FP32"}'
            try:record=yolo_trial(model,image,size,amp,args.repeats)
            except Exception as error:record={'status':'failed','note':f'{type(error).__name__}: {error}'}
            record['name']=name;methods[method]=record
            print(key,method,record.get('latency',{}),flush=True)
        best=methods['yolo-1280-fp16'] if methods['yolo-1280-fp16']['status']=='tested' else methods['yolo-1280-fp32']
        if best['status']=='tested':
            start=time.perf_counter_ns();merged=merge_ocr(best['boxes'],methods['ocr']['boxes']);merge_ms=(time.perf_counter_ns()-start)/1e6
            serial=best['latency']['median_ms']+methods['ocr']['latency']['median_ms']+merge_ms
            # A composed cost estimate, not a falsely claimed measured end-to-end run.
            methods['yolo-ocr']={'name':'YOLOv9-E + Windows OCR fusion (no embeddings)','status':'composed estimate',
                'boxes':merged,'latency':{'median_ms':round(serial,3),'p95_ms':round(best['latency']['p95_ms']+methods['ocr']['latency']['p95_ms']+merge_ms,3)},
                'parallel_budget_ms':max(best['latency']['median_ms'],methods['ocr']['latency']['median_ms'])+merge_ms,
                'note':'Actual detector/OCR boxes merged locally; latency is sum of independent measured stages + measured merge, not an end-to-end or parallel measurement. No embeddings/captioning.'}
            cache={};times=[];thumbnail=image.resize((64,36)).tobytes();cache[(image.size,thumbnail)]=best['boxes']
            for _ in range(30):
                start=time.perf_counter_ns();cached=cache.get((image.size,image.resize((64,36)).tobytes()));times.append((time.perf_counter_ns()-start)/1e6)
                assert cached is not None
            other=images['anvil' if key=='btd' else 'btd']
            assert cache.get((other.size,other.resize((64,36)).tobytes())) is None
            methods['cached-map']={'name':'Seeded exact-thumbnail interaction-map cache','status':'tested repeated same image',
                'boxes':copy.deepcopy(best['boxes']),'latency':stats(times),
                'note':'Cache populated from YOLO output, then checked on identical pixels only. Different supplied image rejected. This saves discovery only for an unchanged known image; not template matching on unseen layouts.'}
        missing={
            'uia':('DOM/UIA/accessibility','Not applicable: PNG files contain no live DOM/UIA tree. No synthetic tree substituted.'),
            'dxgi':('DXGI capture','Not applicable: supplied files already captured. No live-game capture performed or timed.'),
            'embeddings':('YOLO + OCR + DINOv2/SigLIP memory','Not run: embedding checkpoint and verified reference-label database absent. Embeddings do not independently find boxes or establish identity.'),
            'captioning':('Full OmniParser + Florence captions','Not run: caption checkpoint and OCR/caption dependencies unavailable. Detector-only runtime does not provide the full pipeline.'),
            'gui-vlm':('Small GUI VLM','Not run: no verified image-capable local GUI VLM loaded. No cloud call or assistant substitute.'),
            'agent-vision':('Full agent screenshot analysis','Excluded by user: no assistant-derived boxes or latency presented as a detector result.')}
        for method,(name,note) in missing.items():methods[method]={'name':name,'status':'not run','note':note}
        for method,record in methods.items():
            report['method_names'][method]=record['name']
            if 'boxes' in record:
                for item in record['boxes']:
                    b=item['bbox']
                    assert b['x']>=0 and b['y']>=0 and b['width']>0 and b['height']>0
                    assert b['x']+b['width']<=image.width and b['y']+b['height']<=image.height
                record['overlay']=f'{key}-{method}.png'
                render(image.copy(),record,args.output/record['overlay'])
        report['images'][key]={'title':'Bloons TD Battles 2' if key=='btd' else 'Anvil Empires','source':str(paths[key]),'dimensions':list(image.size),'methods':methods}
    (args.output/'comparison.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    report_html(report,args.output)
    print('REPORT',args.output/'comparison.html',flush=True)

if __name__=='__main__':main()
