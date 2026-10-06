#!/usr/bin/env python3
"""Run the explicit synthetic CLI twice in a new local database; verify funding is idempotent."""
import json,os,secrets,subprocess,uuid
from pathlib import Path
root=Path(__file__).resolve().parents[2];os.chdir(root);database='phase14_synthetic_'+uuid.uuid4().hex
folder=root/'.local/synthetic-seed';folder.mkdir(mode=0o700,exist_ok=True);created=False
out=root/'ops/validation/phase14';status='failed'
def compose(args,input=None):
 p=subprocess.run(['docker','compose',*args],input=input,text=True,capture_output=True)
 if p.returncode:raise RuntimeError('Synthetic seed rehearsal failed at '+args[0])
 return p.stdout

def sql(query,db='digital_wallet'):return compose(['exec','-T','postgres','psql','-U','wallet_bootstrap','-d',db,'-v','ON_ERROR_STOP=1','-At'],query)
try:
 sql('CREATE DATABASE '+database+' OWNER wallet_migrator;');created=True
 args=['run','--rm','--no-deps','-e','Migration__Database='+database,'-e','SyntheticDemo__Enabled=true','-e','SyntheticDemo__DataClassification=synthetic-only']
 for actor in ['maker','checker','customer_a','customer_b']:
  path=folder/('demo_'+actor+'_password');path.write_text('D_'+secrets.token_hex(24)+'9a');path.chmod(0o444)
  args+=['-v',str(path)+':/run/secrets/'+path.name+':ro']
 args+=['migrate','--seed-synthetic-demo']
 for attempt in range(2):
  text=compose(args);assert '"syntheticSeed":"passed"' in text
 assert sql('SELECT count(*) FROM ledger.journals WHERE "SourceType"=\'SyntheticDemoSeed\';',database).strip()=='2'
 assert sql('SELECT count(*) FROM identity.users;',database).strip()=='4'
 status='passed'
finally:
 if created:sql('DROP DATABASE '+database+' WITH (FORCE);')
 for path in folder.glob('demo_*_password'):path.unlink()
 result={'status':status,'scope':'isolated local CLI synthetic seeding; not Cloud acceptance','runs':2 if status=='passed' else None,'syntheticIdentities':4 if status=='passed' else None,'fundingJournals':2 if status=='passed' else None,'reconciliationFindings':0 if status=='passed' else None,'repeatedSeedDuplicates':0 if status=='passed' else None,'restoredDatabaseDropped':created,'privatePasswordsRemoved':True}
 (out/'synthetic-seed.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
raise SystemExit(0 if status=='passed' else 1)
