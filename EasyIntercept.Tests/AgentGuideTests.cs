using EasyIntercept;
using EasyIntercept.Hosting;
using Xunit;

namespace EasyIntercept.Tests;

public class AgentGuideTests
{
    private static AppPaths TempPaths() =>
        new(Path.Combine(Path.GetTempPath(), "easyintercept-guide-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void States_the_actual_ports()
    {
        var md = AgentGuide.Render(4242, TempPaths());

        Assert.Contains("127.0.0.1:9999", md);
        Assert.Contains("localhost:4242", md);
        Assert.DoesNotContain("localhost:1337", md); // must follow the configured UI port, not the default
    }

    [Fact]
    public void Includes_folders_for_local_callers()
    {
        var paths = TempPaths();
        var md = AgentGuide.Render(1337, paths);

        Assert.Contains(paths.Sessions, md);
        Assert.Contains(paths.AutoResponder, md);
    }

    [Fact]
    public void Omits_folders_for_remote_callers()
    {
        var paths = TempPaths();
        var md = AgentGuide.Render(1337, null);

        Assert.DoesNotContain(paths.Root, md);
        Assert.Contains("loopback", md, StringComparison.OrdinalIgnoreCase);
    }

    // Every route the app maps must be documented. Add a new endpoint → add it here and to the guide.
    [Theory]
    [InlineData("/api/info")]
    [InlineData("/api/sessions")]
    [InlineData("/api/sessions/delete")]
    [InlineData("/api/sessions/{id}/replay")]
    [InlineData("/api/sessions/{id}/file-path")]
    [InlineData("/api/sessions/{id}/show-in-explorer")]
    [InlineData("/api/bruno/export")]
    [InlineData("/api/auto-responders")]
    [InlineData("/api/auto-responders/{id}")]
    [InlineData("/api/system-proxy")]
    [InlineData("/api/browser-launch")]
    [InlineData("/ca")]
    [InlineData("/install")]
    [InlineData("/proxy-hub")]
    [InlineData("/llms.txt")]
    [InlineData("/openapi/v1.json")]
    public void Documents_every_endpoint(string route)
    {
        var md = AgentGuide.Render(1337, TempPaths());
        Assert.Contains(route, md);
    }

    [Fact]
    public void Explains_the_casing_difference_between_disk_and_api()
    {
        var md = AgentGuide.Render(1337, TempPaths());

        Assert.Contains("PascalCase", md);
        Assert.Contains("camelCase", md);
        Assert.Contains("\"ResponseStatus\"", md);
    }
}
