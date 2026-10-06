#!/usr/bin/env python3
import hashlib,json,subprocess
from pathlib import Path
values=dict(line.split('=',1) for line in Path('.env').read_text().splitlines());version=values['VERSION']
folder=Path('.local/release');folder.mkdir(parents=True,exist_ok=True)
for service,file in [('api','api'),('worker','worker'),('migrator','migrator'),('admin','admin')]:
    image='digitalwallet/'+service+':'+version
    args=['docker','buildx','build','--provenance=mode=min','--sbom=true','--output','type=oci,dest='+str(folder/(service+'.oci.tar')),'--metadata-file',str(folder/(service+'.provenance.json')),'-t',image,'-f','ops/containers/'+file+'.Dockerfile']
    for key in ['VERSION','GIT_SHA','BUILD_DATE']:args+=['--build-arg',key+'='+values[key]]
    subprocess.run(args+['.'],check=True)
    # skopeo transports the already-built OCI image to the daemon for scanning, without rebuilding.
    subprocess.run(['sudo','apt-get','update','-qq'],check=True) if service=='api' else None
    subprocess.run(['sudo','apt-get','install','-y','skopeo'],check=True) if service=='api' else None
    subprocess.run(['skopeo','copy','oci-archive:'+str(folder/(service+'.oci.tar')),'docker-daemon:'+image],check=True)
    subprocess.run(['docker','save','-o',str(folder/(service+'.docker.tar')),image],check=True)
checksums=[]
for p in sorted(folder.glob('*')):
    if p.is_file():checksums.append(hashlib.file_digest(p.open('rb'),'sha256').hexdigest()+'  '+p.name)
(folder/'SHA256SUMS').write_text('\n'.join(checksums)+'\n')
