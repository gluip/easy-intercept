using EasyIntercept.AutoResponder;
using EasyIntercept.Certificates;
using EasyIntercept.Hubs;
using EasyIntercept.Proxy;
using EasyIntercept.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://*:8080");

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
        Proxy = new System.Net.WebProxy("http://localhost:8888"),
        UseProxy = true,
    });

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<CertificateService>();
builder.Services.AddSingleton<AutoResponderStore>();
builder.Services.AddHostedService<ProxyServer>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<ProxyHub>("/proxy-hub");

app.MapGet("/api/sessions", (SessionStore store) =>
    Results.Ok(store.GetAll()));

app.MapDelete("/api/sessions", (SessionStore store) =>
{
    store.Clear();
    return Results.Ok();
});

app.MapPost("/api/sessions/delete", (Guid[] ids, SessionStore store) =>
{
    store.RemoveMany(ids);
    return Results.Ok();
});

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

app.MapGet("/api/auto-responders", (AutoResponderStore store) =>
    Results.Ok(store.GetAll()));

app.MapPost("/api/auto-responders", (AutoResponderRule rule, AutoResponderStore store) =>
{
    store.Add(rule);
    return Results.Ok(rule);
});

app.MapPut("/api/auto-responders/{id:guid}", (Guid id, AutoResponderRule rule, AutoResponderStore store) =>
{
    if (rule.Id != id) return Results.BadRequest("Id mismatch");
    return store.Update(id, rule) ? Results.Ok(rule) : Results.NotFound();
});

app.MapDelete("/api/auto-responders/{id:guid}", (Guid id, AutoResponderStore store) =>
    store.Remove(id) ? Results.Ok() : Results.NotFound());

app.MapGet("/ca", (CertificateService certs) =>
{
    var path = certs.CaCertPath;
    if (!File.Exists(path)) return Results.NotFound();
    return Results.Bytes(File.ReadAllBytes(path), "application/x-x509-ca-cert", "easyntercept-ca.crt");
});

app.MapGet("/install", async (HttpContext ctx) =>
{
    var host = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var caUrl = $"{host}/ca";
    var html = $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8"/>
          <meta name="viewport" content="width=device-width, initial-scale=1"/>
          <title>Install EasyIntercept CA</title>
          <style>
            body { font-family: -apple-system, sans-serif; max-width: 480px; margin: 40px auto; padding: 0 20px; text-align: center; }
            h1 { font-size: 1.4rem; }
            .btn { display: inline-block; margin: 20px 0; padding: 14px 28px; background: #007aff; color: #fff;
                   border-radius: 12px; text-decoration: none; font-size: 1.1rem; }
            .qr { margin: 20px auto; }
            ol { text-align: left; line-height: 1.8; }
            code { background: #f0f0f0; padding: 2px 6px; border-radius: 4px; }
          </style>
          <script src="https://cdn.jsdelivr.net/npm/qrcodejs@1.0.0/qrcode.min.js"></script>
        </head>
        <body>
          <h1>Install EasyIntercept CA Certificate</h1>
          <p>Proxy address: <code>{{ctx.Request.Host.Host}}:8888</code></p>
          <div id="qr" class="qr"></div>
          <script>new QRCode(document.getElementById("qr"), { text: "{{caUrl}}", width: 200, height: 200 });</script>
          <a class="btn" href="/ca">Download &amp; Install Certificate</a>
          <ol>
            <li>Scan the QR-code or tap the button above <strong>in Safari</strong></li>
            <li>Tap <em>Allow</em> when asked to download a profile</li>
            <li>Go to <strong>Settings → General → VPN &amp; Device Management</strong></li>
            <li>Tap the <em>EasyIntercept</em> profile → <em>Install</em></li>
            <li>Go to <strong>Settings → General → About → Certificate Trust Settings</strong></li>
            <li>Enable full trust for <em>EasyIntercept CA</em></li>
            <li>Set proxy to <code>{{ctx.Request.Host.Host}}:8888</code> under Wi-Fi settings</li>
          </ol>
        </body>
        </html>
        """;
    ctx.Response.ContentType = "text/html";
    await ctx.Response.WriteAsync(html);
});

await app.RunAsync();
