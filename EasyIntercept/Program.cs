using EasyIntercept.AutoResponder;
using EasyIntercept.Certificates;
using EasyIntercept.Hubs;
using EasyIntercept.Models;
using EasyIntercept.Persistence;
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
        Proxy = new System.Net.WebProxy("http://localhost:8888"),
        UseProxy = true,
    });

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<PinStore>();
builder.Services.AddSingleton<JsonPersistence>();
builder.Services.AddSingleton<AutoResponderStore>();
builder.Services.AddSingleton<RecordingStore>();
builder.Services.AddSingleton<AnalysisStore>();
builder.Services.AddSingleton<CertificateService>();
builder.Services.AddHostedService<ProxyServer>();

var app = builder.Build();

// Initialize stores from disk
var persistence = app.Services.GetRequiredService<JsonPersistence>();
var autoStore = app.Services.GetRequiredService<AutoResponderStore>();
var recStore = app.Services.GetRequiredService<RecordingStore>();
var analysisStore = app.Services.GetRequiredService<AnalysisStore>();
autoStore.Init();
recStore.Init();
analysisStore.Init();

// Live reload on external file changes
persistence.OnAutoResponderChanged = () => autoStore.ReloadFromDisk();
persistence.OnRecordingsChanged = () => recStore.ReloadFromDisk();
persistence.OnAnalysisChanged = () => analysisStore.ReloadFromDisk();
persistence.StartWatching();

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

// Recording CRUD
app.MapGet("/api/recordings", (RecordingStore store) =>
    Results.Ok(store.GetAll().Select(r => new
    {
        r.Id, r.Name, r.CreatedAt, r.Active,
        RulesCount = r.Rules.Count,
    })));

app.MapPost("/api/recordings", (Recording input, RecordingStore store) =>
    Results.Ok(store.Create(input.Name)));

app.MapDelete("/api/recordings/{id:guid}", (Guid id, RecordingStore store) =>
    store.Delete(id) ? Results.Ok() : Results.NotFound());

app.MapPut("/api/recordings/{id:guid}", (Guid id, Recording input, RecordingStore store) =>
{
    var rec = store.Rename(id, input.Name);
    return rec != null ? Results.Ok(rec) : Results.NotFound();
});

app.MapPost("/api/recordings/{id:guid}/activate", (Guid id, RecordingStore store) =>
    store.Activate(id) ? Results.Ok() : Results.NotFound());

app.MapPost("/api/recordings/{id:guid}/deactivate", (Guid id, RecordingStore store) =>
    store.Deactivate(id) ? Results.Ok() : Results.NotFound());

app.MapPost("/api/recordings/start", (Recording input, RecordingStore store) =>
{
    var rec = store.StartRecording(input.Name);
    return Results.Ok(rec);
});

app.MapPost("/api/recordings/stop", (RecordingStore store) =>
{
    store.StopRecording();
    return Results.Ok();
});

app.MapGet("/api/recordings/status", (RecordingStore store) =>
    Results.Ok(new { RecordingId = store.RecordingId, ActiveId = store.ActiveId }));

app.MapGet("/api/recordings/{id:guid}/rules", (Guid id, RecordingStore store) =>
{
    var rec = store.Get(id);
    return rec != null ? Results.Ok(rec.Rules) : Results.NotFound();
});

app.MapPut("/api/recordings/{id:guid}/rules/{ruleId:guid}",
    (Guid id, Guid ruleId, AutoResponderRule rule, RecordingStore store) =>
{
    rule.Id = ruleId;
    return store.UpdateRule(id, rule) ? Results.Ok(rule) : Results.NotFound();
});

app.MapPost("/api/recordings/{id:guid}/rules/{ruleId:guid}/toggle",
    (Guid id, Guid ruleId, RecordingStore store) =>
    store.ToggleRule(id, ruleId) ? Results.Ok() : Results.NotFound());

app.MapDelete("/api/recordings/{id:guid}/rules/{ruleId:guid}",
    (Guid id, Guid ruleId, RecordingStore store) =>
    store.DeleteRule(id, ruleId) ? Results.Ok() : Results.NotFound());

// Analysis
app.MapGet("/api/analysis/runs", (AnalysisStore store) =>
    Results.Ok(store.GetAll()));

app.MapGet("/api/analysis/status", (AnalysisStore store) =>
    Results.Ok(store.GetStatus()));

app.MapPost("/api/analysis/start", (AnalysisRun input, AnalysisStore store) =>
    Results.Ok(store.StartRun(input.Name, input.HostFilter)));

app.MapPost("/api/analysis/stop", (AnalysisStore store) =>
{
    store.StopRun();
    return Results.Ok();
});

app.MapDelete("/api/analysis/runs/{id:guid}", (Guid id, AnalysisStore store) =>
    store.Delete(id) ? Results.Ok() : Results.NotFound());

app.MapGet("/api/analysis/runs/{id:guid}/events", (Guid id, AnalysisStore store) =>
{
    var run = store.Get(id);
    return run != null ? Results.Ok(store.GetEventSummaries(id)) : Results.NotFound();
});

app.MapGet("/api/analysis/runs/{id:guid}/events/{sequence:int}", (Guid id, int sequence, AnalysisStore store) =>
{
    var analysisEvent = store.GetEvent(id, sequence);
    return analysisEvent != null ? Results.Ok(analysisEvent) : Results.NotFound();
});

await app.RunAsync();
