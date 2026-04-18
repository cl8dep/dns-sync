# dns-sync

[![CI](https://github.com/cl8dep/dns-sync/actions/workflows/ci.yml/badge.svg)](https://github.com/cl8dep/dns-sync/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/cl8dep/dns-sync)](https://github.com/cl8dep/dns-sync/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

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

## Supported providers

| Provider | Type | Read | Write |
|---|---|---|---|
| YAML files | `yaml` | ✓ | — |
| Cloudflare | `cloudflare` | ✓ | ✓ |
| GCP Cloud DNS | `gcp_cloud_dns` | ✓ | ✓ |
| Porkbun | `porkbun` | ✓ | ✓ |
| AWS Route 53 | `route53` | — | — (planned) |

**Supported record types:**

| Type | Description | Supported |
|---|---|---|
| A | IPv4 address | ✓ |
| AAAA | IPv6 address | ✓ |
| CNAME | Canonical name alias | ✓ |
| MX | Mail exchange | ✓ |
| TXT | Text (SPF, DKIM, DMARC…) | ✓ |
| NS | Name server delegation | ✓ |
| CAA | Certification Authority Authorization | ✓ |
| SRV | Service locator | ✓ |
| ALIAS / ANAME | Flattened CNAME at apex | ✓ (read as CNAME) |
| PTR | Reverse DNS pointer | — |
| TLSA | TLS certificate association (DANE) | — |
| SSHFP | SSH fingerprint | — |
| NAPTR | Naming authority pointer | — |
| DNSKEY | DNSSEC zone key | — |
| DS | DNSSEC delegation signer | — |
| HTTPS / SVCB | Service binding (modern CDN) | — |

---

## Install

```bash
# macOS (Apple Silicon)
curl -fsSL https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-darwin-arm64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync

# Linux
curl -fsSL https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-linux-x64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync
```

Homebrew: `brew install cl8dep/tap/dns-sync`

---

## Documentation

- [Getting Started](https://github.com/cl8dep/dns-sync/wiki/Getting-Started) — install, first config, first sync
- [Configuration Reference](https://github.com/cl8dep/dns-sync/wiki/Configuration-Reference) — all providers and options
- [Zone File Format](https://github.com/cl8dep/dns-sync/wiki/Zone-File-Format) — record types and syntax
- [CI/CD Integration](https://github.com/cl8dep/dns-sync/wiki/CI-CD-Integration) — GitHub Actions, GitOps workflow

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT — see [LICENSE](LICENSE)
