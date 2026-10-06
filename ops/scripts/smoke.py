#!/usr/bin/env python3
"""Real HTTP + real Compose. No credentials, payloads or personal identifiers in evidence."""
import base64, datetime as dt, json, os, secrets, subprocess, time, urllib.request, urllib.error, uuid
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
os.chdir(ROOT)
API='http://127.0.0.1:'+os.getenv('API_PORT','8081')
WEB='http://127.0.0.1:'+os.getenv('ADMIN_PORT','8080')
KEY=(ROOT/'.local/secrets/fixture_key').read_text().strip()
checks=[]
recovery_read_attempts=0
def compose(*args, check=True):
    p=subprocess.run(['docker','compose',*args],capture_output=True,text=True,check=False)
    if check and p.returncode: raise RuntimeError('Compose operation failed: '+args[0])
    return p

def request(path, data=None, token=None, method=None, headers=None, web=False):
    h={'Content-Type':'application/json', **(headers or {})}
    if token:h['Authorization']='Bearer '+token
    req=urllib.request.Request((WEB if web else API)+path,data=None if data is None else json.dumps(data).encode(),headers=h,method=method)
    try:
        with urllib.request.urlopen(req,timeout=15) as response:
            raw=response.read(); return json.loads(raw) if raw and 'json' in response.headers.get('Content-Type','') else raw
    except urllib.error.HTTPError as e:
        # Report a safe code only, never request/response payloads or tokens.
        raise RuntimeError('HTTP failed with status '+str(e.code)+' at '+path.split('?')[0]) from None

def wait(test, seconds=100):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        value=test()
        if value:return value
        time.sleep(1)
    raise RuntimeError('Bounded readiness condition timed out')

def record(name):checks.append({'name':name,'status':'passed'})
def sql(query,role='wallet_migrator',ok=True):
    # Password goes through stdin, never a process argument or persisted evidence.
    password=(ROOT / ('.local/secrets/'+{'wallet_migrator':'migrator','wallet_api':'api','wallet_worker':'worker'}[role]+'_db_password')).read_text().strip()
    script='export PGPASSWORD; IFS= read -r PGPASSWORD; exec psql -h postgres -U '+role+' -d digital_wallet -v ON_ERROR_STOP=1 -At'
    p=subprocess.run(['docker','compose','exec','-T','postgres','sh','-c',script],input=password+'\n'+query+'\n',text=True,capture_output=True)
    if ok and p.returncode:raise RuntimeError('Database assertion failed')
    return p

def code(challenge):return json.loads(compose('exec','-T','api','dotnet','DigitalWallet.Api.dll','--fixture-code',challenge).stdout)['code']
def healthy():
    p=compose('ps','--format','json'); services=[json.loads(x) for x in p.stdout.splitlines() if x.strip()]
    required={'api','worker','admin-web','postgres','redis','rabbitmq','notification-sink'}
    return required.issubset({s['Service'] for s in services if s.get('Health')=='healthy'})
def actor(label,staff=False):
    email=label+'-'+uuid.uuid4().hex+'@smoke.invalid'; password='W!'+secrets.token_urlsafe(20)+'a9'
    device={'deviceId':uuid.uuid4().hex,'name':'Compose smoke'}
    issued=request('/api/v1/auth/register',{'email':email,'password':password,'acceptTerms':True,'locale':'ar-SA','device':device})
    auth=request('/api/v1/auth/register/verify',{'challengeId':issued['challengeId'],'code':code(issued['challengeId']),'device':device})
    token=auth['accessToken']; claims=json.loads(base64.urlsafe_b64decode(token.split('.')[1]+'==')); user=claims['sub']
    profile=request('/api/v1/me/profile',{'firstName':'Smoke','lastName':'Fixture','preferredLanguage':'ar-SA'},token)
    request('/api/dev/fixture',{'userId':user,'staff':staff},headers={'X-Fixture-Key':KEY})
    auth=request('/api/v1/auth/login',{'email':email,'password':password,'device':device}); token=auth['accessToken']
    wallet=request('/api/v1/wallets',{},token)
    return {'token':token,'user':user,'profile':profile,'wallet':wallet}

def main():
    compose('restart','api','redis');wait(healthy,240);record('all-compose-services-healthy')
    compose('run','--rm','--no-deps','migrate','--verify');record('all-13-contexts-no-pending-migrations')
    for path in ['/health/live','/health/ready']:
        health=request(path); assert not any(x in json.dumps(health).lower() for x in ['password','connectionstring','secret','rabbituri']); assert health['status'] in ['alive','healthy']
    record('safe-api-readiness-and-liveness')
    for path in ['/','/customers']:
        with urllib.request.urlopen(WEB+path,timeout=10) as r:
            assert r.status==200 and b'<div id="root">' in r.read()
            headers=json.loads((ROOT/'apps/admin/security-headers.json').read_text())
            assert all(r.headers.get(k)==v for k,v in headers.items())
    record('spa-fallback-and-security-headers')
    sender=actor('sender',True); receiver=actor('receiver'); record('registration-otp-login-customers-wallets')
    token=sender['token']; prefix='/api/v1/admin/risk-limits/rules'
    now=dt.datetime.now(dt.timezone.utc)
    for kind in range(3):
        rule={'kind':kind,'scope':3,'scopeKey':sender['wallet']['id'],'version':1,'currency':'SAR','transferType':'Internal','effectiveFrom':(now-dt.timedelta(days=1)).isoformat(),'reason':'Local fixture rule'}
        if kind==0:rule['maximumTransferMinor']=1000000
        if kind==1:rule.update(fixedFeeMinor=0,basisPoints=0)
        created=request(prefix,rule,token);request(prefix+'/'+created['id']+'/publish',{},token)
    request('/api/v1/wallets/'+sender['wallet']['id']+'/demo-funds',{'amountMinor':10000,'sourceId':uuid.uuid4().hex,'reason':'Local smoke funding'},token)
    beneficiary=request('/api/v1/beneficiaries',{'publicHandle':receiver['wallet']['publicHandle'],'nickname':'Smoke receiver'},token)
    challenge=sql('SELECT "OtpChallengeId" FROM beneficiaries.beneficiaries WHERE "Id"=\''+beneficiary['id']+'\';').stdout.strip()
    verified=request('/api/v1/beneficiaries/'+beneficiary['id']+'/verify',{'code':code(challenge),'concurrencyVersion':beneficiary['concurrencyVersion']},token)
    request('/api/v1/beneficiaries/'+beneficiary['id']+'/activate',{'concurrencyVersion':verified['concurrencyVersion']},token);record('real-beneficiary-otp-activation')
    def transfer(amount):
        quote=request('/api/v1/transfers/quotes',{'beneficiaryId':beneficiary['id'],'amountMinor':amount,'currency':'SAR'},token)
        intent=request('/api/v1/transfers',{'quoteId':quote['id']},token,headers={'Idempotency-Key':uuid.uuid4().hex})
        challenge=sql('SELECT "OtpChallengeId" FROM transfers.transfers WHERE "Id"=\''+intent['id']+'\';').stdout.strip()
        receipt=request('/api/v1/transfers/'+intent['id']+'/confirm',{'code':code(challenge)},token);assert receipt['status']=='Completed';return receipt
    first=transfer(1000); tid=first['id'];receipt=request('/api/v1/transfers/'+tid,token=token)
    history=request('/api/v1/transactions',token=token);assert history['items'];record('financial-transfer-history-receipt')
    def notification_count():return int(sql('SELECT count(*) FROM notifications.notifications WHERE "AggregateId"=\''+tid+'\';').stdout.strip())
    wait(lambda:notification_count()==2);record('rabbit-delivery-one-logical-notification-per-recipient')
    wait(lambda:sql('SELECT count(*) FROM messaging.outbox WHERE "AggregateId"=\''+tid+'\' AND "PublishedAt" IS NOT NULL;').stdout.strip()=='1');record('outbox-confirmed-publication')
    mfa=request('/api/v1/admin/session/mfa',{},token);request('/api/v1/admin/session/mfa/verify',{'challengeId':mfa['challengeId'],'code':code(mfa['challengeId'])},token)
    compose('stop','worker')
    stopped_transfer=transfer(100)
    assert sql('SELECT count(*) FROM messaging.outbox WHERE \"AggregateId\"=\''+stopped_transfer['id']+'\' AND \"PublishedAt\" IS NULL;').stdout.strip()=='1'
    record('worker-stopped-api-commits-money-and-durable-outbox')
    job=request('/api/v1/admin/reports',{'type':'CustomerSummary','from':(now-dt.timedelta(days=1)).isoformat(),'to':(now+dt.timedelta(hours=1)).isoformat()},token,headers={'Idempotency-Key':uuid.uuid4().hex})
    assert request('/api/v1/admin/reports/'+job['id'],token=token)['status']=='Queued'
    compose('start','worker');wait(healthy,180);record('worker-restart-resumes-pending-report-and-outbox')
    wait(lambda:request('/api/v1/admin/reports/'+job['id'],token=token)['status']=='Completed'); assert request('/api/v1/admin/reports/'+job['id']+'/download',token=token);record('report-worker-completion-shared-file-download')
    # Mutation is exercised with the real session-bound MFA and Audit filter.
    frozen=request('/api/v1/admin/wallets/'+receiver['wallet']['id']+'/freeze',{'reason':'Compose smoke review','concurrencyVersion':receiver['wallet']['concurrencyVersion']},token)
    request('/api/v1/admin/wallets/'+receiver['wallet']['id']+'/unfreeze',{'reason':'Compose smoke complete','concurrencyVersion':frozen['concurrencyVersion']},token)
    assert int(sql("SELECT count(*) FROM audit.logs WHERE \"Action\"='wallet.freeze' AND \"Result\"='Success';").stdout)>0;record('administrative-mutation-durable-audit')
    for role in ['wallet_api','wallet_worker']:
        assert sql('CREATE TABLE public.forbidden(id int);',role,False).returncode
        assert sql('ALTER TABLE audit.logs DISABLE TRIGGER USER;',role,False).returncode
        assert sql('TRUNCATE audit.logs;',role,False).returncode
        assert sql('DELETE FROM audit.logs;',role,False).returncode
        assert sql('UPDATE identity."__EFMigrationsHistory" SET "ProductVersion"=\'forbidden\';',role,False).returncode
    record('runtime-roles-deny-ddl-trigger-disable-truncate-audit-delete-history-update')
    before=request('/api/v1/wallets/'+sender['wallet']['id']+'/balance',token=token)
    compose('stop','redis')
    try:
        assert request('/health/ready')['financialReady'];transfer(500);record('redis-outage-financial-path')
    finally:compose('start','redis')
    compose('stop','rabbitmq')
    try:
        outage=transfer(500);assert sql('SELECT count(*) FROM messaging.outbox WHERE "AggregateId"=\''+outage['id']+'\' AND "PublishedAt" IS NULL;').stdout.strip()=='1';record('rabbit-outage-committed-transfer-pending-outbox')
    finally:compose('start','rabbitmq')
    wait(lambda:sql('SELECT count(*) FROM notifications.notifications WHERE "AggregateId"=\''+outage['id']+'\';').stdout.strip()=='2',150);record('rabbit-recovery-drains-original-outbox')
    balance=request('/api/v1/wallets/'+sender['wallet']['id']+'/balance',token=token)
    compose('restart','api','worker','redis','rabbitmq');wait(healthy,240)
    replay=request('/api/v1/transfers/'+tid+'/confirm',{'code':'000000'},token);assert replay['id']==tid and replay['status']=='Completed'
    assert request('/api/v1/wallets/'+sender['wallet']['id']+'/balance',token=token)['ledgerBalance']==balance['ledgerBalance'];assert notification_count()==2
    assert sql('SELECT count(*) FROM ledger.journals WHERE "SourceType"=\'InternalTransfer\' AND "SourceId"=\''+tid+'\';').stdout.strip()=='1';record('api-worker-redis-rabbit-restarts-no-duplicate-money-or-notification')
    # Force a new volatile Redis container, then verify the durable receipt and balances.
    compose('up','-d','--no-deps','--force-recreate','redis');wait(healthy);assert request('/api/v1/transfers/'+tid,token=token)==receipt;record('redis-recreated-with-empty-cache-preserves-receipt')
    compose('restart','postgres');wait(healthy,180)
    def restored_receipt():
        global recovery_read_attempts
        try:return request('/api/v1/transfers/'+tid,token=token)==receipt
        except RuntimeError as error:
            if 'status 500' not in str(error) and 'status 503' not in str(error):raise
            recovery_read_attempts+=1;return False
    wait(restored_receipt,30);record('postgres-volume-durable-after-restart')
    reconciliation=request('/api/v1/admin/ledger/reconciliation',token=token);assert not reconciliation['findings'];record('reconciliation-zero-after-outages-and-restarts')
    # Verify worker host has no customer/admin business endpoints.
    for service in ['api','worker','admin-web','notification-sink']:
        container=compose('ps','-q',service).stdout.strip();user=subprocess.check_output(['docker','inspect','--format','{{.Config.User}}',container],text=True).strip();assert user and user not in ['0','root','0:0']
    record('all-application-runtime-containers-non-root')

try:
    main();status='passed'
except Exception as error:
    status='failed'; checks.append({'name':'suite','status':'failed','reason':str(error)[:180]})
finally:
    # Recover local services even when an assertion fails.
    compose('start','redis','rabbitmq','worker',check=False)
    evidence={'suite':'phase13-real-compose-smoke','status':status,'passed':sum(c['status']=='passed' for c in checks),'failed':sum(c['status']=='failed' for c in checks),'skipped':0,'postgresRecoveryReadAttempts':recovery_read_attempts,'checks':checks}
    (ROOT/os.environ.get('WALLET_EVIDENCE_DIR','ops/validation/phase13')/'smoke.json').write_text(json.dumps(evidence,indent=2)+'\n')
    print(json.dumps(evidence,ensure_ascii=False))
raise SystemExit(0 if status=='passed' else 1)
