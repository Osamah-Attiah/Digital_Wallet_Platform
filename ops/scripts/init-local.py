#!/usr/bin/env python3
from pathlib import Path
import secrets, os, subprocess, datetime
root = Path(__file__).resolve().parents[2]
folder = root / '.local' / 'secrets'; folder.mkdir(parents=True, exist_ok=True); folder.chmod(0o700)
for name in ['bootstrap_password','migrator_db_password','api_db_password','worker_db_password','redis_password','rabbit_password','rabbit_api_password','jwt_key','otp_key','token_key','cache_key','fixture_key','grafana_password']:
    path = folder / name
    if not path.exists():
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as f: f.write(secrets.token_hex(32))
# Docker Compose file-backed secrets preserve host mode; containers need read access on local Docker Desktop.
# Parent directory stays 0700; these generated keys are exclusively local development credentials.
    path.chmod(0o644)
env = root / '.env'
if not env.exists():
    sha = subprocess.check_output(['git','rev-parse','HEAD'], cwd=root, text=True).strip()
    env.write_text('VERSION=13.0.0-local\nGIT_SHA=' + sha + '\nBUILD_DATE=' + datetime.datetime.now(datetime.timezone.utc).isoformat() + '\n')
    env.chmod(0o600)
print('Local credentials prepared; values are never printed.')
