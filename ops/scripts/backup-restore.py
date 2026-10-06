#!/usr/bin/env python3
"""Local rehearsal only: consistent pg_dump, isolated restore, the real Migrator's reconciliation."""
import hashlib,json,subprocess,uuid,os
from pathlib import Path
root=Path(__file__).resolve().parents[2];os.chdir(root)
out=root/'ops/validation/phase14';out.mkdir(parents=True,exist_ok=True)
folder=root/'.local/backups';folder.mkdir(mode=0o700,exist_ok=True);folder.chmod(0o700)
database='phase14_restore_'+uuid.uuid4().hex
checks=[];status='failed';dump=folder/(database+'.dump')
def run(args,input=None):
 p=subprocess.run(['docker','compose',*args],input=input,capture_output=True)
 if p.returncode:raise RuntimeError('Backup/restore operation failed: '+args[0])
 return p.stdout

def sql(query):return run(['exec','-T','postgres','psql','-U','wallet_bootstrap','-d','digital_wallet','-v','ON_ERROR_STOP=1','-At'],query.encode())
created=False
try:
 run(['stop','api','worker'])
 data=run(['exec','-T','postgres','pg_dump','-U','wallet_bootstrap','-d','digital_wallet','--format=custom','--no-owner','--no-privileges'])
 with dump.open('wb') as stream:stream.write(data)
 dump.chmod(0o600);checks.append('consistent-custom-format-backup')
 sql('CREATE DATABASE '+database+' OWNER wallet_migrator; REVOKE ALL ON DATABASE '+database+' FROM PUBLIC;');created=True
 run(['exec','-T','postgres','pg_restore','-U','wallet_bootstrap','--role=wallet_migrator','-d',database,'--exit-on-error','--no-owner','--no-privileges'],data)
 checks.append('restore-into-isolated-database-with-no-api-worker-connect')
 output=run(['run','--rm','--no-deps','-e','Migration__Database='+database,'migrate','--reconcile']).decode()
 manifests=[json.loads(line) for line in output.splitlines() if line.startswith('{')]
 manifest=next(x for x in manifests if x.get('status')=='verified')
 reconciliation=next(x for x in manifests if 'reconciliationFindings' in x)
 assert len(manifest['contexts'])==13 and all(c['pending']==0 for c in manifest['contexts'])
 assert reconciliation['reconciliationFindings']==0
 checks+=['all-13-contexts-no-pending-on-restored-database','real-ledger-reconciliation-zero']
 status='passed'
finally:
 if created:sql('DROP DATABASE '+database+' WITH (FORCE);')
 run(['start','api','worker'])
 result={'status':status,'scope':'local Docker synthetic data; not a Cloud backup/restore acceptance','passed':len(checks),'checks':checks,'backupSha256':hashlib.file_digest(dump.open('rb'),'sha256').hexdigest() if dump.exists() else None,'rawBackupCommitted':False,'restoreIsolated':True,'reconciliationFindings':0 if status=='passed' else None,'restoredDatabaseDropped':created}
 (out/'backup-restore-local.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
raise SystemExit(0 if status=='passed' else 1)
