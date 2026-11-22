using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace TaskService;

public static class ProxyTrust
{
    public static ForwardedHeadersOptions? Options(IConfiguration configuration)
    {
        var entries = configuration.GetSection("Operations:TrustedProxies").GetChildren().Select(x => x.Value).ToArray();
        if (entries.Length == 0)
            return null;
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var value in entries)
        {
            if (!IPAddress.TryParse(value, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                throw new InvalidOperationException("Each trusted proxy must have a specific IP address.");
            options.KnownProxies.Add(address);
        }
        return options;
    }
}
