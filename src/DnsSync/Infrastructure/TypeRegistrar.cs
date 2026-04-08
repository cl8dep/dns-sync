using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace DnsSync.Infrastructure;

/// <summary>Bridges Microsoft.Extensions.DependencyInjection with Spectre.Console.Cli.</summary>
public sealed class TypeRegistrar(IServiceCollection services) : ITypeRegistrar
{
    public ITypeResolver Build() =>
        new TypeResolver(services.BuildServiceProvider());

    public void Register(Type service, Type implementation) =>
        services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) =>
        services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) =>
        services.AddSingleton(service, _ => factory());
}

public sealed class TypeResolver(IServiceProvider provider) : ITypeResolver, IDisposable
{
    public object? Resolve(Type? type) =>
        type is null ? null : provider.GetService(type);

    public void Dispose()
    {
        if (provider is IDisposable disposable)
            disposable.Dispose();
    }
}
