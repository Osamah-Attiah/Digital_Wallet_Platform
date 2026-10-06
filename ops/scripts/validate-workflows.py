#!/usr/bin/env python3
from pathlib import Path
import re, json
checks=[]
for path in Path('.github/workflows').glob('*.yml'):
    source=path.read_text()
    assert not re.search(r'pull_request_target|workflow_run|\$\{\{[^}]*\bsecrets\s*(?:\.|\[)',source),path
    assert 'uses: actions/checkout' not in source or 'persist-credentials: false' in source
    assert 'contents: read' in source
    for action in re.findall(r'uses:\s*([^\s#]+)',source):
        assert re.fullmatch(r'[\w./-]+@[0-9a-f]{40}',action),(path,action)
    checks.append({'file':path.name,'fullShaActions':True,'trustedPrBoundary':True})
print(json.dumps({'workflows':checks,'status':'passed'}))
