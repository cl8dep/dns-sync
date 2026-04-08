# dns-sync

**DNS as code. One binary.**

A fast, self-contained CLI for syncing DNS records across providers — inspired by OctoDNS, built in C#.

Define your DNS zones as YAML files, then let `dns-sync` keep your providers in sync.

```
dns-sync plan   --config config.yaml    # Preview what would change
dns-sync apply  --config config.yaml    # Apply changes
dns-sync validate --config config.yaml  # Check config and zone files
```

---

## Why dns-sync?

| Pain point (OctoDNS) | dns-sync solution |
|---|---|
| Python-only, complex install | Single self-contained binary — no runtime needed |
| Stack traces as errors | Friendly `✗` messages with actionable hints |
| No pre-flight validation | Verifies API credentials before touching anything |
| Opaque diff output | Color-coded plan: `+` green / `~` yellow / `-` red |
| `--doit` naming | Clear `plan` vs `apply` commands |
| No structured logging | `--log-file` + GCP Cloud Logging (auto-detected) |

---

## Installation

### Download binary (recommended)

Download the latest release for your platform from the [Releases page](https://github.com/cl8dep/dns-sync/releases).

```bash
# macOS arm64
curl -L https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-darwin-arm64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync

# Linux x64
curl -L https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-linux-x64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync
```

### Build from source

```bash
git clone https://github.com/cl8dep/dns-sync.git
cd dns-sync
dotnet publish src/DnsSync -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:DebugType=none -o out/
./out/dns-sync --version
```

---

## Quick start

**1. Copy the example config:**

```bash
cp config.example.yaml config.yaml
```

**2. Set your Cloudflare API token:**

```bash
export CF_API_TOKEN=your_cloudflare_token_here
```

**3. Create a zone file** (`zones/example.com.yaml`):

```yaml
'':
  - type: A
    ttl: 3600
    values:
      - 203.0.113.1

www:
  type: CNAME
  ttl: 300
  value: example.com.

_dmarc:
  type: TXT
  ttl: 3600
  value: "v=DMARC1; p=reject; rua=mailto:dmarc@example.com"
```

**4. Preview changes:**

```bash
dns-sync plan --config config.yaml
```

**5. Apply:**

```bash
dns-sync apply --config config.yaml
```

---

## Config reference

```yaml
providers:
  yaml_source:
    type: yaml
    directory: ./zones          # path to zone YAML files

  cloudflare:
    type: cloudflare
    api_token: "${CF_API_TOKEN}" # Zone:DNS:Edit scoped token

zones:
  example.com.:
    source: yaml_source
    targets:
      - cloudflare
```

All `${ENV_VAR}` references are interpolated from environment variables at runtime. Never put secrets directly in config files.

---

## Zone file format

Zone files live in the `directory` configured for the `yaml` provider. Each file is named `{zone}.yaml` (e.g. `example.com.yaml`).

```yaml
# Apex / @ / root
'':
  - type: A
    ttl: 3600
    values:
      - 1.2.3.4
      - 5.6.7.8

  - type: MX
    ttl: 600
    values:
      - preference: 10
        exchange: mx1.example.com.

# Subdomains
www:
  type: CNAME
  ttl: 300
  value: example.com.

_dmarc:
  type: TXT
  ttl: 3600
  value: "v=DMARC1; p=reject"

'':
  type: CAA
  ttl: 3600
  values:
    - flags: 0
      tag: issue
      value: "letsencrypt.org"
```

**Supported record types:** A, AAAA, CNAME, MX, TXT, NS, CAA

---

## Commands

### `dns-sync validate`

Validates config structure and zone files without making any network calls.

```
dns-sync validate --config config.yaml
```

### `dns-sync plan`

Shows what would change. Reads source zone and compares against each target provider. No writes.

```
dns-sync plan --config config.yaml
dns-sync plan --config config.yaml --include-apex-ns
```

Output:
```
✓ Config valid (1 zone, 2 providers)

Running pre-flight checks...
✓ Source provider 'yaml_source' reachable
✓ Target provider 'cloudflare' reachable

Zone: example.com. → cloudflare
  + api.example.com.                             A      300  203.0.113.10  (new)
  ~ mail.example.com.                            A     3600→300  203.0.113.20  (ttl only)
  - old.example.com.                             A     3600  (deleted)

3 change(s) — run dns-sync apply to apply.
```

### `dns-sync apply`

Applies changes to all target providers.

```
dns-sync apply --config config.yaml           # interactive confirmation
dns-sync apply --config config.yaml --yes     # non-interactive (CI/CD)
dns-sync apply --config config.yaml --max-changes 100  # raise safety limit
dns-sync apply --config config.yaml --force   # skip safety limit entirely
```

---

## Global flags

| Flag | Description |
|---|---|
| `-c, --config <PATH>` | Path to config YAML (default: `config.yaml`) |
| `--strict` | Treat warnings as errors |
| `-v, --verbose` | Enable debug log output |
| `--no-color` | Disable ANSI colors (`NO_COLOR` env var also respected) |
| `--gcp-logs` | Structured JSON output for GCP Cloud Logging (auto in Cloud Run) |
| `--log-file <PATH>` | Also write logs to a file |

### Apply-specific flags

| Flag | Description |
|---|---|
| `-y, --yes` | Skip confirmation prompt |
| `--max-changes <N>` | Abort if plan > N changes (default: 50) |
| `--force` | Override `--max-changes` |
| `--include-apex-ns` | Include apex NS records in diff (excluded by default) |

---

## Safety features

- **Pre-flight checks** — verifies API credentials before touching any DNS records
- **`--max-changes 50`** — aborts if plan exceeds 50 changes (prevents accidental mass deletion)
- **Interactive confirmation** — shows full plan and asks before applying (skip with `--yes`)
- **SOA excluded** — SOA records are never synced (they'd corrupt the zone serial)
- **Apex NS excluded** — apex NS records are excluded by default to prevent registrar conflicts

---

## Supported providers

| Provider | Read | Write | Status |
|---|---|---|---|
| `yaml` | ✓ | — | Available |
| `cloudflare` | ✓ | ✓ | Available |
| `route53` | — | — | Coming soon |
| `gcp_cloud_dns` | — | — | Coming soon |

---

## Development

```bash
# Build
dotnet build

# Run tests
dotnet test

# Run locally
dotnet run --project src/DnsSync -- plan --config config.yaml

# Publish single binary (linux)
dotnet publish src/DnsSync -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:DebugType=none -o out/
```

---

## License

MIT — see [LICENSE](LICENSE)
