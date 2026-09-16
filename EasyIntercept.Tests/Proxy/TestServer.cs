using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EasyIntercept.Tests.Proxy;

/// <summary>A minimal Kestrel host on an ephemeral loopback port, plain or TLS with a throwaway self-signed certificate.</summary>
public sealed class TestServer : IAsyncDisposable
{
    public WebApplication App { get; }
    public int Port { get; }
    public string Scheme { get; }
    public string BaseUrl => $"{Scheme}://127.0.0.1:{Port}";
    public string HostPort => $"127.0.0.1:{Port}";

    private TestServer(WebApplication app, int port, string scheme)
    {
        App = app;
        Port = port;
        Scheme = scheme;
    }

    public static async Task<TestServer> StartAsync(Action<WebApplication> configure, bool https = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var cert = https ? SelfSignedCertificate() : null;
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l =>
        {
            if (cert is not null) l.UseHttps(cert);
        }));

        var app = builder.Build();
        app.UseWebSockets();
        configure(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var port = new Uri(address).Port;
        return new TestServer(app, port, https ? "https" : "http");
    }

    private static X509Certificate2 SelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Re-import through PKCS#12 so the private key is usable by SslStream on every platform (notably macOS).
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await App.StopAsync(cts.Token);
        await App.DisposeAsync();
    }
}
