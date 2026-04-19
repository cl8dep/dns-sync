#!/usr/bin/env bash

RUNS=${1:-20}
ZONE_DIR=${ZONE_DIR:-/benchmark/zones}

echo "=== dns-sync vs OctoDNS Benchmark ==="
echo "Runs: $RUNS | Tool: hyperfine"
echo ""

mkdir -p /tmp/dns-sync-out /tmp/octodns-out
cp "$ZONE_DIR/dns-sync/example.com.yaml" /tmp/dns-sync-out/example.com.yaml

cat > /tmp/bench-dns-sync.yaml << EOF
providers:
  source:
    type: yaml
    directory: $ZONE_DIR/dns-sync
  target:
    type: yaml
    directory: /tmp/dns-sync-out
zones:
  example.com.:
    source: source
    targets:
      - target
EOF

cat > /tmp/bench-octodns.yaml << EOF
providers:
  source:
    class: octodns.provider.yaml.YamlProvider
    directory: $ZONE_DIR/octodns
  target:
    class: octodns.provider.yaml.YamlProvider
    directory: /tmp/octodns-out
    default_ttl: 3600
zones:
  example.com.:
    sources:
      - source
    targets:
      - target
EOF

echo "1. Startup time (--help)"
hyperfine --warmup 3 --runs "$RUNS" \
    --command-name "dns-sync" "dns-sync --help" \
    --command-name "octodns-sync" "octodns-sync -h"

echo ""
echo "2. Plan time (YAML → YAML, no network)"
hyperfine --warmup 3 --runs "$RUNS" \
    --command-name "dns-sync" "dns-sync plan -c /tmp/bench-dns-sync.yaml" \
    --command-name "octodns-sync" "octodns-sync --config-file /tmp/bench-octodns.yaml"
