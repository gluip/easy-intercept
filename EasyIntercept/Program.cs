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
    });

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<PinStore>();
builder.Services.AddHostedService<ProxyServer>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<ProxyHub>("/proxy-hub");

app.MapGet("/api/sessions", (SessionStore store) =>
    Results.Ok(store.GetAll()));

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

await app.RunAsync();
