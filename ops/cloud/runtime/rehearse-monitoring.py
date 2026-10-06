#!/usr/bin/env python3
"""Local derived-image regression. Loopback only; empty alert receiver; no external notifications."""
import base64,http.client,json,subprocess,time,urllib.error,urllib.request
from pathlib import Path
root=Path(__file__).resolve().parents[3]
values=dict(line.split('=',1) for line in (root/'.env').read_text().splitlines())
password=(root/'.local/secrets/grafana_password').read_text().strip()
auth='Basic '+base64.b64encode(('wallet_observer:'+password).encode()).decode()
def request(port,path,body=None,authenticated=False):
    headers={'Authorization':auth} if authenticated else {}
    if body is not None:headers['Content-Type']='application/json'
    req=urllib.request.Request('http://127.0.0.1:'+str(port)+path,data=json.dumps(body).encode() if body is not None else None,headers=headers)
    with urllib.request.urlopen(req,timeout=5) as response:return json.load(response)
containers=[]
try:
    subprocess.run(['docker','run','-d','--name','wallet-phase14-grafana','--network','digital-wallet_public','--read-only','--cap-drop=ALL','--security-opt=no-new-privileges:true','--memory=256m','--user','472:472','-p','127.0.0.1:3001:3000','--tmpfs','/tmp:uid=472,gid=472,size=16m','--tmpfs','/var/lib/grafana:uid=472,gid=472,size=64m','-v',str(root/'.local/secrets/grafana_password')+':/run/secrets/grafana_password:ro','-v',str(root/'ops/observability/provisioning')+':/etc/grafana/provisioning:ro','-v',str(root/'ops/observability/provisioning/resolved-dashboards')+':/var/lib/grafana/dashboards:ro','-e','GF_SECURITY_ADMIN_USER=wallet_observer','-e','GF_SECURITY_ADMIN_PASSWORD__FILE=/run/secrets/grafana_password','-e','GF_AUTH_ANONYMOUS_ENABLED=false','-e','GF_USERS_ALLOW_SIGN_UP=false','-e','GF_ANALYTICS_REPORTING_ENABLED=false','-e','GF_ANALYTICS_CHECK_FOR_UPDATES=false','-e','GF_PLUGINS_CHECK_FOR_UPDATES=false','-e','GF_PLUGINS_PREINSTALL_DISABLED=true','digitalwallet/cloud-grafana:'+values['VERSION']],check=True,stdout=subprocess.DEVNULL);containers.append('wallet-phase14-grafana')
    subprocess.run(['docker','network','connect','digital-wallet_operations','wallet-phase14-grafana'],check=True)
    subprocess.run(['docker','run','-d','--name','wallet-phase14-alertmanager','--network','digital-wallet_public','--read-only','--cap-drop=ALL','--security-opt=no-new-privileges:true','--memory=128m','--user','65534:65534','-p','127.0.0.1:9094:9093','--tmpfs','/alertmanager:uid=65534,gid=65534,size=16m','-v',str(root/'ops/cloud/runtime/alertmanager.yaml')+':/etc/alertmanager/config.yml:ro','digitalwallet/cloud-alertmanager:'+values['VERSION'],'--config.file=/etc/alertmanager/config.yml','--storage.path=/alertmanager','--cluster.listen-address=','--data.retention=24h'],check=True,stdout=subprocess.DEVNULL);containers.append('wallet-phase14-alertmanager')
    subprocess.run(['docker','network','connect','digital-wallet_operations','wallet-phase14-alertmanager'],check=True)
    for i in range(90):
        try:
            health=request(3001,'/api/health');assert health['database']=='ok';request(9094,'/api/v2/status');break
        except (urllib.error.URLError,http.client.HTTPException,AssertionError):
            if i==89:raise
            time.sleep(2)
    datasource=request(3001,'/api/datasources/uid/wallet-prometheus/health',authenticated=True);assert datasource['status']=='OK',datasource
    plugins=request(3001,'/api/plugins',authenticated=True)
    prometheus=next(p for p in plugins if p['id']=='prometheus')
    assert prometheus['signature'] in ['valid','internal'],prometheus['signature']
    dashboards=request(3001,'/api/search?type=dash-db',authenticated=True);assert len(dashboards)==5,len(dashboards)
    # The configured receiver has no webhook/email/integration. This proves local ingestion only.
    try:request(9094,'/api/v2/alerts',[{'labels':{'alertname':'WalletPhase14LocalProbe','environment':'synthetic-only'},'annotations':{'summary':'Local ingestion rehearsal; no external receiver'}}])
    except json.JSONDecodeError:pass
    alerts=request(9094,'/api/v2/alerts');assert any(a['labels']['alertname']=='WalletPhase14LocalProbe' for a in alerts)
    result={'status':'passed','scope':'local Docker derived images; not Cloud or external alert delivery','grafanaVersion':health['version'],'dashboardCount':len(dashboards),'signedPrometheusPluginHealth':datasource['status'],'prometheusPluginSignature':prometheus['signature'],'alertmanagerIngestion':'passed','externalNotificationsSent':0,'externalDelivery':'Skipped'}
    (root/'ops/validation/phase14/monitoring-local.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
finally:
    for name in containers:subprocess.run(['docker','rm','-f',name],check=False,stdout=subprocess.DEVNULL)
