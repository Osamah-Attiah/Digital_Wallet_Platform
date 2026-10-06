#!/usr/bin/env python3
"""Validate the deployment contract without starting containers or contacting cloud APIs."""
import json, os, subprocess
from pathlib import Path
root=Path(__file__).resolve().parents[3];os.chdir(root)
folder=root/'.local/cloud-contract-review';folder.mkdir(parents=True,exist_ok=True)
values={'DEMO_HOST':'192.0.2.1','RELEASE_SHA':'0'*40,'RELEASE_VERSION':'14.0.0-review','WALLET_DATA_DIR':str(folder/'data'),'WALLET_TLS_DIR':str(folder/'tls'),'WALLET_SECRETS_DIR':str(folder/'secrets')}
for name in ['api','worker','migrator','admin','collector','postgres','redis','grafana','alertmanager']:values[name.upper()+'_IMAGE']='ghcr.io/review/placeholder/'+name+'@sha256:'+'0'*64
env=folder/'review.env';env.write_text(''.join(k+'='+v+'\n' for k,v in values.items()))
config=json.loads(subprocess.check_output(['docker','compose','--env-file',str(env),'-f','ops/cloud/runtime/compose.yaml','config','--format','json'],text=True))
s=config['services'];checks={
 'twelve-services':len(s)==12,
 'production-only-hosts':all(s[n]['environment']['ASPNETCORE_ENVIRONMENT']=='Production' for n in ['api','worker','migrate']),
 'dependency-tls-enabled':all(s[n]['environment']['Hosting__DependencyTls']=='true' for n in ['api','worker','migrate']),
 'fixtures-disabled':all(not any(k.startswith('DevelopmentFixtures') for k in s[n]['environment']) for n in ['api','worker','migrate']),
 'single-trusted-proxy':s['api']['environment']['HttpSecurity__KnownProxies__0']=='172.28.0.3',
 'no-published-data-or-api-ports':not any(s[n].get('ports') for n in ['api','worker','postgres','redis','rabbitmq','collector','prometheus','alertmanager']),
 'grafana-loopback-only':all(p.get('host_ip')=='127.0.0.1' for p in s['grafana']['ports']),
 'private-data-networks':config['networks']['private']['internal'] and config['networks']['operations']['internal'],
 'nonprivileged-readonly-services':all(v.get('read_only') and 'ALL' in v.get('cap_drop',[]) and not v.get('privileged',False) for v in s.values()),
 'migration-before-runtime':all(s[n]['depends_on']['migrate']['condition']=='service_completed_successfully' for n in ['api','worker']),
 'all-image-references-immutable':all('@sha256:' in v['image'] for v in s.values()),
 'scoped-secrets':not any(x['source'].startswith('demo_') for n in ['api','worker'] for x in s[n]['secrets']),
 'persistent-failure-blocks-reboot':('deployment-in-progress' in Path('ops/cloud/runtime/wallet-demo.service').read_text() and 'compose.active.json' in Path('ops/cloud/runtime/deploy.sh').read_text())
}
checks['all-repository-bind-sources-exist']=all(Path(v['source']).exists() for service in s.values() for v in service.get('volumes',[]) if v.get('type')=='bind' and not v['source'].startswith(str(folder)))
assert all(checks.values()),checks
out={'status':'passed','scope':'static resolved Compose validation; placeholder identities; no containers started; no cloud resource or TLS handshake tested','checks':checks,'readyToDeploy':False}
Path('ops/validation/phase14/runtime-contract.json').write_text(json.dumps(out,indent=2)+'\n');print(json.dumps(out))
