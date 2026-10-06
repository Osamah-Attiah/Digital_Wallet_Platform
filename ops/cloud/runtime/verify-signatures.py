#!/usr/bin/env python3
import base64,hashlib,json,os,subprocess,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parent))
import importlib.util
spec=importlib.util.spec_from_file_location('release_validator',Path(__file__).with_name('validate-release.py'));validator=importlib.util.module_from_spec(spec);spec.loader.exec_module(validator)
paths=list(Path('.local/cloud-download').rglob('release.json'));assert len(paths)==1
path=paths[0];assert hashlib.sha256(path.read_bytes()).hexdigest()==os.environ['MANIFEST_SHA'];release=validator.validate(path)
repo=os.environ['REPOSITORY'].lower();identity='https://github.com/'+os.environ['REPOSITORY']+'/.github/workflows/cloud-package.yml@refs/heads/main'
assert release['revision']==subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip()
subprocess.run(['docker','login','ghcr.io','-u',os.environ['REGISTRY_ACTOR'],'--password-stdin'],input=os.environ['GH_TOKEN'],text=True,check=True)
try:
 for name,digest in release['images'].items():
  assert digest.startswith('ghcr.io/'+repo+'/'+name+'@sha256:')
  subprocess.run(['cosign','verify','--certificate-identity',identity,'--certificate-oidc-issuer','https://token.actions.githubusercontent.com',digest],check=True,stdout=subprocess.DEVNULL)
  output=subprocess.check_output(['cosign','verify-attestation','--type','https://digitalwallet.invalid/attestations/trivy/v1','--certificate-identity',identity,'--certificate-oidc-issuer','https://token.actions.githubusercontent.com',digest])
  envelopes=[json.loads(line) for line in output.decode().splitlines() if line.strip()]
  assert envelopes
  for envelope in envelopes:
   statement=json.loads(base64.b64decode(envelope['payload'],validate=True))
   assert statement['predicateType']=='https://digitalwallet.invalid/attestations/trivy/v1'
   assert any(s.get('digest',{}).get('sha256')==digest.split('@sha256:')[1] for s in statement['subject'])
   scan=statement['predicate']
   assert scan.get('SchemaVersion')==2 and scan.get('Metadata',{}).get('ImageID')
   assert not any(v['Severity'] in ['HIGH','CRITICAL'] for r in scan.get('Results',[]) for v in r.get('Vulnerabilities',[]))
   assert not any(r.get('Secrets') for r in scan.get('Results',[]))
finally:subprocess.run(['docker','logout','ghcr.io'],check=False)
Path('.local/approved-release.json').write_bytes(path.read_bytes());print('All immutable image signatures and signed scan attestations verified.')
