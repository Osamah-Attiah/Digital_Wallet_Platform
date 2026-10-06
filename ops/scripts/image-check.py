#!/usr/bin/env python3
import json,subprocess,tarfile,tempfile,os
from pathlib import Path
values=dict(line.split('=',1) for line in Path('.env').read_text().splitlines());checks=[]
for name in ['api','worker','migrator','admin']:
    image='digitalwallet/'+name+':'+values['VERSION']; info=json.loads(subprocess.check_output(['docker','inspect',image]))[0]
    user=info['Config']['User']; assert user and user not in ['root','0','0:0']
    labels=info['Config']['Labels'];assert labels['org.opencontainers.image.revision']==values['GIT_SHA'];assert labels['org.opencontainers.image.version']==values['VERSION'];assert labels['org.opencontainers.image.created']==values['BUILD_DATE']
    container=subprocess.check_output(['docker','create',image],text=True).strip()
    try:
        with tempfile.TemporaryFile() as output:
            subprocess.run(['docker','export',container],stdout=output,check=True);output.seek(0)
            with tarfile.open(fileobj=output) as archive:
                names=archive.getnames()
                forbidden=[p for p in names if p in ['bin/sh','bin/bash','bin/ash','usr/bin/dotnet-sdk','usr/bin/gcc','usr/bin/npm','usr/bin/node'] or '/.git/' in p or '/tests/' in p or p.endswith('/.env') or p.endswith('.cs') or p.endswith('.csproj') or 'Microsoft.CodeAnalysis' in p]
                assert not forbidden,(name,forbidden[:4])
                for p in names:
                    if p.startswith('app/') and p.endswith('.json'):
                        item=archive.extractfile(p)
                        if item:
                            text=item.read().decode(errors='replace'); assert 'development-only-' not in text
    finally:subprocess.run(['docker','rm',container],check=True,stdout=subprocess.DEVNULL)
    checks.append({'image':name,'imageId':info['Id'],'user':user,'version':values['VERSION'],'gitSha':values['GIT_SHA'],'buildDate':values['BUILD_DATE'],'noShellBuildToolsSourceSecrets':True,'releaseLabelsMatch':True})
(Path(os.environ.get('WALLET_EVIDENCE_DIR','ops/validation/phase13'))/'images.json').write_text(json.dumps({'status':'passed','checks':checks},indent=2)+'\n');print('Four images passed non-root and filesystem checks.')
