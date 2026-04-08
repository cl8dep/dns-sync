# dns-sync

**DNS as code. One binary.**

A fast, self-contained CLI for syncing DNS records across providers — inspired by OctoDNS, built in C#.

Define your DNS zones as YAML files, then let `dns-sync` keep your providers in sync.

```
dns-sync validate --config config.yaml   # Validate config and zone files (no network calls)
dns-sync plan     --config config.yaml   # Preview what would change
dns-sync apply    --config config.yaml   # Apply changes
```

---

## Why dns-sync?

| Pain point (OctoDNS) | dns-sync solution |
|---|---|
| Python-only, complex install | Single self-contained binary — no runtime needed |
| Stack traces as errors | Friendly `✗` messages with actionable hints |
| No pre-flight validation | Verifies API credentials before touching anything |
| Opaque diff output | Color-coded plan with before/after for updates |
| `--doit` naming | Clear `plan` vs `apply` commands |
| No structured logging | `--log-file` + GCP Cloud Logging (auto-detected in Cloud Run) |

---

## Installation

### Download binary (recommended)

Download the latest release for your platform from the [Releases page](https://github.com/cl8dep/dns-sync/releases).

```bash
# macOS arm64
curl -L https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-darwin-arm64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync

# macOS x64
curl -L https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-darwin-x64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync

# Linux x64
curl -L https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-linux-x64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync
```

### Build from source

```bash
git clone --recurse-submodules https://github.com/cl8dep/dns-sync.git
cd dns-sync
dotnet publish src/DnsSync -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:DebugType=none -o out/
./out/dns-sync --version
```

### Docker

```bash
docker run --rm \
  -e CLOUDFLARE_API_TOKEN=your_token \
  -v $(pwd)/config.yaml:/app/config.yaml \
  -v $(pwd)/zones:/app/zones \
  ghcr.io/cl8dep/dns-sync:latest plan --config /app/config.yaml
```

---

## Quick start

**1. Copy the example config:**

```bash
cp config.example.yaml config.yaml
```

**2. Set your credentials:**

```bash
export CLOUDFLARE_API_TOKEN=your_token_here
export CLOUDFLARE_ACCOUNT_ID=your_account_id  # optional but recommended
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
  zones:
    type: yaml
    directory: ./zones          # relative to this config file

  cloudflare:
    type: cloudflare
    api_token: ${CLOUDFLARE_API_TOKEN}    # Zone:DNS:Edit scoped token
    account_id: ${CLOUDFLARE_ACCOUNT_ID} # optional — scopes zone lookups to one account

zones:
  example.com.:
    source: zones
    targets:
      - cloudflare
```

All `${ENV_VAR}` references are interpolated from environment variables at runtime. Never put secrets directly in config files.

> **Note:** `directory` paths are resolved relative to the config file location, not the current working directory. You can run `dns-sync` from any folder.

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

**Supported record types:** A, AAAA, CNAME, MX, TXT, NS, CAA, SRV

---

## Commands

### `dns-sync validate`

Validates config structure and zone files without making any network calls.

```
dns-sync validate --config config.yaml [--strict]
```

### `dns-sync plan`

Shows what would change. No writes to any provider.

```
dns-sync plan --config config.yaml
dns-sync plan --config config.yaml --include-apex-ns
dns-sync plan --config config.yaml --output json       # machine-readable output
dns-sync plan --config config.yaml --exit-code         # returns 2 if changes pending
```

Example output:
```
Zone: example.com. → cloudflare
  + api.example.com.                         A      300  203.0.113.10
  ~ mail.example.com.                        A     3600
      before: 203.0.113.5
      after:  203.0.113.20
  - old.example.com.                         A     3600  203.0.113.1

  1 create(s), 1 update(s), 1 delete(s)
```

### `dns-sync apply`

Applies changes to all target providers.

```
dns-sync apply --config config.yaml                   # interactive confirmation
dns-sync apply --config config.yaml --yes             # non-interactive (CI/CD)
dns-sync apply --config config.yaml --max-changes 100 # raise safety limit
dns-sync apply --config config.yaml --force           # skip safety limit
```

---

## Flags

### Global

| Flag | Description |
|---|---|
| `-c, --config <PATH>` | Path to config YAML (default: `config.yaml`) |
| `--strict` | Treat warnings as errors |
| `-v, --verbose` | Enable debug log output |
| `--no-color` | Disable ANSI colors (`NO_COLOR` env var also respected) |
| `--gcp-logs` | Structured JSON output for GCP Cloud Logging (auto-enabled in Cloud Run) |
| `--log-file <PATH>` | Also write logs to a file |

### Plan-specific

| Flag | Description |
|---|---|
| `--include-apex-ns` | Include apex NS records in diff (excluded by default) |
| `--output <FORMAT>` | Output format: `text` (default) or `json` |
| `--exit-code` | Return exit code `2` when changes are pending (Terraform-compatible) |

### Apply-specific

| Flag | Description |
|---|---|
| `-y, --yes` | Skip confirmation prompt |
| `--max-changes <N>` | Abort if plan > N changes (default: 50) |
| `--force` | Override `--max-changes` |
| `--include-apex-ns` | Include apex NS records in diff |

---

## Safety features

- **Pre-flight checks** — verifies API credentials and connectivity before touching any DNS records
- **`--max-changes 50`** — aborts if plan exceeds 50 changes (prevents accidental mass deletion)
- **Interactive confirmation** — shows full plan and asks before applying (skip with `--yes`)
- **SOA excluded** — SOA records are never synced (they'd corrupt the zone serial)
- **Apex NS excluded** — apex NS records are excluded by default to prevent registrar conflicts
- **`account_id` scoping** — Cloudflare zone lookups can be scoped to a specific account

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

# Publish single binary (macOS arm64)
dotnet publish src/DnsSync -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true -p:DebugType=none -o out/
```

---

## License

MIT — see [LICENSE](LICENSE)
