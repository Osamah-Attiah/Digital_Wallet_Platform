#!/usr/bin/env python3
import json,os,subprocess
from pathlib import Path
root=Path(__file__).resolve().parents[2]
values=[p.read_bytes().strip() for p in (root/'.local/secrets').iterdir() if p.is_file()]
paths=subprocess.check_output(['git','ls-files','--cached','--others','--exclude-standard','-z'],cwd=root,text=True).split('\0')
tracked=[root/p for p in paths if p and (root/p).is_file()]
evidence=root/os.environ.get('WALLET_EVIDENCE_DIR','ops/validation/phase13');evidence.mkdir(parents=True,exist_ok=True)
artifacts=[p for p in evidence.rglob('*') if p.is_file()]+[p for p in (root/'.local/test-results').rglob('*') if p.is_file()]
found=[]
for path in tracked+artifacts:
 if path.is_file():
  data=path.read_bytes()
  if any(v and v in data for v in values):found.append(str(path.relative_to(root)))
result={'status':'failed' if found else 'passed','repositoryFiles':len(tracked),'artifactFiles':len(artifacts),'privateCredentialMatches':len(found)}
(evidence/'private-secrets.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result));raise SystemExit(1 if found else 0)
