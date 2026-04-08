#!/usr/bin/env python3
"""
Update dns-sync Homebrew formula with new version and sha256 hashes.
Usage: python3 update-tap.py <formula_path>
Reads VERSION, VERSION_NUM, SHA_ARM64, SHA_X64 from environment.
"""
import re, os, sys

formula_path = sys.argv[1]
version = os.environ['VERSION_NUM']
tag     = os.environ['VERSION']
arm64   = os.environ['SHA_ARM64']
x64     = os.environ['SHA_X64']

with open(formula_path) as f:
    t = f.read()

# Update version field
t = re.sub(r'version "[^"]+"', f'version "{version}"', t)

# Update download URLs
t = re.sub(r'download/v[^/]+/dns-sync-darwin-arm64', f'download/{tag}/dns-sync-darwin-arm64', t)
t = re.sub(r'download/v[^/]+/dns-sync-darwin-x64',   f'download/{tag}/dns-sync-darwin-x64',   t)

# Update sha256 values in order: first = arm64, second = x64
n = [0]
def replace_sha(m):
    n[0] += 1
    return f'sha256 "{arm64 if n[0] == 1 else x64}"'
t = re.sub(r'sha256 "[a-f0-9]+"', replace_sha, t)

with open(formula_path, 'w') as f:
    f.write(t)

print(f"Updated {formula_path} to {version}")
