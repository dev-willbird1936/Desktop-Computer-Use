#!/usr/bin/env python3
"""Recover saved cloud trial artifacts and update the comparison; never makes API calls."""
import json
from decimal import Decimal
from pathlib import Path
import sys
from PIL import Image
from ui_perception_compare import overlap, render, report_html, stats
from ui_openrouter_normalized_compare import CONFIGS

folder=Path(sys.argv[1])
plan=json.loads((folder/'openrouter-test-plan.json').read_text())
report=json.loads((folder/'comparison-before-openrouter.json').read_text())
reference=json.loads((folder/'btd-card-reference.json').read_text())


def card_match(boxes):
    pairs=sorted(((overlap(p['bbox'],g['bbox']),pi,gi) for pi,p in enumerate(boxes) for gi,g in enumerate(reference['cards'])),reverse=True)
    used_pred=set();used_gt=set();matches=[]
    for iou,pi,gi in pairs:
        if iou<.5:break
        if pi in used_pred or gi in used_gt:continue
        used_pred.add(pi);used_gt.add(gi)
        matches.append({'reference':reference['cards'][gi]['key'],'proposal_index':pi+1,'iou':round(iou,3)})
    return {'matched':len(used_gt),'total':16,'threshold':.5,'matches':matches,
            'missed':[g['key'] for gi,g in enumerate(reference['cards']) if gi not in used_gt],
            'note':'Benchmark-only manual card rectangles; one-to-one IoU >= .5. Not supplied to models. This does not verify names or clickability.'}


cost=0.;completed=0

def add_record(config,imagekey,trials,requested,mode):
    global cost,completed
    completed+=len(trials)
    cost+=sum(t.get('usage',{}).get('cost',0) or 0 for t in trials)
    valid=[t for t in trials if t.get('status')=='valid']
    name=config['name']+' — thinking '+config.get('reasoning_effort','not applicable (Instruct)')
    r={'name':name,'model':config['model'],'reasoning_effort':config.get('reasoning_effort','not applicable / omitted'),
       'status':'tested cloud' if len(valid)==requested else 'tested cloud / partial' if valid else 'failed / no valid inventory' if trials else 'not run / gate stopped',
       'request_mode':mode,'runs_requested':requested,'runs_completed':len(trials),'valid_runs':len(valid),
       'providers':sorted({t.get('provider') or 'not returned' for t in trials}),
       'trials':[{'repeat':t.get('repeat'),'status':t.get('status'),'error':t.get('error'),'http_status':t.get('http_status'),
                  'http_ms':t.get('http_ms'),'candidate_count':t.get('candidate_count'),'usage':t.get('usage',{})} for t in trials],
       'note':'Exact supplied image through OpenRouter. Full POST-to-completed-JSON latency includes upload, network and output generation, unlike local detector-only timing. No assistant-derived proposals. Names/coverage/clickability remain unverified. data_collection=deny; no automated retry or provider fallback.'}
    rejected=[t.get('http_ms') for t in trials if t.get('status')!='valid']
    if rejected:r['failed_response_http_ms']=rejected
    if any(t.get('repeat',1)>1 for t in trials):
        r['note']+=' Includes a separately user-requested Anvil redo with an explicit JSON-syntax reminder. Prior failures remain recorded; this is not an automated retry.'
    if config.get('coordinate_space'):
        r['coordinate_space']=config['coordinate_space']
        r['note']+=' Declared normalized boxes explicitly converted to original pixels; labels limited to six words. This is a different output contract from the initial pixel-coordinate trials.'
    if valid:
        r['latency']=stats([t['http_ms'] for t in valid]);r['boxes']=valid[0]['boxes']
        r['coverage_asserted']=valid[0].get('coverage_asserted');r['coverage_issue']=valid[0].get('coverage_issue')
        r['candidate_counts_by_run']=[len(t['boxes']) for t in valid]
        r['overlay']=f"{imagekey}-{config['key']}.png"
        if imagekey=='btd':r['card_coverage_by_run']=[card_match(t['boxes']) for t in valid]
        if len(trials)<requested:r['note']+=' Interrupted: only saved responses included; remaining outcomes unknown/not recorded.'
        if len(valid)==1:r['note']+=' Single sample only, not a stable latency distribution or a reliability estimate.'
        with Image.open(report['images'][imagekey]['source']) as im:render(im.convert('RGB'),r,folder/r['overlay'])
    else:
        r['note']+=' '+ '; '.join(t.get('error','No recorded error') for t in trials)
        if not trials:r['note']+=' No call was sent for this row because the bounded trial stopped early.'
        if trials:r['failed_response_http_ms']=[t.get('http_ms') for t in trials]
    report['images'][imagekey]['methods'][config['key']]=r;report['method_names'][config['key']]=name
    print(imagekey,config['key'],'valid',len(valid),'ms',r.get('latency',{}).get('median_ms'),'cards',[v['matched'] for v in r.get('card_coverage_by_run',[])])


for config in plan['configs']:
    for imagekey in report['images']:
        files=sorted((folder/'openrouter-runs').glob(f"{imagekey}-{config['key']}-r*.json"))
        add_record(config,imagekey,[json.loads(f.read_text()) for f in files],plan['repeats'],'strict schema, requested original pixels')
initial_count=completed;initial_cost=cost
phase_trials={}
for phase in ('openrouter-json-mode','openrouter-normalized','openrouter-anvil-redo-r1'):
    if not (folder/phase).exists():continue
    for f in sorted((folder/phase).glob('*.json')):
        if f.name=='summary.json':continue
        trial=json.loads(f.read_text())
        phase_trials.setdefault((trial['image'],trial['config']['key']),[]).append(trial)
for (imagekey,_),trials in phase_trials.items():
    modes='; '.join(dict.fromkeys(t.get('request_mode','json_object') for t in trials))
    add_record(trials[0]['config'],imagekey,trials,len(trials),modes)
# Explicitly show all normalized configurations, including any skipped by the stop gate.
if (folder/'openrouter-normalized').exists():
    for config in CONFIGS:
        for imagekey,data in report['images'].items():
            if config['key'] not in data['methods']:add_record(config,imagekey,[],1,'normalized JSON mode')
for record in report['images']['btd']['methods'].values():
    if 'boxes' in record:record['btd_card_reference']=card_match(record['boxes'])
report['card_reference']=reference
report['openrouter_test']={'responses_persisted':completed,'initial_plan_posts':plan['maximum_planned_posts'],
    'reported_cost_usd_for_persisted_responses':round(cost,6),
    'initial_responses_persisted':initial_count,'initial_reported_cost_usd':round(initial_cost,6),
    'state':'Initial strict-schema batch interrupted at 900-second timeout; later separately reviewed compatibility trials are included with their exact request formats',
    'note':'One initial request may have been in flight and billed without a saved response. API-reported cost is not a guaranteed final account charge. Revised normalized trials have only one sample per model/thinking/image.',
    'data_collection':'deny','no_automatic_retries':True,'provider_fallbacks':False,
    'user_authorization':'then test it via open router and add it to comparison'}
gemini_key='or-gemini31-lite-minimal-norm'
btd_trials=phase_trials.get(('btd',gemini_key),[])
anvil_trials=phase_trials.get(('anvil',gemini_key),[])
if any(t.get('repeat',1)>1 for t in anvil_trials):
    btd=btd_trials[0];anvil=anvil_trials[-1]
    amounts=[t.get('usage',{}).get('cost') for t in (btd,anvil)]
    pair={'model':btd['config']['model'],'thinking':btd['config']['reasoning_effort'],
          'btd_cost_usd':amounts[0],'anvil_redo_cost_usd':amounts[1],
          'btd_status':btd['status'],'anvil_redo_status':anvil['status'],
          'note':'Existing BTD call plus the latest explicitly requested Anvil rerun. Earlier Anvil failures are excluded from the two-call total, not erased. API-reported inference cost, not a guaranteed final account charge.'}
    if all(isinstance(c,(int,float)) for c in amounts):
        pair['two_call_total_usd']=str(sum(Decimal(str(c)) for c in amounts))
    pair['earlier_anvil_reported_cost_usd']=str(sum(Decimal(str(t.get('usage',{}).get('cost') or 0)) for t in anvil_trials[:-1]))
    report['openrouter_test']['gemini_current_pair']=pair
if (folder/'gemini-one-call-diagnostic.json').exists():
    d=json.loads((folder/'gemini-one-call-diagnostic.json').read_text())
    report['openrouter_test']['diagnostic']={k:d.get(k) for k in ('purpose','model','thinking','status','http_status','http_ms')}
    report['openrouter_test']['diagnostic']['finding']='Google AI Studio INVALID_ARGUMENT; exact invalid parameter not identified. Strict-schema failure is not a measurement of recognition capability.'
(folder/'comparison.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
report_html(report,folder)
print('REPORT',folder/'comparison.html','PERSISTED_COST_USD',round(cost,6))
