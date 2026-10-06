#!/usr/bin/env python3
"""Check the new host files explicitly; dotnet format directory filters can select no files."""
from pathlib import Path
import subprocess
import sys

folders = [Path('src/DigitalWallet.Api/Hosting'), Path('src/DigitalWallet.Worker'), Path('src/DigitalWallet.Migrator')]
paths = sorted(str(path) for folder in folders for path in folder.glob('*.cs'))
paths.append('tests/DigitalWallet.Api.Tests/PackagingPhase13Tests.cs')
args = ['dotnet', 'format', 'whitespace', '--folder', '--include', *paths]
if '--fix' not in sys.argv:
    args.append('--verify-no-changes')
subprocess.run(args, check=True)
