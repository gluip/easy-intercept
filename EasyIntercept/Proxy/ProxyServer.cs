using System.Net;
using System.Net.Sockets;
using EasyIntercept.AutoResponder;
using EasyIntercept.Certificates;
using EasyIntercept.Hubs;
using EasyIntercept.Pins;
using EasyIntercept.Storage;
using Microsoft.AspNetCore.SignalR;

namespace EasyIntercept.Proxy;

public class ProxyServer : BackgroundService
{
    private readonly ILogger<ProxyServer> _logger;
    private readonly SessionStore _sessions;
    private readonly PinStore _pins;
    private readonly AutoResponderStore _autoResponder;
    private readonly RecordingStore _recordings;
    private readonly AnalysisStore _analysis;
    private readonly IHubContext<ProxyHub> _hub;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CertificateService _certs;

    public ProxyServer(
        ILogger<ProxyServer> logger,
        SessionStore sessions,
        PinStore pins,
        AutoResponderStore autoResponder,
        RecordingStore recordings,
        AnalysisStore analysis,
        IHubContext<ProxyHub> hub,
        IHttpClientFactory httpClientFactory,
        CertificateService certs)
    {
        _logger = logger;
        _sessions = sessions;
        _pins = pins;
        _autoResponder = autoResponder;
        _recordings = recordings;
        _analysis = analysis;
        _hub = hub;
        _httpClientFactory = httpClientFactory;
        _certs = certs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, 8888);
        listener.Start();

        _logger.LogInformation("EasyIntercept proxy  →  http://localhost:8888");
        _logger.LogInformation("EasyIntercept web UI →  http://localhost:8080");

        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error accepting TCP connection");
                continue;
            }

            _ = Task.Run(async () =>
            {
                var conn = new ProxyConnection(client, _sessions, _pins, _autoResponder, _recordings, _analysis, _hub, _httpClientFactory, _certs);
                try
                {
                    await conn.HandleAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Connection handler error");
                }
            }, stoppingToken);
        }

        listener.Stop();
        _logger.LogInformation("Proxy stopped");
    }
}
