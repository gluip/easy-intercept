using System.Reflection;
using EasyIntercept;
using EasyIntercept.AutoResponder;
using EasyIntercept.Certificates;
using EasyIntercept.Export;
using EasyIntercept.Hosting;
using EasyIntercept.Hubs;
using EasyIntercept.Proxy;
using EasyIntercept.Storage;
using Microsoft.Extensions.Configuration.Json;

var options = StartupOptions.Parse(args);
args = StartupOptions.StripOwnFlags(args);

#if WINDOWS
// WinExe: no console of our own, but reuse the terminal's when started from one (dotnet run, pwsh).
EasyIntercept.Desktop.ConsoleAttach.TryAttachParentConsole();
#endif

// Published/installed builds ship wwwroot + appsettings*.json next to the exe; serve from there
// regardless of the working directory (Start Menu, HKLM Run, terminal). Dev builds (dotnet run)
// have no wwwroot in bin/, so they keep ASP.NET's default: the project directory.
var exeDir = AppContext.BaseDirectory;
var builder = Directory.Exists(Path.Combine(exeDir, "wwwroot"))
    ? WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = exeDir })
    : WebApplication.CreateBuilder(args);

// User-level overrides live in <DataRoot>\appsettings.json (e.g. UiPort chosen at runtime).
// Inserted after the shipped appsettings*.json so env vars and command line still win.
var dataRoot = AppPaths.ResolveRoot(builder.Configuration["DataRoot"]);
Directory.CreateDirectory(dataRoot);
{
    var sources = builder.Configuration.Sources;
    var userSettings = new JsonConfigurationSource
    {
        Path = Path.Combine(dataRoot, "appsettings.json"),
        Optional = true,
        ReloadOnChange = false,
    };
    userSettings.ResolveFileProvider();
    var insertAt = sources.ToList().FindLastIndex(s => s is JsonConfigurationSource) + 1;
    sources.Insert(insertAt, userSettings);
}

if (options.InstallCa)
    return CaInstaller.Run(new AppPaths(builder.Configuration));

var uiPort = StartupOptions.GetUiPort(builder.Configuration);

// Single instance: a second launch just brings up the UI of the running one.
using var instanceMutex = new Mutex(true, @"Local\EasyIntercept", out var isFirstInstance);
if (!isFirstInstance)
{
    if (options.ShouldOpenBrowser) Launcher.OpenUrl(StartupOptions.UiUrl(uiPort));
    return 0;
}

if (!Launcher.IsPortFree(uiPort))
{
    int? chosen = null;
#if WINDOWS
    if (!options.NoTray)
    {
        chosen = EasyIntercept.Desktop.PortPrompt.Show(uiPort, Launcher.FindFreePort(uiPort + 1));
        if (chosen is int p)
        {
            StartupOptions.SaveUserSetting(Path.Combine(dataRoot, "appsettings.json"), "UiPort", p);
            builder.Configuration["UiPort"] = p.ToString();
        }
    }
#endif
    if (chosen is null)
    {
        Console.Error.WriteLine($"Port {uiPort} is already in use. Start with --UiPort=<port> or set UiPort in {Path.Combine(dataRoot, "appsettings.json")}.");
        return 1;
    }
    uiPort = chosen.Value;
}

builder.WebHost.UseUrls($"http://*:{uiPort}");

// Graceful shutdown would otherwise wait up to 30 s for open proxy/SignalR connections to drain,
// which makes "Exit" in the tray look like it does nothing.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3));

builder.Services.AddSignalR();

builder.Services.AddHttpClient("proxy", c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() =>
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
        Proxy = new System.Net.WebProxy("http://localhost:9999"),
        UseProxy = true,
    });

builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<CertificateService>();
builder.Services.AddSingleton<AutoResponderStore>();
builder.Services.AddSingleton<SystemProxyService>();
builder.Services.AddSingleton<BrowserLauncherService>();
builder.Services.AddHostedService<ProxyServer>();
#if WINDOWS
if (!options.NoTray)
    builder.Services.AddHostedService<EasyIntercept.Desktop.TrayIconService>();
#endif

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<ProxyHub>("/proxy-hub");

app.MapGet("/api/info", (AppPaths paths) =>
{
    var informational = Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var version = informational?.Split('+')[0] ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "dev";
    return Results.Ok(new
    {
        version,
        uiPort,
        proxyPort = StartupOptions.ProxyPort,
        dataRoot = paths.Root,
        sessionsPath = paths.Sessions,
        autoResponderPath = paths.AutoResponder,
    });
});

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
    {
        var contentType = session.RequestHeaders.GetValueOrDefault("Content-Type", "application/octet-stream");
        request.Content = new StringContent(session.RequestBody, System.Text.Encoding.UTF8);
        request.Content.Headers.ContentType = null;
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
    }

    var resp = await client.SendAsync(request);
    var body = await resp.Content.ReadAsStringAsync();
    return Results.Ok(new { status = (int)resp.StatusCode, body });
});

app.MapGet("/api/sessions/{id:guid}/file-path", (Guid id, SessionStore store) =>
{
    var path = store.GetFilePath(id);
    if (path is null) return Results.NotFound();
    return Results.Ok(new { path });
});

app.MapPost("/api/sessions/{id:guid}/show-in-explorer", (Guid id, SessionStore store) =>
{
    var path = store.GetFilePath(id);
    if (path is null || !File.Exists(path)) return Results.NotFound();
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "explorer.exe",
        Arguments = $"/select,\"{path}\"",
        UseShellExecute = true,
    });
    return Results.Ok();
});

app.MapPost("/api/bruno/export", (BrunoExportRequest body, SessionStore store) =>
{
    if (string.IsNullOrWhiteSpace(body.CollectionPath) || !Directory.Exists(body.CollectionPath))
        return Results.BadRequest($"Folder does not exist: {body.CollectionPath}");

    // custom name only makes sense for a single request
    var customName = body.SessionIds.Length == 1 ? body.Name : null;

    var written = new List<string>();
    foreach (var id in body.SessionIds)
    {
        var session = store.Get(id);
        if (session is null) continue;
        var baseName = Path.GetFileNameWithoutExtension(BrunoExporter.FileName(session, customName));
        var filePath = Path.Combine(body.CollectionPath, baseName + ".bru");
        for (var n = 2; File.Exists(filePath); n++)
            filePath = Path.Combine(body.CollectionPath, $"{baseName}_{n}.bru");
        File.WriteAllText(filePath, BrunoExporter.ToBru(session, customName));
        written.Add(Path.GetFileName(filePath));
    }

    return written.Count > 0
        ? Results.Ok(new { files = written })
        : Results.NotFound("No matching sessions found");
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

app.MapGet("/api/system-proxy", (SystemProxyService proxy) =>
    Results.Ok(new { enabled = proxy.IsEnabled() }));

app.MapPost("/api/system-proxy", (SystemProxyEnableRequest body, SystemProxyService proxy) =>
{
    if (body.Enabled) proxy.Enable();
    else proxy.Disable();
    return Results.Ok(new { enabled = proxy.IsEnabled() });
});

// Project to id/name only — ExePath is for Launch() internally and would leak
// local filesystem paths (and the username) to the browser.
app.MapGet("/api/browser-launch", (BrowserLauncherService launcher) =>
    Results.Ok(new { browsers = launcher.DetectBrowsers().Select(b => new { b.Id, b.Name }) }));

app.MapPost("/api/browser-launch", (BrowserLaunchRequest body, BrowserLauncherService launcher) =>
{
    try
    {
        launcher.Launch(body.BrowserId);
        return Results.Ok();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

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
          <p>Proxy address: <code>{{ctx.Request.Host.Host}}:{{StartupOptions.ProxyPort}}</code></p>
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
            <li>Set proxy to <code>{{ctx.Request.Host.Host}}:{{StartupOptions.ProxyPort}}</code> under Wi-Fi settings</li>
          </ol>
        </body>
        </html>
        """;
    ctx.Response.ContentType = "text/html";
    await ctx.Response.WriteAsync(html);
});

if (options.ShouldOpenBrowser)
    app.Lifetime.ApplicationStarted.Register(() => Launcher.OpenUrl(StartupOptions.UiUrl(uiPort)));

await app.RunAsync();
GC.KeepAlive(instanceMutex);
return 0;

record SystemProxyEnableRequest(bool Enabled);
record BrowserLaunchRequest(string BrowserId);
record BrunoExportRequest(Guid[] SessionIds, string CollectionPath, string? Name = null);
