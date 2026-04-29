using DnsSync.Config;
using Microsoft.Extensions.Logging;

namespace DnsSync.Providers;

public sealed class DefaultProviderFactory : IProviderFactory
{
    public IProvider Create(string name, ProviderConfig config, ILoggerFactory loggerFactory)
        => ProviderFactory.Create(name, config, loggerFactory);
}
