#!/usr/bin/env python3
"""Host-only Secret Manager fetch. Never uses latest or places secret values in CLI/env/evidence."""
import base64, json, os, re, urllib.request
from pathlib import Path
manifest=json.loads(Path('/etc/wallet/secret-versions.json').read_text())
folder=Path('/run/wallet'); folder.mkdir(mode=0o700,exist_ok=True);folder.chmod(0o700)
req=urllib.request.Request('http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token',headers={'Metadata-Flavor':'Google'})
with urllib.request.urlopen(req,timeout=5) as response: token=json.load(response)['access_token']
for name,resource in manifest.items():
    assert re.fullmatch(r'[a-z_]+',name) and re.fullmatch(r'projects/[a-z0-9-]+/secrets/wallet-demo-[a-z0-9-]+/versions/[0-9]+',resource)
    req=urllib.request.Request('https://secretmanager.googleapis.com/v1/'+resource+':access',headers={'Authorization':'Bearer '+token})
    with urllib.request.urlopen(req,timeout=10) as response: payload=base64.b64decode(json.load(response)['payload']['data'],validate=True)
    if name in ['dependency_tls','edge_tls']:
        bundle=json.loads(payload);allowed={'ca.crt','postgres.crt','postgres.key','redis.crt','redis.key','rabbitmq.crt','rabbitmq.key','collector.crt','collector.key'} if name=='dependency_tls' else {'server.crt','server.key'}
        assert set(bundle)==allowed
        for component,uid in ([('postgres',70),('redis',999),('rabbitmq',999),('collector',10001),('client',1654)] if name=='dependency_tls' else [('edge',1000)]):
            target=folder/'tls'/component;target.mkdir(mode=0o755,parents=True,exist_ok=True)
            keys=['ca.crt'] if component=='client' else (['server.crt','server.key'] if component=='edge' else ['ca.crt',component+'.crt',component+'.key'])
            for key in keys:
                path=target/key;path.write_text(bundle[key]);path.chmod(0o600 if key.endswith('.key') else 0o644);os.chown(path,uid,uid)
    else:
        assert len(payload.strip())>=32 and re.fullmatch(rb'[A-Za-z0-9_-]+',payload.strip())
        target=folder/'secrets';target.mkdir(mode=0o755,exist_ok=True)
        path=target/name;path.write_bytes(payload.strip());path.chmod(0o444) # Parent /run/wallet is root-only; scoped bind mounts are read-only.
print('Pinned secret versions loaded into host tmpfs; values not printed.')
