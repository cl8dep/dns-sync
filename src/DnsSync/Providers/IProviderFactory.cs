using DnsSync.Config;
using Microsoft.Extensions.Logging;

namespace DnsSync.Providers;

public interface IProviderFactory
{
    IProvider Create(string name, ProviderConfig config, ILoggerFactory loggerFactory);
}
