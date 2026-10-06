#!/usr/bin/env python3
"""Read-only local scans of every proposed infrastructure image; no registry publication."""
import json,os,subprocess,collections
from pathlib import Path
root=Path(__file__).resolve().parents[3];os.chdir(root);out=root/'ops/validation/phase14';cache=root/'.local/trivy-cache';cache.mkdir(exist_ok=True)
config=json.loads(Path('ops/cloud/runtime/compose.yaml').read_text());values=dict(line.split('=',1) for line in Path('.env').read_text().splitlines())
images={n:config['services'][n]['image'] for n in ['postgres','redis','rabbitmq','prometheus','grafana','edge','alertmanager']};images['collector']='digitalwallet/collector:'+values['VERSION'];images['postgres']='digitalwallet/cloud-postgres:'+values['VERSION'];images['redis']='digitalwallet/cloud-redis:'+values['VERSION'];images['grafana']='digitalwallet/cloud-grafana:'+values['VERSION'];images['alertmanager']='digitalwallet/cloud-alertmanager:'+values['VERSION'];results=[]
for name,image in images.items():
 if not image.startswith('digitalwallet/'):subprocess.run(['docker','pull',image],check=True,stdout=subprocess.DEVNULL)
 report=out/('infra-'+name+'.scan.json')
 subprocess.run(['docker','run','--rm','-v','/var/run/docker.sock:/var/run/docker.sock','-v',str(out)+':/output','-v',str(cache)+':/root/.cache/trivy','aquasec/trivy:0.67.2@sha256:e2b22eac59c02003d8749f5b8d9bd073b62e30fefaef5b7c8371204e0a4b0c08','image','--quiet','--scanners','vuln,secret','--format','json','--output','/output/'+report.name,image],check=True)
 scan=json.loads(report.read_text());v=[v for r in scan.get('Results',[]) for v in r.get('Vulnerabilities',[])];s=[s for r in scan.get('Results',[]) for s in r.get('Secrets',[])]
 counts=dict(collections.Counter(x['Severity'] for x in v));results.append({'image':name,'reference':image,'imageId':scan['Metadata']['ImageID'],'findings':counts,'imageSecrets':len(s)})
 print(json.dumps(results[-1]),flush=True)
failed=any(r['findings'].get('HIGH',0) or r['findings'].get('CRITICAL',0) or r['imageSecrets'] for r in results)
(out/'infrastructure-security.json').write_text(json.dumps({'status':'failed' if failed else 'passed','results':results,'exceptions':0},indent=2)+'\n')
raise SystemExit(1 if failed else 0)
