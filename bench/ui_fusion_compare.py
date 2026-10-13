#!/usr/bin/env python3
"""Augment an existing offline report with measured parallel YOLO/OCR tool execution."""
from concurrent.futures import ThreadPoolExecutor
import copy
import json
import os
from pathlib import Path
import sys
import time

import torch
from PIL import Image
from ui_perception_compare import QuietClient, ROOT, box_item, decode, load_model, merge_ocr, render, report_html, stats, yolo_input


def main():
    folder=Path(sys.argv[1])
    report=json.loads((folder/'comparison.json').read_text(encoding='utf-8'))
    logs=folder/'parallel-fusion';logs.mkdir(exist_ok=False)
    exe=ROOT/'src/ShadowUse/bin/Release/net10.0-windows10.0.22621.0/dcu.exe'
    if (exe.parent/'settings.json').exists():raise RuntimeError('Refusing executable-local settings override')
    env=os.environ.copy();env['APPDATA']=str(folder/'isolated-appdata')
    torch.set_num_threads(2);torch.backends.cudnn.benchmark=True
    model=load_model(Path(report['weights_path']),torch.device('cuda'))
    client=QuietClient(exe,env,logs)
    try:
        init=client.call('initialize',{'protocolVersion':'2025-03-26','capabilities':{},'clientInfo':{'name':'offline-fusion-benchmark','version':'1'}})
        if 'error' in init:raise RuntimeError(init)
        client.notify('notifications/initialized')
        with ThreadPoolExecutor(max_workers=1) as executor:
            for key,data in report['images'].items():
                image=Image.open(data['source']).convert('RGB')
                def detect():
                    x,scale=yolo_input(image,1280,torch.device('cuda'))
                    with torch.inference_mode(),torch.autocast('cuda',dtype=torch.float16):heads=model(x)
                    boxes,_=decode(heads,scale,*image.size,.05,.45)
                    torch.cuda.synchronize()
                    return [box_item(b) for b in boxes]
                for _ in range(5):detect()
                times=[];ocr_times=[]
                for run in range(report['repeats']+1):
                    torch.cuda.synchronize();started=time.perf_counter_ns()
                    future=executor.submit(client.call,'tools/call',{'name':'ui_candidates_detect_image','arguments':{'image_path':data['source'],'method':'ocr','max_candidates':255}})
                    boxes=detect()
                    response=future.result()
                    if 'error' in response:raise RuntimeError(response)
                    ocr=json.loads(response['result']['content'][0]['text'])
                    if not ocr.get('ok'):raise RuntimeError(ocr)
                    merged=merge_ocr(boxes,[box_item(b) for b in ocr['candidates']])
                    elapsed=(time.perf_counter_ns()-started)/1e6
                    if run:times.append(elapsed);ocr_times.append(ocr['perception_ms'])
                method='yolo-ocr-parallel'
                record={'name':'YOLOv9-E FP16 + Windows OCR (actual parallel tool path)',
                    'status':'tested parallel','boxes':merged,'latency':stats(times),
                    'ocr_detection_under_parallel_load':stats(ocr_times),
                    'note':'Measured simultaneous GPU detector + isolated DCU OCR request, joined and merged. Includes OCR file decode, annotation generation, MCP serialization and round-trip; excludes model startup and report drawing. This is NOT the same latency scope as detector-only pixels-to-boxes. No embeddings or captions.'}
                record['overlay']=f'{key}-{method}.png'
                render(image,record,folder/record['overlay'])
                data['methods'][method]=record;report['method_names'][method]=record['name']
                print(key,len(merged),record['latency'],flush=True)
    finally:
        try:client.close()
        except Exception:client.abort()
    # Preserve the original measurements separately before updating the report.
    (folder/'comparison-before-parallel.json').write_text(json.dumps(json.loads((folder/'comparison.json').read_text()),indent=2),encoding='utf-8')
    (folder/'comparison.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    report_html(report,folder)

if __name__=='__main__':main()
