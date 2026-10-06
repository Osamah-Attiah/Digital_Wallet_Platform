#!/usr/bin/env python3
"""Only invoked by the protected manual publish job; never executed during phase 13 implementation."""
import hashlib,json,os,re,subprocess
from pathlib import Path
folder=Path('.local/download/.local/release')
if not folder.exists():folder=Path('.local/download/release')
if not folder.exists():folder=Path('.local/download')
repo=os.environ['REGISTRY_REPOSITORY'].lower();version=os.environ['RELEASE_VERSION']
assert re.fullmatch(r'[a-z0-9_.-]+/[a-z0-9_.-]+',repo) and re.fullmatch(r'[a-zA-Z0-9_.-]+',version)
for line in (folder/'SHA256SUMS').read_text().splitlines():
    digest,name=line.split('  ',1);p=folder/name
    assert p.resolve().is_relative_to(folder.resolve()) and hashlib.file_digest(p.open('rb'),'sha256').hexdigest()==digest
subprocess.run(['docker','login','ghcr.io','-u',os.environ['REGISTRY_ACTOR'],'--password-stdin'],input=os.environ['REGISTRY_TOKEN'],text=True,check=True)
try:
    for name in ['api','worker','migrator','admin']:
        image='ghcr.io/'+repo+'/'+name+':'+version
        subprocess.run(['skopeo','copy','--all','--dest-authfile',str(Path.home()/'.docker/config.json'),'oci-archive:'+str(folder/(name+'.oci.tar')),'docker://'+image],check=True)
        raw=subprocess.check_output(['skopeo','inspect','--raw','docker://'+image]);digest=image.split(':')[0]+'@sha256:'+hashlib.sha256(raw).hexdigest()
        subprocess.run(['cosign','sign','--yes',digest],check=True)
        subprocess.run(['cosign','attest','--yes','--type','spdxjson','--predicate',str(Path('.local/download/ops/validation/phase13')/(name+'.sbom.spdx.json')),digest],check=True)
finally:subprocess.run(['docker','logout','ghcr.io'],check=False)
