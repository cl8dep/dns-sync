# Contributing to dns-sync

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git

## Setup

```bash
git clone https://github.com/cl8dep/dns-sync.git
cd dns-sync
dotnet build
dotnet test
```

All tests should pass before you start working.

## Running the tests

```bash
dotnet test                                          # all tests
dotnet test --filter "FullyQualifiedName~DiffEngine" # specific test class
dotnet test --logger "console;verbosity=detailed"   # verbose output
```

## Project structure

```
src/DnsSync/
├── Commands/       # CLI commands: validate, plan, apply (Spectre.Console.Cli)
├── Config/         # Config loading, env var interpolation, .env support
├── Core/           # DnsRecord types, ZoneDiff engine, record normalization
├── Providers/
│   ├── Yaml/       # YAML zone file provider (source only)
│   ├── Cloudflare/ # Cloudflare DNS API v4
│   └── Gcp/        # Google Cloud DNS REST API v1
└── Validation/     # Zone and record validation rules

tests/DnsSync.Tests/
├── Core/           # ZoneDiff, normalization tests
├── Config/         # ConfigLoader, DotEnvLoader tests
└── Providers/      # YamlProvider tests
```

## Adding a new DNS provider

1. Create `src/DnsSync/Providers/<Name>/<Name>Provider.cs` implementing `IProvider`:

```csharp
public interface IProvider
{
    Task PreflightAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetZonesAsync(CancellationToken ct = default);
    Task<DnsZone> GetZoneAsync(string zoneName, CancellationToken ct = default);
    Task ApplyPlanAsync(string zoneName, IReadOnlyList<DnsChange> changes, CancellationToken ct = default);
}
```

2. Register it in `ProviderFactory.cs`:

```csharp
"myprovider" => new MyProvider(...),
```

3. Add it to `KnownProviderTypes` in `ConfigLoader.cs` and add validation in `ValidateStructure()`.

4. Add the new type to `ProviderConfig` fields in `DnsSyncConfig.cs` if it needs new config keys.

5. Add at least one integration-style test in `tests/DnsSync.Tests/Providers/`.

## Adding a new DNS record type

1. Create `src/DnsSync/Core/Records/<Type>Record.cs` extending `DnsRecord`.
2. Implement `CanonicalHash()` (order-independent, normalized) and `FormatValues()`.
3. Add the type to `ZoneValidator.cs` (`KnownTypes`, `SupportedTypes`, and a validation case).
4. Add parsing in `YamlProvider.cs` (`ParseRecordDef`).
5. Add serialization/deserialization in `CloudflareProvider.cs` and `GcpCloudDnsProvider.cs`.
6. Add tests in `RecordNormalizationTests.cs`.

## Code style

- Follow existing patterns — no tabs, 4-space indent, `var` where type is obvious
- No external packages without discussion — the binary size matters
- No docstrings on internal methods unless the logic is genuinely non-obvious
- Prefer simple, direct code over abstractions

## Submitting changes

1. Fork the repo and create a branch: `git checkout -b feature/my-feature`
2. Make your changes with tests
3. Ensure `dotnet test` passes
4. Open a pull request with a clear description of what and why

## Reporting bugs

Open an issue at [github.com/cl8dep/dns-sync/issues](https://github.com/cl8dep/dns-sync/issues) with:
- dns-sync version (`dns-sync --version`)
- The command you ran
- Expected vs actual behavior
- Config file (redact secrets)
