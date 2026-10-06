#!/usr/bin/env python3
"""Only the approved protected workflow calls this; no cloud resource creation here."""
import json,os,re,shlex,subprocess
from pathlib import Path
assert os.environ.get('CLOUD_EXECUTION_APPROVED')=='true'
project=os.environ['GCP_PROJECT'];zone=os.environ['GCP_ZONE'];instance=os.environ['GCP_INSTANCE']
assert all(re.fullmatch('[a-z0-9-]+',x) for x in [project,zone,instance])
release=json.loads(Path('.local/approved-release.json').read_text());revision=release['revision']
bundle=Path('.local/cloud-release.tar')
with bundle.open('wb') as stream:subprocess.run(['git','archive','--format=tar','HEAD','ops/cloud/runtime','ops/postgres','ops/observability/provisioning','ops/observability/alerts.rules.json'],stdout=stream,check=True)
common=['--project',project,'--zone',zone,'--tunnel-through-iap']
subprocess.run(['gcloud','compute','scp',str(bundle),'.local/approved-release.json',instance+':/tmp/',*common],check=True)
def remote(command,input=None):subprocess.run(['gcloud','compute','ssh',instance,*common,'--command',command],input=input,text=True,check=True)
# Short-lived read credential through SSH stdin. Never store it in release manifest, process arguments or artifacts.
try:
 remote('sudo docker login ghcr.io -u '+shlex.quote(os.environ['REGISTRY_ACTOR'])+' --password-stdin',os.environ['GH_TOKEN'])
 command=' && '.join([
  'sudo install -d -m 0755 /srv/wallet/releases/'+revision,
  'sudo tar -xf /tmp/cloud-release.tar -C /srv/wallet/releases/'+revision,
  "sudo sh -c 'test ! -f /etc/wallet/release.json || cp /etc/wallet/release.json /etc/wallet/previous-release.json'",
  'sudo install -m 0644 /tmp/approved-release.json /etc/wallet/release.json',
  'sudo env CLOUD_EXECUTION_APPROVED=true APPROVED_MANIFEST_SHA256='+shlex.quote(os.environ['APPROVED_MANIFEST_SHA256'])+' sh /srv/wallet/releases/'+revision+'/ops/cloud/runtime/deploy.sh',
  'sudo ln -sfn /srv/wallet/releases/'+revision+' /srv/wallet/current'
 ])
 remote(command)
finally:
 remote('sudo docker logout ghcr.io; rm -f /tmp/cloud-release.tar /tmp/approved-release.json')
