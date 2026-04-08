# Getting Started

## Prerequisites

No runtime required. Just download the binary.

## 1. Download

```bash
# macOS ARM (Apple Silicon)
curl -L https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-darwin-arm64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync

# macOS Intel
curl -L https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-darwin-x64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync

# Linux x64
curl -L https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-linux-x64 \
  -o /usr/local/bin/dns-sync && chmod +x /usr/local/bin/dns-sync

dns-sync --version
```

## 2. Create your config

```bash
cp config.example.yaml config.yaml
```

Edit `config.yaml` and set your provider credentials via environment variables:

```bash
export CF_API_TOKEN=your_cloudflare_zone_dns_edit_token
```

## 3. Write your first zone file

Create `zones/example.com.yaml`:

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

## 4. Validate

```bash
dns-sync validate --config config.yaml
```

## 5. Preview changes

```bash
dns-sync plan --config config.yaml
```

## 6. Apply

```bash
dns-sync apply --config config.yaml
```

Type `y` to confirm, or use `--yes` for CI/CD.

---

Next: [Configuration Reference](Configuration-Reference)
