# Configuration Reference

The main config file (`config.yaml`) defines providers and zones.

## Top-level structure

```yaml
providers:
  <provider-name>:
    type: <provider-type>
    # ...provider-specific options

zones:
  <zone-name>.:
    source: <provider-name>
    targets:
      - <provider-name>
```

## Provider types

### `yaml` (source only)

```yaml
providers:
  yaml_source:
    type: yaml
    directory: ./zones    # path to zone YAML files (required)
```

### `cloudflare`

```yaml
providers:
  cloudflare:
    type: cloudflare
    api_token: "${CF_API_TOKEN}"   # Zone:DNS:Edit scoped token (required)
```

Create a scoped API token at [dash.cloudflare.com/profile/api-tokens](https://dash.cloudflare.com/profile/api-tokens) with:
- **Zone → DNS → Edit**
- Limit to specific zones if desired

## Environment variable interpolation

All `${VAR}` references are replaced at runtime from environment variables.

```yaml
api_token: "${CF_API_TOKEN}"
```

Throws an error at startup if the variable is not set. Never put secrets directly in config files.

## Zone configuration

```yaml
zones:
  example.com.:           # must end with a trailing dot
    source: yaml_source   # provider name (must be type: yaml for Phase 1)
    targets:
      - cloudflare        # one or more target providers
```

## Validation rules

- Each zone must have exactly one source
- Source and target cannot be the same provider
- All referenced providers must be defined in `providers:`
- `yaml` provider requires `directory`
- `cloudflare` provider requires `api_token`
