#!/usr/bin/env python3
"""Re-render the saved comparison without rerunning inference or altering its timings."""
import json
from pathlib import Path
import sys
from ui_perception_compare import report_html

folder=Path(sys.argv[1])
report=json.loads((folder/'comparison.json').read_text(encoding='utf-8'))
report['hardware']='NVIDIA RTX 3080 12 GiB; AMD Ryzen 9 3950X (16 cores / 32 threads); GPU timings synchronized; concurrent host/GPU workloads not controlled.'
report['self_review']={'implementation':8.0,'evidence':7.0,'presentation':8.0,'overall':7.5,'scale':'S2.2',
                       'note':'Tooling/report self-review only, not detection accuracy. Several requested model routes are untested.'}
(folder/'comparison.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
report_html(report,folder)
model_dir=Path(report['weights_path']).parent
metadata={'source':'https://huggingface.co/microsoft/OmniParser-v2.0/resolve/main/icon_detect_v3/model.pt',
          'source_license':'https://huggingface.co/microsoft/OmniParser-v2.0/blob/main/icon_detect_v3/LICENSE',
          'license':'MIT (Microsoft)','sha256':report['weights_sha256'],'bytes':Path(report['weights_path']).stat().st_size,
          'runtime':'E:/models/dcu-ui-candidate-bench-20261003/venv','torch':report['torch_version'],
          'torchvision':report['torchvision_version'],'purpose':'offline operator-supplied screenshot comparison'}
(model_dir/'dcu-benchmark-provenance.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(folder/'comparison.html')
