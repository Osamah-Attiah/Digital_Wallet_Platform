#!/usr/bin/env python3
import json,os,re,subprocess
assert os.environ['APPROVED']=='true'
run_id=os.environ['RELEASE_RUN_ID'];assert re.fullmatch('[0-9]+',run_id)
assert re.fullmatch('[a-f0-9]{64}',os.environ['MANIFEST_SHA'])
repository=os.environ['REPOSITORY'];assert re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+',repository)
run=json.loads(subprocess.check_output(['gh','api','repos/'+repository+'/actions/runs/'+run_id]))
assert run['path']=='.github/workflows/cloud-package.yml' and run['event']=='workflow_dispatch' and run['head_branch']=='main' and run['conclusion']=='success'
# Deployment must use the same reviewed source revision as this workflow checkout.
assert run['head_sha']==subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip()
print('Approved successful package run on the exact current main revision verified.')
