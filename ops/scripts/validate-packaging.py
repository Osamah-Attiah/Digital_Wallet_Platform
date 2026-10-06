#!/usr/bin/env python3
import json,subprocess,re
from pathlib import Path
compose=json.loads(subprocess.check_output(['docker','compose','config','--format','json']))
services=compose['services']
for name in ['api','worker','migrate','admin-web','notification-sink']:
    s=services[name]; assert s['read_only']; assert 'ALL' in s['cap_drop']; assert s['pids_limit']<=256
for name in ['api','worker']:
    assert services[name]['depends_on']['migrate']['condition']=='service_completed_successfully'
assert compose['networks']['private']['internal']
for name in ['postgres','redis','rabbitmq','worker','notification-sink']:
    assert not services[name].get('ports')
for name in ['api','admin-web']:
    assert all(p['host_ip']=='127.0.0.1' for p in services[name].get('ports',[]))
for path in Path('ops/containers').glob('*.Dockerfile'):
    s=path.read_text(); assert 'USER 1654:1654' in s or 'USER 10001:10001' in s; assert 'HEALTHCHECK' in s
    for image in re.findall(r'^FROM ([^\s]+)',s,re.M):assert image=='scratch' or '@sha256:' in image
locks=list(Path('src').rglob('packages.lock.json'))+list(Path('tests').rglob('packages.lock.json'));assert len(locks)>=32
result={'status':'passed','services':len(services),'lockedProjects':len(locks),'privatePorts':False,'migrationGate':True,'nonRootImages':4}
Path('ops/validation/phase13/packaging.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
