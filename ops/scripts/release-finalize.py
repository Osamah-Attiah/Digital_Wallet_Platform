#!/usr/bin/env python3
import hashlib,shutil
from pathlib import Path
folder=Path('.local/release');evidence=folder/'evidence';evidence.mkdir(parents=True,exist_ok=True)
for p in Path('ops/validation/phase13').glob('*.json'):shutil.copy2(p,evidence/p.name)
rows=[]
for p in sorted(folder.rglob('*')):
 if p.is_file() and p.name!='SHA256SUMS':rows.append(hashlib.file_digest(p.open('rb'),'sha256').hexdigest()+'  '+str(p.relative_to(folder)))
(folder/'SHA256SUMS').write_text('\n'.join(rows)+'\n')
