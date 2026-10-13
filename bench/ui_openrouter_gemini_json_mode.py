#!/usr/bin/env python3
"""Bounded, revised Gemini compatibility trial: JSON-object mode instead of strict schema.

One call per model/thinking/image. If the first configuration fails validation, stop;
no automatic retries, redirects, provider fallbacks or relaxation of privacy routing.
"""
import json
from pathlib import Path
import time
import urllib.error
import urllib.request
from ui_openrouter_compare import API, NoRedirect, key_from_config, payload_for, parse_inventory

folder=Path(r'C:/Users/warcr/AppData/Local/Temp/ui-perception-offline-compare-r1')
inputs={
 'btd':Path(r'C:/Users/warcr/AppData/Local/Temp/pi-clipboard-bec114a8-de4a-4a00-89fe-79309ada5503.png'),
 'anvil':Path(r'C:/Users/warcr/AppData/Local/Temp/pi-clipboard-1ba6ba2e-7e27-4091-a70a-b3688514ff2a.png')}
configs=[
 {'key':'or-gemini31-lite-minimal-json','model':'google/gemini-3.1-flash-lite','reasoning_effort':'minimal','name':'Gemini 3.1 Flash Lite JSON mode'},
 {'key':'or-gemini31-lite-low-json','model':'google/gemini-3.1-flash-lite','reasoning_effort':'low','name':'Gemini 3.1 Flash Lite JSON mode'},
 {'key':'or-gemini3-flash-minimal-json','model':'google/gemini-3-flash-preview','reasoning_effort':'minimal','name':'Gemini 3 Flash JSON mode'}]
out=folder/'openrouter-json-mode';out.mkdir(exist_ok=False)
key=key_from_config();opener=urllib.request.build_opener(NoRedirect())
first=True;records=[]
for config in configs:
    for imagekey,path in inputs.items():
        payload,frame,width,height=payload_for(config,path)
        payload['response_format']={'type':'json_object'}
        example={'frame_id':frame,'image_width':width,'image_height':height,'candidate_set_complete':False,
                 'candidate_set_issue':'coverage uncertainty','candidates':[{'key':'c1','role':'button','label':'visual description','bbox':{'x':0,'y':0,'width':1,'height':1}}]}
        payload['messages'][1]['content'][0]['text']+='\nRequired output keys and structure (example only, not a detected target): '+json.dumps(example)+'. Return compact JSON, not pretty-printed JSON. Bounds must be original image pixels.'
        request=urllib.request.Request(API,data=json.dumps(payload).encode(),method='POST',headers={'Authorization':'Bearer '+key,'Content-Type':'application/json','X-OpenRouter-Title':'DCU authorized revised Gemini JSON-mode comparison'})
        trial={'config':config,'image':imagekey,'request_mode':'json_object','repeat':1}
        started=time.perf_counter_ns()
        try:
            with opener.open(request,timeout=120) as response:data=json.load(response)
            trial.update({'http_ms':round((time.perf_counter_ns()-started)/1e6,3),'provider':data.get('provider'),'usage':data.get('usage',{}),'response':data})
            try:
                parsed,boxes=parse_inventory(data,frame,width,height)
                trial.update({'status':'valid','boxes':boxes,'candidate_count':len(boxes),'coverage_asserted':parsed['candidate_set_complete'],'coverage_issue':parsed['candidate_set_issue']})
            except (ValueError,KeyError,TypeError) as error:trial.update({'status':'validation failed','error':str(error)})
        except urllib.error.HTTPError as error:
            try:
                detail=json.loads(error.read()).get('error',{})
                msg=detail.get('message','unknown provider error');provider=detail.get('metadata',{}).get('provider_name')
            except Exception:msg='Could not parse provider error';provider=None
            trial.update({'status':'HTTP failure','http_status':error.code,'error':msg.replace(key,'<redacted>')[:300],'provider':provider,'http_ms':round((time.perf_counter_ns()-started)/1e6,3)})
        except Exception as error:trial.update({'status':'request failure','error':type(error).__name__,'http_ms':round((time.perf_counter_ns()-started)/1e6,3)})
        (out/f"{imagekey}-{config['key']}.json").write_text(json.dumps(trial,indent=2),encoding='utf-8')
        records.append(trial);print(imagekey,config['key'],trial['status'],trial.get('candidate_count'),trial.get('http_ms'),flush=True)
        if first and trial['status']!='valid':
            print('STOP: first revised request did not yield a valid inventory; remaining configurations not sent.',flush=True)
            (out/'summary.json').write_text(json.dumps({'requests':len(records),'stopped_early':True},indent=2))
            raise SystemExit(0)
        first=False
(out/'summary.json').write_text(json.dumps({'requests':len(records),'stopped_early':False},indent=2))
