using System.Text;
using System.Text.Json;
using EasyIntercept.Models;

namespace EasyIntercept.Export;

public static class BrunoExporter
{
    public static string ToBru(ProxySession session, string? name = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("meta {");
        sb.AppendLine($"  name: {(string.IsNullOrWhiteSpace(name) ? DefaultName(session) : name.Trim())}");
        sb.AppendLine("  type: http");
        sb.AppendLine("  seq: 1");
        sb.AppendLine("}");
        sb.AppendLine();

        var bodyType = DetectBodyType(session);
        sb.AppendLine($"{session.Method.ToLowerInvariant()} {{");
        sb.AppendLine($"  url: {session.Url}");
        sb.AppendLine($"  body: {bodyType}");
        sb.AppendLine("  auth: none");
        sb.AppendLine("}");

        var headers = session.RequestHeaders
            .Where(h => !h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                     && !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (headers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("headers {");
            foreach (var (key, value) in headers)
                sb.AppendLine($"  {key}: {value}");
            sb.AppendLine("}");
        }

        if (bodyType != "none")
        {
            sb.AppendLine();
            sb.AppendLine($"body:{bodyType} {{");
            foreach (var line in FormatBody(session.RequestBody, bodyType).Split('\n'))
                sb.AppendLine("  " + line.TrimEnd('\r'));
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    public static string DefaultName(ProxySession session) =>
        $"{session.Method} {UrlPath(session.Url)}";

    // Collections are shared through git across operating systems, so strip what any of them rejects.
    // Path.GetInvalidFileNameChars() only knows the current OS: on macOS that is just '/' and NUL.
    private static readonly char[] InvalidFileNameChars =
        [.. "<>:\"/\\|?*", .. Enumerable.Range(0, 32).Select(i => (char)i)];

    // Device names Windows refuses as a file name, with or without an extension ("con.bru", "lpt1.x.bru")
    private static readonly HashSet<string> ReservedWindowsNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(0, 10).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
        StringComparer.OrdinalIgnoreCase);

    public static string FileName(ProxySession session, string? name = null)
    {
        string baseName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            baseName = name.Trim();
        }
        else
        {
            try
            {
                var uri = new Uri(session.Url);
                var path = uri.AbsolutePath.Trim('/');
                baseName = string.IsNullOrEmpty(path) ? uri.Host : path;
            }
            catch
            {
                baseName = "request";
            }
            baseName = $"{session.Method}_{baseName}";
        }
        foreach (var c in InvalidFileNameChars)
            baseName = baseName.Replace(c, '_');
        if (baseName.Length > 100) baseName = baseName[..100];
        // Windows drops trailing dots and spaces, so such a name can't be checked out there (trim after truncating)
        baseName = baseName.TrimEnd('.', ' ');
        if (baseName.Length == 0) baseName = "request";
        if (ReservedWindowsNames.Contains(baseName.Split('.')[0].TrimEnd(' '))) baseName = "_" + baseName;
        return baseName + ".bru";
    }

    private static string UrlPath(string url)
    {
        try { return new Uri(url).AbsolutePath; } catch { return url; }
    }

    private static string DetectBodyType(ProxySession session)
    {
        if (string.IsNullOrEmpty(session.RequestBody)) return "none";
        var contentType = session.RequestHeaders
            .FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            .Value ?? "";
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return "json";
        if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)) return "xml";
        return "text";
    }

    private static string FormatBody(string body, string bodyType)
    {
        if (bodyType != "json") return body;
        try
        {
            // NDJSON bodies (e.g. ES _bulk) won't parse and are kept as-is
            using var doc = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return body;
        }
    }
}
