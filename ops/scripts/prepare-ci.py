#!/usr/bin/env python3
"""Discard checked-in historical evidence only in an ephemeral GitHub Actions checkout."""
import os
from pathlib import Path

if os.environ.get('GITHUB_ACTIONS') != 'true':
    raise SystemExit('Evidence reset is restricted to ephemeral GitHub Actions checkouts.')
folder = Path(__file__).resolve().parents[2] / 'ops/validation/phase13'
folder.mkdir(parents=True, exist_ok=True)
for path in folder.iterdir():
    if path.is_file() and (path.suffix in {'.json', '.txt'} or path.name == 'SHA256SUMS'):
        path.unlink()
print('CI evidence folder prepared for this run; no historical reports will be uploaded.')
