#!/bin/sh
# Download the dns-sync binary that matches this OS and CPU.
# Usage: install.sh [version]   (default: latest)
# Env:   DNS_SYNC_INSTALL_DIR   (default: $HOME/.local/bin)
set -eu

version="${1:-latest}"
dir="${DNS_SYNC_INSTALL_DIR:-$HOME/.local/bin}"

case "$(uname -s)" in
  Linux)  os=linux ;;
  Darwin) os=darwin ;;
  *) echo "Unsupported OS: $(uname -s)" >&2; exit 1 ;;
esac

case "$(uname -m)" in
  x86_64|amd64)  arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

asset="dns-sync-$os-$arch"
base="https://github.com/cl8dep/dns-sync/releases"
if [ "$version" = "latest" ]; then
  url="$base/latest/download/$asset"
else
  url="$base/download/$version/$asset"
fi

mkdir -p "$dir"
tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT
curl -fsSL "$url" -o "$tmp" || { echo "Download failed: $url" >&2; exit 1; }
chmod +x "$tmp"
mv "$tmp" "$dir/dns-sync"
trap - EXIT
echo "Installed $asset ($version) to $dir/dns-sync" >&2
