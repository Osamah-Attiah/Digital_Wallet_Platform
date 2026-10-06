#!/usr/bin/env python3
"""Fail closed. Scan tool errors and expired exceptions fail the gate; no automatic retry."""
import datetime, json, os, subprocess, tempfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];os.chdir(ROOT)
OUT=ROOT/os.environ.get('WALLET_EVIDENCE_DIR','ops/validation/phase13');OUT.mkdir(parents=True,exist_ok=True)
values=dict(line.split('=',1) for line in Path('.env').read_text().splitlines());version=values['VERSION']
TOOLS={
 'trivy':'aquasec/trivy:0.67.2@sha256:e2b22eac59c02003d8749f5b8d9bd073b62e30fefaef5b7c8371204e0a4b0c08',
 'syft':'anchore/syft:v1.33.0@sha256:f94e5d9fce1f2278491a8e3a63bd5f6ddb81fdfdbb8bf7a1637565c1d5344357',
 'gitleaks':'ghcr.io/gitleaks/gitleaks:v8.28.0@sha256:cdbb7c955abce02001a9f6c9f602fb195b7fadc1e812065883f695d1eeaba854',
 'hadolint':'hadolint/hadolint:v2.14.0@sha256:27086352fd5e1907ea2b934eb1023f217c5ae087992eb59fde121dce9c9ff21e'}
exceptions=json.loads(Path('ops/security/vulnerability-allowlist.json').read_text())['exceptions']
for item in exceptions:
 assert item['severity']=='HIGH' and item['justification'] and item['owner']
 assert 0<(datetime.date.fromisoformat(item['expires'])-datetime.date.fromisoformat(item['created'])).days<=30
 assert datetime.date.fromisoformat(item['expires'])>=datetime.date.today()
 assert (datetime.date.fromisoformat(item['expires'])-datetime.date.today()).days<=30
failures=[];results=[]
def process(args,output=None,input=None):
 p=subprocess.run(args,input=input,text=True,capture_output=True)
 if output:(OUT/output).write_text(p.stdout)
 return p

def docker(tool,args,socket=False,input=None):
 command=['docker','run','--rm']
 if input is not None:command+=['-i']
 command+=['-v',str(ROOT)+':/repo:ro','-v',str(OUT)+':/output']
 if tool=='trivy':command+=['-v',str(ROOT/'.local/trivy-cache')+':/root/.cache/trivy']
 if socket:command+=['-v','/var/run/docker.sock:/var/run/docker.sock']
 return process(command+[TOOLS[tool]]+args,input=input)

def policy(report,image):
 violations=[]
 for result in report.get('Results',[]):
  for item in result.get('Vulnerabilities',[]):
   if item['Severity']=='CRITICAL':violations.append(item['VulnerabilityID'])
   elif item['Severity']=='HIGH' and not any(e['id']==item['VulnerabilityID'] and e['image']==image for e in exceptions):violations.append(item['VulnerabilityID'])
 return violations

# Scan only tracked/trackable project inputs; keep private secrets, exports and build caches out of reports.
with tempfile.TemporaryDirectory() as stage:
 import shutil
 staging=Path(stage)
 paths=subprocess.check_output(['git','ls-files','--cached','--others','--exclude-standard','-z'],text=True).split('\0')
 for name in paths:
  if not name or name.startswith(('ops/validation/','.git/')):continue
  path=ROOT/name
  if path.is_file():dest=staging/name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(path,dest)
 p=process(['docker','run','--rm','-v',stage+':/repo:ro','-v',str(OUT)+':/output',TOOLS['gitleaks'],'dir','/repo','--config','/repo/.gitleaks.toml','--redact=100','--report-format','json','--report-path','/output/repository-secrets.json'])
 results.append({'scan':'repository-secrets','exitCode':p.returncode})
 if p.returncode:failures.append('repository-secrets')
for path in Path('ops/containers').glob('*.Dockerfile'):
 p=docker('hadolint',['/bin/hadolint','--failure-threshold','warning','-'],input=path.read_text())
 (OUT/(path.stem+'.dockerfile-lint.txt')).write_text(p.stdout+p.stderr)
 if p.returncode:failures.append('dockerfile-'+path.stem)
for name in ['api','worker','migrator','admin']:
 image='digitalwallet/'+name+':'+version
 p=docker('syft',[image,'-o','spdx-json=/output/'+name+'.sbom.spdx.json','-o','syft-json=/output/'+name+'.inventory.json'],socket=True)
 if p.returncode:failures.append('sbom-'+name);continue
 inventory=json.loads((OUT/(name+'.inventory.json')).read_text())
 if name=='admin':
  # npm's omit-dev graph can also omit production packages reused as dev peers (React).
  # Generate the complete locked graph, then retain exactly the production lock entries.
  frontend=process(['npm','sbom','--prefix','apps/admin','--sbom-format','spdx','--package-lock-only'])
  if frontend.returncode:failures.append('frontend-sbom');continue
  front=json.loads(frontend.stdout);lock=json.loads(Path('apps/admin/package-lock.json').read_text())
  production={path for path,entry in lock['packages'].items() if not entry.get('dev',False)}
  front['packages']=[p for p in front['packages'] if p.get('packageFileName','') in production]
  ids={p['SPDXID'] for p in front['packages']}|{'SPDXRef-DOCUMENT'}
  front['relationships']=[r for r in front['relationships'] if r['spdxElementId'] in ids and r['relatedSpdxElement'] in ids]
  assert {p.get('packageFileName','') for p in front['packages']}==production
  assert set(lock['packages']['']['dependencies']).issubset({p['name'] for p in front['packages']})
  (OUT/'admin.frontend.spdx.json').write_text(json.dumps(front,indent=2)+'\n')
  merged=json.loads((OUT/'admin.sbom.spdx.json').read_text())
  def rename(value):
   if isinstance(value,str) and value.startswith('SPDXRef-') and value!='SPDXRef-DOCUMENT':return 'SPDXRef-Frontend-'+value[8:]
   if isinstance(value,list):return [rename(v) for v in value]
   if isinstance(value,dict):return {k:rename(v) for k,v in value.items()}
   return value
  front=rename(front)
  merged['packages'].extend(front.get('packages',[]));merged['relationships'].extend(front.get('relationships',[]))
  (OUT/'admin.sbom.spdx.json').write_text(json.dumps(merged,indent=2)+'\n')
  inventory['artifacts'].extend({'name':p['name'],'version':p.get('versionInfo',''),'licenses':[{'value':p.get('licenseDeclared','NOASSERTION')}]} for p in front.get('packages',[]))
 licenses=[{'name':i['name'],'version':i['version'],'licenses':[x.get('value','NOASSERTION') for x in i.get('licenses',[])]} for i in inventory.get('artifacts',[])]
 (OUT/(name+'.licenses.json')).write_text(json.dumps(licenses,indent=2)+'\n')
 p=docker('trivy',['image','--scanners','vuln,secret','--format','json','--output','/output/'+name+'.scan.json',image],socket=True)
 if p.returncode:failures.append('container-scan-'+name);continue
 report=json.loads((OUT/(name+'.scan.json')).read_text());violations=policy(report,name)
 if any(r.get('Secrets') for r in report.get('Results',[])):violations.append('image-layer-secret')
 results.append({'scan':name,'policyViolations':sorted(set(violations)),'sbom':True})
 if violations:failures.append('image-policy-'+name)
p=process(['dotnet','list','Digital_Wallet_Platform.sln','package','--vulnerable','--include-transitive','--format','json'],output='nuget-audit.json')
if p.returncode:failures.append('nuget-scan-tool')
else:
 report=json.loads(p.stdout)
 for project in report.get('projects',[]):
  for framework in project.get('frameworks',[]):
   for package in framework.get('topLevelPackages',[])+framework.get('transitivePackages',[]):
    if any(v['severity'].lower() in ['critical','high'] for v in package.get('vulnerabilities',[])):failures.append('nuget-high-critical')
p=process(['npm','audit','--prefix','apps/admin','--json'],output='npm-audit.json')
try:
 audit=json.loads(p.stdout);v=audit['metadata']['vulnerabilities']
 if v['critical'] or v['high']:failures.append('npm-high-critical')
except Exception:failures.append('npm-scan-tool')
process(['python3','ops/scripts/image-check.py']).check_returncode()
summary={'status':'failed' if failures else 'passed','failures':sorted(set(failures)),'results':results,'exceptions':len(exceptions),'criticalAllowed':False}
(OUT/'security-summary.json').write_text(json.dumps(summary,indent=2)+'\n');print(json.dumps(summary));raise SystemExit(1 if failures else 0)
