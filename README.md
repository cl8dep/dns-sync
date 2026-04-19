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

**Supported record types:** A, AAAA, CNAME, MX, TXT, NS, CAA, SRV

For the full list including planned and unsupported types, see [Record Types](https://github.com/cl8dep/dns-sync/wiki/Home#supported-record-types) in the wiki.

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

## Performance

<!-- BENCHMARK_START -->
> Measured with [hyperfine](https://github.com/sharkdp/hyperfine) on ubuntu-latest (linux/x64) — dns-sync 0.0.0-dev+b5377a7e0398c19c4ce40847005568299df34c41, octoDNS 1.16.0. Updated 2026-04-19.

**Startup time (`--help`)**

| Command | Mean [ms] | Min [ms] | Max [ms] | Relative |
|:---|---:|---:|---:|---:|
| `dns-sync` | 95.6 ± 2.7 | 93.5 | 106.4 | 1.00 |
| `octodns-sync` | 117.3 ± 2.0 | 114.5 | 121.5 | 1.23 ± 0.04 |


**Plan time (YAML → YAML, no network)**

| Command | Mean [ms] | Min [ms] | Max [ms] | Relative |
|:---|---:|---:|---:|---:|
| `dns-sync` | 142.6 ± 3.4 | 137.3 | 150.4 | 1.00 |
| `octodns-sync` | 164.5 ± 2.9 | 159.8 | 171.8 | 1.15 ± 0.03 |

<!-- BENCHMARK_END -->

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT — see [LICENSE](LICENSE)
