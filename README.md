# dns-sync

**DNS as code. One binary.**

Sync DNS records across providers from YAML zone files — no runtime, no dependencies.

```bash
dns-sync plan  -c config.yaml   # Preview changes
dns-sync apply -c config.yaml   # Apply changes
```

---

## What it does

You define your DNS zones as YAML files. `dns-sync` reads them and syncs your DNS providers to match — creating, updating, or deleting records as needed.

It works like Terraform for DNS: `plan` shows what would change, `apply` makes it happen.

---

## Features

- Single self-contained binary — no Python, no Node, no runtime
- Color-coded plan with before/after diffs
- Pre-flight credential checks before touching any DNS
- Safety limit: aborts if plan exceeds N changes (`--max-changes`)
- `--exit-code` flag for CI/CD pipelines (returns `2` when changes pending)
- JSON output mode for scripting
- Structured logging for GCP Cloud Logging (auto-detected in Cloud Run)
- `.env` file support for local development

---

## Supported providers

| Provider | Type | Read | Write |
|---|---|---|---|
| YAML files | `yaml` | ✓ | — |
| Cloudflare | `cloudflare` | ✓ | ✓ |
| GCP Cloud DNS | `gcp_cloud_dns` | ✓ | ✓ |
| Porkbun | `porkbun` | ✓ | ✓ |
| AWS Route 53 | `route53` | — | — (planned) |

**Supported record types:** A, AAAA, CNAME, MX, TXT, NS, CAA, SRV

---

## Installation

```bash
# macOS (Apple Silicon)
curl -fsSL https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-darwin-arm64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync

# macOS (Intel)
curl -fsSL https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-darwin-x64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync

# Linux
curl -fsSL https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-linux-x64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync
```

Or via Homebrew:

```bash
brew install cl8dep/tap/dns-sync
```

See [Getting Started](https://github.com/cl8dep/dns-sync/wiki/Getting-Started) for full installation options including Docker and building from source.

---

## Quick start

```bash
# 1. Copy the example config
cp config.example.yaml config.yaml

# 2. Set your credentials
export PORKBUN_API_KEY=pk1_...
export PORKBUN_SECRET_KEY=sk1_...

# 3. Preview changes
dns-sync plan -c config.yaml

# 4. Apply
dns-sync apply -c config.yaml
```

Full documentation in the [Wiki](https://github.com/cl8dep/dns-sync/wiki):

- [Getting Started](https://github.com/cl8dep/dns-sync/wiki/Getting-Started)
- [Configuration Reference](https://github.com/cl8dep/dns-sync/wiki/Configuration-Reference)
- [CI/CD Integration](https://github.com/cl8dep/dns-sync/wiki/CI-CD-Integration)

---

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for how to set up the project, run tests, and submit changes.

---

## License

MIT — see [LICENSE](LICENSE)
