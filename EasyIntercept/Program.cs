using EasyIntercept.AutoResponder;
using EasyIntercept.Certificates;
using EasyIntercept.Hubs;
using EasyIntercept.Pins;
using EasyIntercept.Proxy;
using EasyIntercept.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:8080");

builder.Services.AddSignalR();

builder.Services.AddHttpClient("proxy").ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        UseProxy = false,
    });

builder.Services.AddHttpClient("replay").ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    });

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<PinStore>();
builder.Services.AddSingleton<AutoResponderStore>();
builder.Services.AddSingleton<CertificateService>();
builder.Services.AddHostedService<ProxyServer>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<ProxyHub>("/proxy-hub");

app.MapGet("/api/sessions", (SessionStore store) =>
    Results.Ok(store.GetAll()));

app.MapPost("/api/sessions/{id:guid}/replay", async (Guid id, SessionStore store, IHttpClientFactory httpFactory) =>
{
    var session = store.Get(id);
    if (session is null) return Results.NotFound();

    using var client = httpFactory.CreateClient("replay");
    var request = new HttpRequestMessage(new HttpMethod(session.Method), session.Url);
    foreach (var (k, v) in session.RequestHeaders)
    {
        if (k.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
        request.Headers.TryAddWithoutValidation(k, v);
    }
    if (!string.IsNullOrEmpty(session.RequestBody))
        request.Content = new StringContent(session.RequestBody, System.Text.Encoding.UTF8,
            session.RequestHeaders.GetValueOrDefault("Content-Type", "application/octet-stream"));

    var resp = await client.SendAsync(request);
    var body = await resp.Content.ReadAsStringAsync();
    return Results.Ok(new { status = (int)resp.StatusCode, body });
});

app.MapPost("/api/sessions/{id:guid}/pin", (Guid id, SessionStore store, PinStore pins) =>
{
    var session = store.Get(id);
    if (session is null) return Results.NotFound();

    pins.Pin(session.Url, new PinnedResponse
    {
        StatusCode = session.ResponseStatus,
        Headers = session.ResponseHeaders,
        Body = session.ResponseBody,
    });

    return Results.Ok(new { pinned = session.Url });
});

app.MapDelete("/api/pins", (string url, PinStore pins) =>
{
    pins.Unpin(url);
    return Results.Ok();
});

app.MapGet("/api/pins", (PinStore pins) =>
    Results.Ok(pins.GetAll()));

// Auto-responder CRUD
app.MapGet("/api/auto-responder", (AutoResponderStore store) =>
    Results.Ok(store.GetAll()));

app.MapPost("/api/auto-responder", (AutoResponderRule rule, AutoResponderStore store) =>
{
    rule.Id = Guid.NewGuid();
    store.AddOrUpdate(rule);
    return Results.Ok(rule);
});

app.MapPut("/api/auto-responder/{id:guid}", (Guid id, AutoResponderRule rule, AutoResponderStore store) =>
{
    if (store.Get(id) is null) return Results.NotFound();
    rule.Id = id;
    store.AddOrUpdate(rule);
    return Results.Ok(rule);
});

app.MapDelete("/api/auto-responder/{id:guid}", (Guid id, AutoResponderStore store) =>
{
    store.Remove(id);
    return Results.Ok();
});

app.MapGet("/ca", (CertificateService certs) =>
{
    var path = certs.CaCertPath;
    if (!File.Exists(path)) return Results.NotFound();
    return Results.Bytes(File.ReadAllBytes(path), "application/x-x509-ca-cert", "easyntercept-ca.crt");
});

await app.RunAsync();
