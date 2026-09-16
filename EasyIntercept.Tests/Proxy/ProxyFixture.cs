using System.Net;
using System.Net.Sockets;
using EasyIntercept.AutoResponder;
using EasyIntercept.Certificates;
using EasyIntercept.Proxy;
using EasyIntercept.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EasyIntercept.Tests.Proxy;

/// <summary>
/// A real <see cref="ProxyConnection"/> behind an ephemeral port, with a temp data root and a
/// recording hub. Shared per test class: the CA generation in <see cref="CertificateService"/> is slow.
/// Upstream certificates are trusted blindly so the test servers can use self-signed ones.
/// </summary>
public sealed class ProxyFixture : IDisposable
{
    public AppPaths Paths { get; }
    public SessionStore Store { get; }
    public AutoResponderStore AutoResponder { get; }
    public CertificateService Certs { get; }
    public RecordingHubContext Hub { get; } = new();
    public IHttpClientFactory HttpClientFactory { get; }
    public int ProxyPort { get; }
    public string ProxyUrl => $"http://127.0.0.1:{ProxyPort}";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ServiceProvider _services;

    public ProxyFixture()
    {
        Paths = new AppPaths(Path.Combine(Path.GetTempPath(), "easyintercept-proxytests-" + Guid.NewGuid().ToString("N")));
        Store = new SessionStore(Paths);
        AutoResponder = new AutoResponderStore(Paths);
        Certs = new CertificateService(Paths);

        var services = new ServiceCollection();
        ProxyHttpClient.Configure(services, trustAnyUpstreamCertificate: true);
        _services = services.BuildServiceProvider();
        HttpClientFactory = _services.GetRequiredService<IHttpClientFactory>();

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        ProxyPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            var conn = new ProxyConnection(client, Store, Hub, HttpClientFactory, Certs, AutoResponder, _cts.Token);
            _ = Task.Run(async () =>
            {
                try { await conn.HandleAsync(); }
                catch { /* a test that cares asserts on the session it produced */ }
            });
        }
    }

    public WebProxy WebProxy => new(ProxyUrl);

    public HttpClient NewHttpClient(bool trustProxyCert = true) => new(new HttpClientHandler
    {
        Proxy = WebProxy,
        UseProxy = true,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = trustProxyCert ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator : null,
    });

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        AutoResponder.Dispose();
        _services.Dispose();
        try { Directory.Delete(Paths.Root, recursive: true); } catch { }
    }
}
