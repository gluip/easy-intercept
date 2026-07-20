using EasyIntercept.Export;
using EasyIntercept.Models;
using Xunit;

namespace EasyIntercept.Tests;

public class BrunoExporterTests
{
    private static ProxySession JsonPost => new()
    {
        Method = "POST",
        Url = "https://api.example.com/users?verbose=true",
        RequestHeaders = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["Authorization"] = "Bearer token123",
            ["Host"] = "api.example.com",
            ["Content-Length"] = "24",
        },
        RequestBody = """{"name":"jan","age":42}""",
    };

    [Fact]
    public void ToBru_WritesMetaBlock_WithMethodAndPath()
    {
        var bru = BrunoExporter.ToBru(JsonPost);

        Assert.Contains("meta {", bru);
        Assert.Contains("  name: POST /users", bru);
        Assert.Contains("  type: http", bru);
        Assert.Contains("  seq: 1", bru);
    }

    [Fact]
    public void ToBru_WritesMethodBlock_WithFullUrlAndBodyType()
    {
        var bru = BrunoExporter.ToBru(JsonPost);

        Assert.Contains("post {", bru);
        Assert.Contains("  url: https://api.example.com/users?verbose=true", bru);
        Assert.Contains("  body: json", bru);
        Assert.Contains("  auth: none", bru);
    }

    [Fact]
    public void ToBru_KeepsHeaders_ButSkipsHostAndContentLength()
    {
        var bru = BrunoExporter.ToBru(JsonPost);

        Assert.Contains("headers {", bru);
        Assert.Contains("  Content-Type: application/json", bru);
        Assert.Contains("  Authorization: Bearer token123", bru);
        Assert.DoesNotContain("Host:", bru);
        Assert.DoesNotContain("Content-Length:", bru);
    }

    [Fact]
    public void ToBru_PrettyPrintsJsonBody_IndentedInsideBlock()
    {
        var bru = BrunoExporter.ToBru(JsonPost);

        Assert.Contains("body:json {", bru);
        Assert.Contains("    \"name\": \"jan\"", bru);
        Assert.Contains("    \"age\": 42", bru);
    }

    [Fact]
    public void ToBru_GetWithoutBody_HasBodyNoneAndNoBodyBlock()
    {
        var session = new ProxySession
        {
            Method = "GET",
            Url = "https://api.example.com/users",
            RequestHeaders = new Dictionary<string, string> { ["Accept"] = "application/json" },
            RequestBody = "",
        };

        var bru = BrunoExporter.ToBru(session);

        Assert.Contains("get {", bru);
        Assert.Contains("  body: none", bru);
        Assert.DoesNotContain("body:none", bru);
        Assert.DoesNotContain("body:json", bru);
        Assert.DoesNotContain("body:text", bru);
    }

    [Fact]
    public void ToBru_XmlContentType_UsesXmlBodyBlock()
    {
        var session = new ProxySession
        {
            Method = "POST",
            Url = "https://api.example.com/soap",
            RequestHeaders = new Dictionary<string, string> { ["Content-Type"] = "text/xml; charset=utf-8" },
            RequestBody = "<envelope><body/></envelope>",
        };

        var bru = BrunoExporter.ToBru(session);

        Assert.Contains("  body: xml", bru);
        Assert.Contains("body:xml {", bru);
        Assert.Contains("  <envelope><body/></envelope>", bru);
    }

    [Fact]
    public void ToBru_UnknownContentType_FallsBackToTextBody()
    {
        var session = new ProxySession
        {
            Method = "POST",
            Url = "https://api.example.com/upload",
            RequestHeaders = new Dictionary<string, string> { ["Content-Type"] = "text/plain" },
            RequestBody = "hello world",
        };

        var bru = BrunoExporter.ToBru(session);

        Assert.Contains("  body: text", bru);
        Assert.Contains("body:text {", bru);
        Assert.Contains("  hello world", bru);
    }

    [Fact]
    public void ToBru_NdjsonBody_IsKeptVerbatim()
    {
        var session = new ProxySession
        {
            Method = "POST",
            Url = "https://es.example.com/logs/_bulk",
            RequestHeaders = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            RequestBody = "{\"create\":{}}\n{\"message\":\"hi\"}",
        };

        var bru = BrunoExporter.ToBru(session);

        // Not valid single-document JSON, so no pretty-printing: lines stay as-is
        Assert.Contains("  {\"create\":{}}", bru);
        Assert.Contains("  {\"message\":\"hi\"}", bru);
    }

    [Fact]
    public void ToBru_ContentTypeLookup_IsCaseInsensitive()
    {
        var session = new ProxySession
        {
            Method = "PUT",
            Url = "https://api.example.com/x",
            RequestHeaders = new Dictionary<string, string> { ["content-type"] = "application/json" },
            RequestBody = "{}",
        };

        Assert.Contains("  body: json", BrunoExporter.ToBru(session));
    }

    [Fact]
    public void ToBru_CustomName_IsUsedInMetaBlock()
    {
        var bru = BrunoExporter.ToBru(JsonPost, "Create user (v2)");

        Assert.Contains("  name: Create user (v2)", bru);
        Assert.DoesNotContain("  name: POST /users", bru);
    }

    [Fact]
    public void ToBru_BlankCustomName_FallsBackToDefault()
    {
        var bru = BrunoExporter.ToBru(JsonPost, "   ");

        Assert.Contains("  name: POST /users", bru);
    }

    [Fact]
    public void FileName_CustomName_IsSanitizedAndUsed()
    {
        var name = BrunoExporter.FileName(JsonPost, "Create user: v2?");

        Assert.Equal("Create user_ v2_.bru", name);
    }

    [Fact]
    public void FileName_BlankCustomName_FallsBackToDefault()
    {
        Assert.Equal("POST_users.bru", BrunoExporter.FileName(JsonPost, "  "));
    }

    [Fact]
    public void FileName_UsesMethodAndPath_WithBruExtension()
    {
        var name = BrunoExporter.FileName(JsonPost);

        Assert.Equal("POST_users.bru", name);
    }

    [Fact]
    public void FileName_ReplacesPathSeparatorsAndInvalidChars()
    {
        var session = new ProxySession
        {
            Method = "POST",
            Url = "https://es.example.com/logs-platform-default/_bulk?filter_path=a,b",
        };

        var name = BrunoExporter.FileName(session);

        Assert.Equal("POST_logs-platform-default__bulk.bru", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("?", name);
    }

    [Fact]
    public void FileName_RootPath_FallsBackToHost()
    {
        var session = new ProxySession { Method = "GET", Url = "https://api.example.com/" };

        Assert.Equal("GET_api.example.com.bru", BrunoExporter.FileName(session));
    }

    [Fact]
    public void FileName_InvalidUrl_FallsBackToRequest()
    {
        var session = new ProxySession { Method = "GET", Url = "not a url" };

        Assert.Equal("GET_request.bru", BrunoExporter.FileName(session));
    }

    [Fact]
    public void FileName_LongPath_IsTruncated()
    {
        var session = new ProxySession
        {
            Method = "GET",
            Url = "https://api.example.com/" + new string('a', 300),
        };

        var name = BrunoExporter.FileName(session);

        Assert.True(name.Length <= 104, $"length was {name.Length}");
        Assert.EndsWith(".bru", name);
    }
}
