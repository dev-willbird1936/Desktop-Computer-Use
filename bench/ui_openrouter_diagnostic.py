#!/usr/bin/env python3
"""One separately reviewed Gemini failure diagnostic; no automatic retry or fallback."""
import json
from pathlib import Path
import re
import time
import urllib.error
import urllib.request
from ui_openrouter_compare import API, NoRedirect, key_from_config, payload_for

folder=Path(r'C:/Users/warcr/AppData/Local/Temp/ui-perception-offline-compare-r1')
config={'model':'google/gemini-3.1-flash-lite','reasoning_effort':'minimal'}
image=Path(r'C:/Users/warcr/AppData/Local/Temp/pi-clipboard-bec114a8-de4a-4a00-89fe-79309ada5503.png')
payload,_,_,_=payload_for(config,image)
key=key_from_config()
request=urllib.request.Request(API,data=json.dumps(payload).encode(),method='POST',headers={'Authorization':'Bearer '+key,'Content-Type':'application/json','X-OpenRouter-Title':'DCU authorized one-call API diagnostic'})
result={'purpose':'one Gemini HTTP 400 diagnostic','model':config['model'],'thinking':'minimal'}
start=time.perf_counter_ns()
try:
    with urllib.request.build_opener(NoRedirect()).open(request,timeout=90) as response:
        data=json.load(response)
    result.update({'status':'success','response':data})
except urllib.error.HTTPError as error:
    raw=error.read().decode('utf-8',errors='replace').replace(key,'<credential redacted>')
    raw=re.sub(r'data:image/[^;]+;base64,[A-Za-z0-9+/=]+','<image data redacted>',raw)
    result.update({'status':'HTTP failure','http_status':error.code,'error_detail':raw[:2000]})
except Exception as error:
    result.update({'status':'request failure','error_type':type(error).__name__})
result['http_ms']=round((time.perf_counter_ns()-start)/1e6,3)
(folder/'gemini-one-call-diagnostic.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result,indent=2))
