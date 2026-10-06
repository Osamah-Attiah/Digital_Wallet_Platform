#!/usr/bin/env python3
import datetime, os, re
from pathlib import Path
sha=os.environ['RELEASE_SHA'];ref=os.environ['RELEASE_REF']
assert re.fullmatch('[0-9a-f]{40}',sha)
version=ref[1:] if re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?',ref) else '14.0.0-'+sha[:12]
Path('.env').write_text('VERSION='+version+'\nGIT_SHA='+sha+'\nBUILD_DATE='+datetime.datetime.now(datetime.timezone.utc).isoformat()+'\n')
with open(os.environ['GITHUB_OUTPUT'],'a') as f:f.write('version='+version+'\n')
