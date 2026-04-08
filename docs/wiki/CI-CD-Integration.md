# CI/CD Integration

## GitHub Actions

```yaml
# .github/workflows/dns-sync.yml
name: Sync DNS

on:
  push:
    branches: [main]
    paths:
      - zones/**
      - config.yaml

jobs:
  sync:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Download dns-sync
        run: |
          curl -L https://github.com/cl8dep/dns-sync/releases/latest/download/dns-sync-linux-x64 \
            -o /usr/local/bin/dns-sync
          chmod +x /usr/local/bin/dns-sync

      - name: Validate
        run: dns-sync validate --config config.yaml
        env:
          CF_API_TOKEN: ${{ secrets.CF_API_TOKEN }}

      - name: Plan (on PR)
        if: github.event_name == 'pull_request'
        run: dns-sync plan --config config.yaml
        env:
          CF_API_TOKEN: ${{ secrets.CF_API_TOKEN }}

      - name: Apply (on main push)
        if: github.ref == 'refs/heads/main'
        run: dns-sync apply --config config.yaml --yes
        env:
          CF_API_TOKEN: ${{ secrets.CF_API_TOKEN }}
```

## GCP Cloud Run Jobs

Run dns-sync as a scheduled Cloud Run Job with auto-detected structured logging:

```bash
gcloud run jobs create dns-sync \
  --image ghcr.io/cl8dep/dns-sync:latest \
  --region us-central1 \
  --set-env-vars CF_API_TOKEN=your-token \
  --args="apply,--config,/config/config.yaml,--yes"
```

The `K_SERVICE` environment variable is automatically set in Cloud Run, which enables JSON structured logging compatible with Google Cloud Logging.

## Docker

```bash
docker run --rm \
  -e CF_API_TOKEN=your-token \
  -v $(pwd)/config.yaml:/app/config.yaml \
  -v $(pwd)/zones:/app/zones \
  ghcr.io/cl8dep/dns-sync:latest \
  apply --config /app/config.yaml --yes
```

## Tips for CI/CD

- Always use `--yes` to skip interactive confirmation
- Set `--max-changes 200` if you expect large initial syncs
- Use `validate` as a separate step before `apply` to catch errors early
- Store API tokens in CI secrets, never in the config file
- Use `--log-file /tmp/dns-sync.log` to capture logs as an artifact
