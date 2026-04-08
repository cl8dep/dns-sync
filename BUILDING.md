# Building dns-sync

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

Verify your install:

```bash
dotnet --version   # should be 10.x
```

## Build

```bash
# Debug build
dotnet build src/DnsSync

# Release build
dotnet build src/DnsSync -c Release
```

## Run locally

```bash
dotnet run --project src/DnsSync -- --help
dotnet run --project src/DnsSync -- validate --config config.yaml
dotnet run --project src/DnsSync -- plan --config config.yaml
```

## Run tests

```bash
dotnet test
dotnet test --logger "console;verbosity=detailed"   # verbose output
```

## Publish self-contained binaries

Single-file, self-contained executables (no .NET runtime required on target machine):

```bash
# macOS Apple Silicon
dotnet publish src/DnsSync -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -o out/osx-arm64

# macOS Intel
dotnet publish src/DnsSync -c Release -r osx-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -o out/osx-x64

# Linux x64
dotnet publish src/DnsSync -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -o out/linux-x64

# Windows x64
dotnet publish src/DnsSync -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -o out/win-x64
```

The resulting binaries are in `out/<rid>/dns-sync` (or `dns-sync.exe` on Windows).

## Project structure

```
dns-sync/
├── src/
│   └── DnsSync/
│       ├── Commands/       # CLI commands (validate, plan, apply)
│       ├── Config/         # Config loading, .env support
│       ├── Core/           # DnsRecord types, ZoneDiff, normalization
│       ├── Providers/
│       │   ├── Yaml/       # YAML zone file provider (source)
│       │   ├── Cloudflare/ # Cloudflare DNS API provider
│       │   └── Gcp/        # Google Cloud DNS provider
│       └── Validation/     # Zone and record validation
└── tests/
    └── DnsSync.Tests/      # xUnit tests
```
