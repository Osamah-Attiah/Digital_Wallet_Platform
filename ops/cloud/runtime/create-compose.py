#!/usr/bin/env python3
"""Generate the standalone deployment contract from phase13, with no inherited dev ports/fixtures."""
import json, subprocess
from pathlib import Path
root = Path(__file__).resolve().parents[3]
base = json.loads(subprocess.check_output(['docker','compose','--profile','observability','config','--format','json'],cwd=root,text=True))
services = base['services']
services.pop('notification-sink')
for name, service in services.items():
    service.pop('build', None); service.pop('ports', None); service.pop('profiles', None)
    service.pop('depends_on', None)
    if 'secrets' in service:service['secrets']=[s for s in service['secrets'] if s['source']!='fixture_key']
    service['logging'] = {'driver':'json-file','options':{'max-size':'10m','max-file':'3'}}
    if name in ['api','worker','migrate','admin-web','collector']:
        service['image'] = '${' + {'api':'API','worker':'WORKER','migrate':'MIGRATOR','admin-web':'ADMIN','collector':'COLLECTOR'}[name] + '_IMAGE:?Reviewed digest required}'
    for volume in service.get('volumes',[]):
        if volume['type']=='bind':
            source=Path(volume['source']).relative_to(root)
            volume['source']='../../../'+str(source)
    if name in ['api','worker','migrate']:
        env=service['environment']
        for key in list(env):
            if key.startswith(('DevelopmentFixtures','Beneficiaries__CoolingPeriod')):del env[key]
        env.update(ASPNETCORE_ENVIRONMENT='Production',Hosting__DependencyTls='true',AllowedHosts='${DEMO_HOST};api;worker;127.0.0.1',Caching__Environment='production',HttpSecurity__AdminOrigins__0='https://${DEMO_HOST}',Observability__ExportEnabled='true',Observability__Endpoint='https://collector:4318',Observability__AllowLocalContainerEndpoint='false',SSL_CERT_FILE='/run/tls/ca.crt',SSL_CERT_DIR='/run/tls',Release__Version='${RELEASE_VERSION}',Release__GitSha='${RELEASE_SHA}')
        env['HttpSecurity__ExternalWorkersDecision']='PHASE14-REVIEWED separate Worker, migration before runtime'
        service['volumes']=service.get('volumes',[])+[{'type':'bind','source':'${WALLET_TLS_DIR}/client','target':'/run/tls','read_only':True}]
        service['networks']={'private':None,'operations':None}
        service['depends_on']={'postgres':{'condition':'service_healthy'}}
        if name!='migrate':service['depends_on']['migrate']={'condition':'service_completed_successfully'}
    if name=='api':
        service['environment'].update(HttpSecurity__KnownProxies__0='172.28.0.3',SyntheticDemo__Enabled='true',SyntheticDemo__DataClassification='synthetic-only')
        service['tmpfs'].append('/var/demo-otp:rw,noexec,nosuid,size=1m,mode=0700,uid=1654,gid=1654')
        service['networks']['ingress']={'ipv4_address':'172.28.0.4'}
    if name=='admin-web':service['networks']={'ingress':None}
    if name in ['postgres','redis','rabbitmq','collector']:
        tlsname='collector' if name=='collector' else name
        service['volumes']=service.get('volumes',[])+[{'type':'bind','source':'${WALLET_TLS_DIR}/'+tlsname,'target':'/run/tls','read_only':True}]
    if name in ['postgres','redis','rabbitmq']:
        for v in service['volumes']:
            if v['target']=='/run-config/'+{'postgres':'postgres.sh','redis':'redis.sh','rabbitmq':'rabbit.sh'}[name]:v['source']='./'+{'postgres':'postgres.sh','redis':'redis.sh','rabbitmq':'rabbit.sh'}[name]
    if name=='postgres':
        service['volumes'].append({'type':'bind','source':'./pg_hba.conf','target':'/run-config/pg_hba.conf','read_only':True})
        service['read_only']=True;service['tmpfs']=['/var/run/postgresql:rw,noexec,nosuid,size=16m,uid=70,gid=70','/tmp:rw,noexec,nosuid,size=16m,uid=70,gid=70']
    if name=='redis':service['healthcheck']['test']=['CMD-SHELL','REDISCLI_AUTH=$$(cat /run/secrets/redis_password) redis-cli --tls --cacert /run/tls/ca.crt -p 6380 --user wallet_cache ping | grep -q PONG']
    if name=='rabbitmq':
        service['read_only']=True
        service['volumes'].append({'type':'bind','source':'./rabbit-definitions.sh','target':'/run-config/rabbit-definitions.sh','read_only':True})
    if name=='collector':
        for v in service['volumes']:
            if v['target']=='/etc/otel/config.yaml':v['source']='./collector.yaml'
    if name=='prometheus':
        service['command']=['--config.file=/etc/prometheus/prometheus.yml','--storage.tsdb.path=/prometheus','--storage.tsdb.retention.time=7d','--storage.tsdb.retention.size=512MB']
        for v in service['volumes']:
            if v['target']=='/etc/prometheus/prometheus.yml':v['source']='./prometheus.yaml'
    if name=='grafana':
        service['environment'].update(GF_ANALYTICS_REPORTING_ENABLED='false',GF_ANALYTICS_CHECK_FOR_UPDATES='false',GF_PLUGINS_CHECK_FOR_UPDATES='false')
        service['ports']=[{'target':3000,'published':'3000','host_ip':'127.0.0.1','protocol':'tcp'}]
        service['networks']={'operations':None,'monitoring-access':None}
services['edge']={'image':'caddy:2.11.2-alpine@sha256:834468128c7696cec0ceea6172f7d692daf645ae51983ca76e39da54a97c570d','user':'1000:1000','environment':{'DEMO_HOST':'${DEMO_HOST}'},'command':['caddy','run','--config','/etc/caddy/Caddyfile'],'cap_drop':['ALL'],'security_opt':['no-new-privileges:true'],'read_only':True,'tmpfs':['/data:rw,noexec,nosuid,uid=1000,gid=1000','/config:rw,noexec,nosuid,uid=1000,gid=1000'],'mem_limit':'128m','cpus':'0.25','pids_limit':128,'restart':'unless-stopped','ports':['443:8443'],'volumes':['./Caddyfile:/etc/caddy/Caddyfile:ro','${WALLET_TLS_DIR}/edge:/run/edge:ro'],'networks':{'ingress':{'ipv4_address':'172.28.0.3'}},'depends_on':{'api':{'condition':'service_healthy'},'admin-web':{'condition':'service_healthy'}}}
services['alertmanager']={'image':'prom/alertmanager:v0.28.1@sha256:27c475db5fb156cab31d5c18a4251ac7ed567746a2483ff264516437a39b15ba','user':'65534:65534','command':['--config.file=/etc/alertmanager/config.yml','--storage.path=/alertmanager','--data.retention=24h'],'cap_drop':['ALL'],'security_opt':['no-new-privileges:true'],'read_only':True,'tmpfs':['/alertmanager:rw,noexec,nosuid,uid=65534,gid=65534'],'mem_limit':'128m','cpus':'0.25','pids_limit':128,'restart':'unless-stopped','volumes':['./alertmanager.yaml:/etc/alertmanager/config.yml:ro'],'networks':{'operations':None}}
for service in services.values():
    service.setdefault('logging',{'driver':'json-file','options':{'max-size':'10m','max-file':'3'}})
    for v in service.get('volumes',[]):
        if isinstance(v,dict) and v['type']=='volume':
            v['type']='bind';v['source']='${WALLET_DATA_DIR}/'+v['source'];v.pop('volume',None)
secrets={k:{'file':'${WALLET_SECRETS_DIR}/'+k} for k in base['secrets'] if k!='fixture_key'}
for name in ['maker','checker','customer_a','customer_b']:
    key='demo_'+name+'_password';secrets[key]={'file':'${WALLET_SECRETS_DIR}/'+key}
    services['migrate']['secrets'].append({'source':key,'target':key})
services['migrate']['environment'].update(SyntheticDemo__Enabled='true',SyntheticDemo__DataClassification='synthetic-only')
for name,size in {'api':'768m','worker':'768m','migrate':'768m','postgres':'768m','redis':'192m','rabbitmq':'512m','admin-web':'128m','collector':'256m','prometheus':'512m','grafana':'256m'}.items():services[name]['mem_limit']=size
networks={'ingress':{'ipam':{'config':[{'subnet':'172.28.0.0/24'}]}},'private':{'internal':True,'ipam':{'config':[{'subnet':'172.29.0.0/24'}]}},'operations':{'internal':True,'ipam':{'config':[{'subnet':'172.30.0.0/24'}]}},'monitoring-access':{'ipam':{'config':[{'subnet':'172.31.0.0/24'}]}}}
lock=json.loads((root/'ops/cloud/runtime/images.lock.json').read_text())
for name in ['rabbitmq','prometheus','edge']:services[name]['image']=lock[name]
for name in ['postgres','redis','grafana','alertmanager']:services[name]['image']='${'+name.upper()+'_IMAGE:?Reviewed digest required}'
(root/'ops/cloud/runtime/compose.yaml').write_text(json.dumps({'name':'wallet-cloud-demo','services':services,'secrets':secrets,'networks':networks},indent=2)+'\n')
print('Standalone production deployment contract generated; no cloud resources accessed.')
