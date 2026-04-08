# Zone File Format

Zone files live in the `directory` configured for the `yaml` provider. Each file is named `{zone-name}.yaml` (e.g. `example.com.yaml` or `example.com.yaml`).

## Structure

```yaml
<subdomain>:
  type: <RECORD_TYPE>
  ttl: <seconds>
  value: <single-value>   # for single-value records
  values:                 # for multi-value records
    - ...
```

The subdomain is relative to the zone. Special values:
- `''` (empty string) — apex / root / `@`
- `@` — apex (alternative syntax)
- `www` → `www.example.com.`
- `_dmarc` → `_dmarc.example.com.`

## Multiple records at the same name

Use a list:

```yaml
'':
  - type: A
    ttl: 3600
    values: [203.0.113.1, 203.0.113.2]
  - type: MX
    ttl: 600
    values:
      - preference: 10
        exchange: mx1.example.com.
```

## Record types

### A — IPv4 address

```yaml
'':
  type: A
  ttl: 3600
  values:
    - 203.0.113.1
    - 203.0.113.2
```

### AAAA — IPv6 address

```yaml
'':
  type: AAAA
  ttl: 3600
  values:
    - 2001:db8::1
```

### CNAME — Canonical name

```yaml
www:
  type: CNAME
  ttl: 300
  value: example.com.    # trailing dot = absolute FQDN
```

> CNAME cannot coexist with any other record type at the same name (RFC 1034).

### MX — Mail exchange

```yaml
'':
  type: MX
  ttl: 600
  values:
    - preference: 10
      exchange: mx1.example.com.
    - preference: 20
      exchange: mx2.example.com.
```

### TXT — Text

```yaml
_dmarc:
  type: TXT
  ttl: 3600
  value: "v=DMARC1; p=reject; rua=mailto:dmarc@example.com"

# Multiple TXT values at the same name:
'':
  type: TXT
  ttl: 3600
  values:
    - "v=spf1 include:_spf.example.com ~all"
    - "google-site-verification=abc123"
```

### NS — Nameserver

```yaml
# Subdomain delegation (apex NS is excluded from sync by default)
sub:
  type: NS
  ttl: 3600
  values:
    - ns1.sub.example.com.
    - ns2.sub.example.com.
```

### CAA — Certification Authority Authorization

```yaml
'':
  type: CAA
  ttl: 3600
  values:
    - flags: 0
      tag: issue
      value: "letsencrypt.org"
    - flags: 0
      tag: issuewild
      value: ";"
```

## Trailing dots

Absolute FQDNs must end with a trailing dot (`.`). dns-sync normalizes these automatically when comparing across providers, so both forms work:

```yaml
value: example.com.    # explicit FQDN
value: example.com     # dns-sync adds the dot internally
```

## Special behaviors

- **SOA records** are always excluded from sync (auto-managed by providers)
- **Apex NS records** (NS at zone root) are excluded by default to prevent registrar conflicts. Use `--include-apex-ns` to override.
- **TXT chunking** is normalized automatically (providers split at 255 bytes differently)
