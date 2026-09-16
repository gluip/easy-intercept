using System.Net;

namespace EasyIntercept.Proxy;

/// <summary>
/// The named HttpClient the proxy uses for upstream requests. Shared with the tests so they run
/// against exactly the handler configuration the app uses.
/// </summary>
public static class ProxyHttpClient
{
    public const string Name = "proxy";

    /// <param name="trustAnyUpstreamCertificate">
    /// Test-only: accept self-signed upstream certificates. Never enable this in the app itself.
    /// </param>
    public static IHttpClientBuilder Configure(IServiceCollection services, bool trustAnyUpstreamCertificate = false) =>
        services.AddHttpClient(Name, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    UseProxy = false,
                };
                if (trustAnyUpstreamCertificate)
                    handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                return handler;
            });
}
