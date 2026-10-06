#!/usr/bin/env python3
"""Protected, explicit registry publication. Build locally once; scan and sign the same image digests."""
import hashlib, json, os, re, subprocess, xml.etree.ElementTree as ET
from pathlib import Path
assert os.environ.get('CLOUD_PUBLICATION_APPROVED')=='true'
repo=os.environ['REGISTRY_REPOSITORY'].lower();assert re.fullmatch(r'[a-z0-9_.-]+/[a-z0-9_.-]+',repo)
values=dict(line.split('=',1) for line in Path('.env').read_text().splitlines());version=values['VERSION']
config=json.loads(Path('ops/cloud/runtime/compose.yaml').read_text());images={name:'digitalwallet/'+name+':'+version for name in ['api','worker','migrator','admin','collector']}
for name in ['postgres','redis','rabbitmq','prometheus','grafana','edge','alertmanager']:images[name]=config['services'][name]['image']
images['postgres']='digitalwallet/cloud-postgres:'+version;images['redis']='digitalwallet/cloud-redis:'+version;images['grafana']='digitalwallet/cloud-grafana:'+version;images['alertmanager']='digitalwallet/cloud-alertmanager:'+version
root=Path.cwd();folder=root/'.local/cloud-package';folder.mkdir(parents=True,exist_ok=True)
scans={}
for name,image in images.items():
    if not image.startswith('digitalwallet/'):subprocess.run(['docker','pull',image],check=True)
    report=folder/(name+'.scan.json')
    subprocess.run(['docker','run','--rm','-v','/var/run/docker.sock:/var/run/docker.sock','-v',str(folder)+':/output','-v',str(root/'.local/trivy-cache')+':/root/.cache/trivy','aquasec/trivy:0.67.2@sha256:e2b22eac59c02003d8749f5b8d9bd073b62e30fefaef5b7c8371204e0a4b0c08','image','--scanners','vuln,secret','--format','json','--output','/output/'+report.name,image],check=True)
    scan=json.loads(report.read_text());findings=[v for r in scan.get('Results',[]) for v in r.get('Vulnerabilities',[]) if v['Severity'] in ['HIGH','CRITICAL']];secrets=[s for r in scan.get('Results',[]) for s in r.get('Secrets',[])]
    assert not findings and not secrets,(name,'Deployment blocked by image findings')
    scans[name]=scan['Metadata']['ImageID']
# Do not turn missing test files or skipped tests into a pass.
trx=list((folder/'trx').glob('*.trx'));assert len(trx)==2
counters=[ET.parse(p).find('.//{*}Counters').attrib for p in trx]
passed=sum(int(c['passed']) for c in counters);failed=sum(int(c['failed']) for c in counters);total=sum(int(c['total']) for c in counters);assert passed>=542 and failed==0 and total==passed
frontend=json.loads((folder/'frontend.json').read_text());assert frontend['numPassedTests']==14 and frontend['numFailedTests']==0 and frontend['numPendingTests']==0
migration=json.loads(Path('ops/validation/phase14/migrations.json').read_text());smoke=json.loads(Path('ops/validation/phase14/smoke.json').read_text());assert migration['status']=='passed' and smoke['status']=='passed'
# Only after all gates have passed can a registry mutation occur.
subprocess.run(['docker','login','ghcr.io','-u',os.environ['REGISTRY_ACTOR'],'--password-stdin'],input=os.environ['REGISTRY_TOKEN'],text=True,check=True)
published={}
try:
    for name,image in images.items():
        assert json.loads(subprocess.check_output(['docker','inspect',image]))[0]['Id']==scans[name]
        tag='ghcr.io/'+repo+'/'+name+':'+values['GIT_SHA'];subprocess.run(['docker','tag',image,tag],check=True);subprocess.run(['docker','push',tag],check=True)
        digests=json.loads(subprocess.check_output(['docker','inspect',tag]))[0]['RepoDigests'];digest=next(d for d in digests if d.startswith(tag.split(':')[0]+'@sha256:'))
        subprocess.run(['cosign','sign','--yes',digest],check=True)
        # Attach the exact vulnerability report as a signed predicate; original app SPDX remains in phase14 evidence.
        subprocess.run(['cosign','attest','--yes','--type','https://digitalwallet.invalid/attestations/trivy/v1','--predicate',str(folder/(name+'.scan.json')),digest],check=True)
        published[name]=digest
finally:subprocess.run(['docker','logout','ghcr.io'],check=False)
release={'revision':values['GIT_SHA'],'version':version,'images':published,'acceptance':{'backendPassed':passed,'backendFailed':0,'backendSkipped':0,'frontendPassed':14,'frontendBuild':True,'localSmoke':True,'pendingMigrations':0,'critical':0,'high':0,'imageSecrets':0}}
(folder/'release.json').write_text(json.dumps(release,indent=2)+'\n');print('Reviewed signed release manifest prepared.')
