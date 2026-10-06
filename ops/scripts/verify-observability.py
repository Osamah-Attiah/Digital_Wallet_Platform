#!/usr/bin/env python3
import base64,json,subprocess,urllib.request
from pathlib import Path
root=Path(__file__).resolve().parents[2]
key=(root/'.local/secrets/grafana_password').read_text().strip()
headers={'Authorization':'Basic '+base64.b64encode(('wallet_observer:'+key).encode()).decode()}
def grafana(path):
 with urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:3000'+path,headers=headers),timeout=10) as response:return json.load(response)
dashboards=grafana('/api/search?type=dash-db');assert len(dashboards)==5
assert grafana('/api/datasources/uid/wallet-prometheus')['type']=='prometheus'
# The image's HTTP probe confirms collector listener health; Prometheus grammar and alert scenarios use the actual binary.
subprocess.run(['docker','compose','exec','-T','collector','/probe','http://127.0.0.1:13133/'],check=True)
subprocess.run(['docker','compose','exec','-T','prometheus','promtool','check','rules','/etc/prometheus/alerts.rules.json'],check=True)
rules=subprocess.check_output(['docker','compose','exec','-T','prometheus','wget','-qO-','http://127.0.0.1:9090/api/v1/rules'],text=True)
parsed=json.loads(rules); count=sum(len(group['rules']) for group in parsed['data']['groups']);assert count==19
metrics=subprocess.check_output(['docker','compose','exec','-T','prometheus','wget','-qO-','http://collector:9464/metrics'],text=True)
assert 'wallet_api_requests' in metrics and 'outbox_pending' in metrics
result={'status':'passed','healthyServices':3,'dashboards':len(dashboards),'rules':count,'apiAndWorkerMetricsReceived':True,'datasourceUidResolved':True}
(root/'ops/validation/phase13/observability.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
