using System.Buffers.Binary;
using System.Text;
using Testy.Core;

namespace Testy.Cli.Mcp;

internal sealed partial class TestyMcpService
{
    private const int MaximumReportBytes = 8 * 1024 * 1024;

    public Task<McpToolResult> GetRunReportAsync(ToolArguments arguments, McpRequestContext context)
    {
        var id = RequireId(arguments.RequireString("runId", 100), "runId");
        var html = RunReportResource(id) ?? throw new McpToolException("This run has no completed report inside its workspace evidence folder.");
        return Task.FromResult(McpToolResult.Json(new { filename = "testy-run-" + id + ".html", mimeType = "text/html", html }));
    }

    /// <summary>Re-render a completed native report from recorded facts, with contained PNGs inlined. Stored HTML is never executed or copied.</summary>
    public string? RunReportResource(string id)
    {
        var run = TryFindRun(id);
        if (run is null || string.IsNullOrWhiteSpace(run.ArtifactDirectory) || HasParentSegment(run.ArtifactDirectory)) return null;
        var folder = Resolve(run.ArtifactDirectory);
        if (!Inside(Path.Combine(Workspace, "artifacts"), folder)
            || WorkspaceFile(Path.Combine(folder, "report.html"), ".html", folder) is null) return null;

        var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var embeddedBytes = 0;
        string? ImageUrl(string recorded)
        {
            var path = EvidenceFile(run, recorded);
            if (path is null || !Inside(folder, path)) return null;
            if (images.TryGetValue(path, out var cached)) return cached;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumReportBytes / 2) throw new McpToolException("A report screenshot exceeds the 4 MiB download limit. Open this report in native Studio.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new McpToolException("A recorded screenshot is not valid PNG evidence.");
            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (width == 0 || height == 0 || width > 8192 || height > 8192 || (ulong)width * height > 40_000_000)
                throw new McpToolException("A report screenshot exceeds the safe image dimensions for download. Open this report in native Studio.");
            var url = "data:image/png;base64," + Convert.ToBase64String(bytes);
            embeddedBytes += url.Length;
            if (embeddedBytes > MaximumReportBytes) throw new McpToolException("This report exceeds the 8 MiB download limit. Open it in native Studio.");
            images[path] = url;
            return url;
        }

        string html;
        try { html = TestRunner.RenderHtmlReport(run, ImageUrl, MaximumReportBytes); }
        catch (InvalidDataException) { throw new McpToolException("This report exceeds the 8 MiB download limit. Open it in native Studio."); }
        // Defense in depth for downloaded evidence. The renderer already escapes every recorded string.
        const string policy = "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">";
        html = html.Replace("<meta charset='utf-8'>", "<meta charset='utf-8'>" + policy, StringComparison.Ordinal);
        if (Encoding.UTF8.GetByteCount(html) > MaximumReportBytes) throw new McpToolException("This report exceeds the 8 MiB download limit. Open it in native Studio.");
        return html;
    }
}
