#!/usr/bin/env python3
import json, re, sys
from pathlib import Path

def validate(path):
    release=json.loads(Path(path).read_text());assert set(release)=={'revision','version','images','acceptance'}
    assert re.fullmatch(r'[a-f0-9]{40}',release['revision']) and re.fullmatch(r'[a-zA-Z0-9._-]+',release['version'])
    expected={'api','worker','migrator','admin','collector','postgres','redis','rabbitmq','prometheus','grafana','edge','alertmanager'}
    assert set(release['images'])==expected
    for name,image in release['images'].items():assert re.fullmatch(r'ghcr\.io/[a-z0-9_.-]+/[a-z0-9_.-]+/'+name+r'@sha256:[a-f0-9]{64}',image)
    a=release['acceptance'];assert a['backendPassed']>=542 and a['backendFailed']==0 and a['backendSkipped']==0
    assert a['frontendPassed']==14 and a['frontendBuild'] is True and a['localSmoke'] is True
    assert a['critical']==0 and a['high']==0 and a['imageSecrets']==0 and a['pendingMigrations']==0
    return release
if __name__=='__main__':
    validate(sys.argv[1]);print('Release manifest passed immutable-image and acceptance gates.')
