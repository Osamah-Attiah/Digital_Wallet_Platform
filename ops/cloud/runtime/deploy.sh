#!/bin/sh
set -eu
[ "${CLOUD_EXECUTION_APPROVED:-}" = "true" ] || { echo 'Cloud deployment requires explicit user approval.' >&2; exit 1; }
[ "$(id -u)" = 0 ] || exit 1
release_dir=$(CDPATH= cd -- "$(dirname -- "$0")/../../.." && pwd)
cd "$release_dir"
python3 ops/cloud/runtime/validate-release.py /etc/wallet/release.json
# Signature verification is done in the protected CI job before this host script is called.
# The host accepts only the manifest checksum reviewed in that protected job.
printf '%s  %s\n' "$APPROVED_MANIFEST_SHA256" /etc/wallet/release.json | sha256sum -c -
python3 ops/cloud/runtime/fetch-secrets.py
python3 - <<'PY'
import json
from pathlib import Path
r=json.loads(Path('/etc/wallet/release.json').read_text());runtime=Path('/etc/wallet/runtime.env');existing=runtime.read_text()
lines=[line for line in existing.splitlines() if not line.startswith(('API_IMAGE=','WORKER_IMAGE=','MIGRATOR_IMAGE=','ADMIN_IMAGE=','COLLECTOR_IMAGE=','RELEASE_VERSION=','RELEASE_SHA='))]
for name in ['api','worker','migrator','admin','collector']:lines.append(name.upper()+'_IMAGE='+r['images'][name])
lines+=['RELEASE_VERSION='+r['version'],'RELEASE_SHA='+r['revision']];runtime.write_text('\n'.join(lines)+'\n');runtime.chmod(0o600)
# All infrastructure images must also use the signed mirror digests from the release manifest.
p=Path('ops/cloud/runtime/compose.yaml');config=json.loads(p.read_text())
for name in ['postgres','redis','rabbitmq','prometheus','grafana','edge','alertmanager']:config['services'][name]['image']=r['images'][name]
p=Path('/etc/wallet/compose.release.json');p.write_text(json.dumps(config,indent=2)+'\n')
# Resolve bind paths because the release contract lives in /etc/wallet.
for service in config['services'].values():
 for volume in service.get('volumes',[]):
  if isinstance(volume,dict) and volume.get('type')=='bind' and not volume['source'].startswith(('/', '${')):volume['source']=str((Path('ops/cloud/runtime')/volume['source']).resolve())
  if isinstance(volume,str):
   pass
# String binds for edge/alertmanager are rewritten separately below.
for service in config['services'].values():
 for index,volume in enumerate(service.get('volumes',[])):
  if isinstance(volume,str) and volume.startswith('./'):
   source,*tail=volume.split(':');service['volumes'][index]=str((Path('ops/cloud/runtime')/source).resolve())+':'+':'.join(tail)
p.write_text(json.dumps(config,indent=2)+'\n')
PY
compose() { docker compose --env-file /etc/wallet/runtime.env -f /etc/wallet/compose.release.json "$@"; }
# Maintenance window protects schema compatibility and synthetic seeding.
touch /etc/wallet/deployment-in-progress
trap 'status=$?; if [ "$status" -ne 0 ]; then compose stop edge api worker 2>/dev/null || true; fi' EXIT
compose stop edge api worker 2>/dev/null || true
compose up -d --wait --wait-timeout 240 postgres redis rabbitmq collector prometheus grafana alertmanager
compose run --rm --no-deps migrate
compose run --rm --no-deps migrate --verify
compose run --rm --no-deps migrate --seed-synthetic-demo
compose up -d --no-deps --wait --wait-timeout 240 api worker admin-web edge
compose run --rm --no-deps migrate --reconcile
install -m 0644 /etc/wallet/compose.release.json /etc/wallet/compose.active.json
rm -f /etc/wallet/deployment-in-progress
# Promote only after a verified HTTPS smoke from the approved client machine; failed migration leaves runtime stopped.
echo 'Migration and reconciliation passed. External UI smoke is still required before demo acceptance.'
