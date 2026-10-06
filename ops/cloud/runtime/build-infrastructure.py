#!/usr/bin/env python3
import hashlib, json, os, subprocess, tarfile
from pathlib import Path
values=dict(line.split('=',1) for line in Path('.env').read_text().splitlines())
folder=Path('.local/cloud-package/infrastructure');folder.mkdir(parents=True,exist_ok=True)
names=os.environ.get('WALLET_INFRA_BUILD_IMAGES','postgres,redis,grafana,alertmanager').split(',')
assert names and set(names)<=set(['postgres','redis','grafana','alertmanager'])
for name in names:
    image='digitalwallet/cloud-'+name+':'+values['VERSION']
    args=['docker','buildx','build','--provenance=mode=max','--sbom=true','--output','type=oci,dest='+str(folder/(name+'.oci.tar')),'--metadata-file',str(folder/(name+'.provenance.json')),'-f','ops/containers/cloud-'+name+'.Dockerfile','-t',image]
    if os.environ.get('WALLET_BUILD_BUILDER'):args+=['--builder',os.environ['WALLET_BUILD_BUILDER']]
    subprocess.run(args+['.'],check=True)
    # Preserve the attested OCI archive. Transport its identical runtime image to the daemon for scanning.
    (folder/(name+'.docker.tar')).unlink(missing_ok=True)
    subprocess.run(['docker','run','--rm','-v',str(folder.resolve())+':/images','quay.io/skopeo/stable:v1.20.0@sha256:47853bb9fb24202af9110531ebd6e43c5f97701254ca290596640290d17942f4','copy','oci-archive:/images/'+name+'.oci.tar','docker-archive:/images/'+name+'.docker.tar:'+image],check=True)
    subprocess.run(['docker','load','-i',str(folder/(name+'.docker.tar'))],check=True)
    metadata=json.loads((folder/(name+'.provenance.json')).read_text())
    assert metadata.get('buildx.build.provenance'), 'Build provenance must be retained.'
    inspected=json.loads(subprocess.check_output(['docker','inspect',image]))[0]
    with tarfile.open(folder/(name+'.oci.tar')) as archive:
        def read_digest(digest):
            data=archive.extractfile('blobs/sha256/'+digest.split(':')[1]).read()
            assert 'sha256:'+hashlib.sha256(data).hexdigest()==digest
            return json.loads(data)
        index=json.load(archive.extractfile('index.json'))
        assert index['manifests'][0]['digest']==metadata['containerimage.digest']
        index=read_digest(index['manifests'][0]['digest'])
        manifest=next(m for m in index['manifests'] if m['platform']=={'architecture':inspected['Architecture'],'os':'linux'})
        assert read_digest(manifest['digest'])['config']['digest']==inspected['Id']
(folder/'SHA256SUMS').write_text(''.join(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+p.name+'\n' for p in sorted(folder.iterdir()) if p.is_file() and p.name!='SHA256SUMS'))
