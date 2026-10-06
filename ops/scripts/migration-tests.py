#!/usr/bin/env python3
import concurrent.futures,json,os,subprocess,uuid
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];os.chdir(ROOT);checks=[]
def run(args,input=None,expect=True):
 p=subprocess.run(['docker','compose',*args],input=input,text=True,capture_output=True)
 if expect and p.returncode:raise RuntimeError('Migration validation operation failed: '+args[0])
 return p

def migrate(extra=[]):
 p=run(['run','--rm','--no-deps',*extra,'migrate']);lines=[line for line in p.stdout.splitlines() if line.startswith('{')];manifest=json.loads(lines[-1]);assert manifest['status']=='verified' and len(manifest['contexts'])==13;return manifest

def bootstrap(sql):return run(['exec','-T','postgres','psql','-U','wallet_bootstrap','-d','digital_wallet','-v','ON_ERROR_STOP=1','-At'],sql)
status='failed'
try:
 first=migrate();second=migrate();assert first['contexts']==second['contexts'];checks.append('migrator-twice-idempotent')
 with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
  manifests=list(pool.map(lambda _:migrate(),range(2)))
 assert all(m['contexts']==first['contexts'] for m in manifests);checks.append('two-concurrent-migrators-global-lock')
 # Freeze the phase-12 database (all 13 last migration IDs predate phase 13; phase 13 adds no DDL).
 # pg_dump contains the real schema/seed data, not a TestServer database or a hand-written approximation.
 dump=run(['exec','-T','postgres','pg_dump','-U','wallet_bootstrap','-d','digital_wallet','--no-owner','--no-privileges']).stdout
 database='phase13_upgrade_'+uuid.uuid4().hex
 bootstrap('CREATE DATABASE '+database+' OWNER wallet_migrator;')
 try:
  run(['exec','-T','postgres','psql','-U','wallet_bootstrap','-d',database,'-v','ON_ERROR_STOP=1'], 'SET ROLE wallet_migrator;\n'+dump)
  upgraded=migrate(['-e','Migration__Database='+database]);assert upgraded['contexts']==first['contexts'];checks.append('phase12-schema-and-data-snapshot-upgrade')
 finally:
  bootstrap('DROP DATABASE '+database+' WITH (FORCE);')
 # Validate first boot from an empty volume under an isolated project, without touching local data.
 project='wallet-migration-'+uuid.uuid4().hex[:10]
 try:
  run(['-p',project,'up','-d','postgres'])
  run(['-p',project,'run','--rm','migrate']);checks.append('fresh-empty-database-bootstrap-and-migration')
  # An invalid migration target must stop runtime dependencies from starting.
  override=ROOT/'.local/migration-failure.yaml';override.write_text('services:\n  migrate:\n    environment:\n      Migration__Database: database_does_not_exist\n  api:\n    ports: !reset []\n')
  p=run(['-p',project,'-f','compose.yaml','-f',str(override),'up','-d','--force-recreate','api','worker'],expect=False);assert p.returncode
  api=run(['-p',project,'ps','--status','running','-q','api']).stdout.strip();assert not api;assert not run(['-p',project,'ps','--status','running','-q','worker']).stdout.strip();checks.append('migration-failure-blocks-api-and-worker-startup')
 finally:
  # Keep validation named volumes for inspection. Destructive cleanup is always an explicit separate action.
  run(['-p',project,'down','--remove-orphans'],expect=False)
 status='passed'
except Exception as error:
 checks.append(str(error)[:180])
result={'status':status,'checks':checks,'passed':len(checks) if status=='passed' else len(checks)-1,'failed':0 if status=='passed' else 1,'skipped':0}
(ROOT/os.environ.get('WALLET_EVIDENCE_DIR','ops/validation/phase13')/'migrations.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result));raise SystemExit(0 if status=='passed' else 1)
