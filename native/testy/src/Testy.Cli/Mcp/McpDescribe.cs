using System.Text.Json.Nodes;
using Testy.Core;

namespace Testy.Cli.Mcp;

/// <summary>
/// Machine-readable discovery: the --describe manifest (how to start and connect to this server, what it offers) and an
/// MCP Registry style server.json for the portable bundle. Neither needs a running server or a workspace.
/// </summary>
internal static class McpDescribe
{
    public const string ManifestSchema = "testy.mcp-describe.v1";
    public const string RegistrySchema = "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json";
    /// <summary>
    /// The reverse-DNS namespace of the server's registry name. A registry verifies that the publisher controls the namespace, so whoever
    /// publishes this server sets it to a domain or account of their own; nothing else in the product depends on its value.
    /// </summary>
    public const string RegistryNamespace = "io.testy";
    public const string RegistryName = RegistryNamespace + "/testy";
    /// <summary>The key the registry schema reserves for data a publisher adds; the local launch details live under it.</summary>
    public const string PublisherMetaKey = "io.modelcontextprotocol.registry/publisher-provided";
    public const string LocalServerKey = "localServer";
    /// <summary>At most 100 characters: the registry schema's limit for description.</summary>
    public const string Description = "Windows desktop UI testing: inspect apps, perform UI steps, create and run tests, read evidence.";
    public const string Framing = "JSON-RPC 2.0 over stdin/stdout, UTF-8, one message per line (newline-delimited, no embedded newlines; a leading byte order mark and CRLF are tolerated; a line may be at most 32 MiB); stderr carries logs only.";
    public const string HttpStartup = "The server prints one JSON line to stdout when ready: {\"transport\":\"http\",\"url\":\"http://127.0.0.1:<port>/mcp\",\"port\":<port>,\"tokenRequired\":<bool>,\"pid\":<pid>,...} and writes the same facts to <workspace>\\mcp\\endpoint.json (removed when it stops). It binds loopback only and runs until its stdin closes, the --parent-pid process exits, or Ctrl+C; started without stdin (the NUL device) it runs until it is terminated.";
    public const string Security =
        "This server can start desktop (GUI) programs the caller names (by path, or by an app name it resolves to one program) and send keyboard and mouse input to the application the caller names by pid or app name, with the rights of the user who runs it. " +
        "Console programs, script hosts, shells, terminals and program launchers are refused, and so are arguments that name one; programs that are part of Windows itself are refused unless --allow-exe names them; only full local drive paths are accepted; --no-launch, --allow-exe and --allow-target narrow this further. These rules are not a sandbox. " +
        "HTTP binds 127.0.0.1 and ::1 only, rejects Origin and Host headers that are not loopback (403) and bodies that are not application/json (415), and enforces an optional bearer token (401). " +
        "Without a token any local process, including one of another Windows account on the same PC, can reach the endpoint: set " + McpOptions.TokenVariable + " on a shared PC.";

    /// <summary>The executable to start (Testy.Cli.exe, or dotnet + Testy.Cli.dll when running from the dll) and the arguments before the verb.</summary>
    public static (string Command, string[] Prefix) Launcher(bool relative)
    {
        var host = Environment.ProcessPath ?? "Testy.Cli.exe";
        var library = typeof(McpDescribe).Assembly.Location;
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (relative ? Path.GetFileName(host) : host, [relative ? Path.GetFileName(library) : library]);
        return (relative ? Path.GetFileName(host) : host, []);
    }

    public static JsonObject Manifest(McpToolCatalog tools, string workspace, string? sampleApp, bool relative, McpLaunchPolicy? policy = null)
    {
        var (command, prefix) = Launcher(relative);
        string[] StdioArgs() => [.. prefix, "mcp"];
        string[] HttpArgs() => [.. prefix, "mcp", "--transport", "http", "--port", "0"];
        JsonObject CommonOptions() => new()
        {
            ["--workspace DIR"] = "Testy workspace to read and write (default: " + (relative ? @"%LOCALAPPDATA%\Testy\Workspace" : AgentFiles.DefaultWorkspace) + ").",
            ["--parent-pid N"] = "Exit when process N exits.",
            ["--no-launch"] = "launch_app, run_test (exe or app) and the app tools with launch true start nothing.",
            ["--allow-exe PATH"] = "Start only this program, or programs in this folder (repeatable); also the only way to allow a program that is part of Windows itself.",
            ["--allow-target NAME"] = "Connect only to this process name or exe path (repeatable)."
        };
        var httpOptions = CommonOptions();
        httpOptions["--port N"] = "Loopback port; 0 picks a free port (default 0).";
        httpOptions[McpOptions.TokenVariable + " (environment)"] = "When set, every request needs Authorization: Bearer <value>. Preferred: a command line can be read by other programs of the same user.";
        httpOptions["--token T"] = "The same on the command line; it wins when both are given.";
        return new JsonObject
        {
            ["schema"] = ManifestSchema,
            ["name"] = McpServer.Name,
            ["title"] = McpServer.Title,
            ["version"] = McpServer.Version,
            ["description"] = Description,
            ["security"] = Security,
            ["protocolVersions"] = new JsonObject
            {
                ["all"] = JsonRpc.Strings(McpServer.SupportedVersions),
                ["handshake"] = JsonRpc.Strings(McpServer.HandshakeVersions),
                ["stateless"] = JsonRpc.Strings(McpServer.StatelessVersions),
                ["note"] = "Handshake revisions start with initialize; revision 2026-07-28 declares its version and client capabilities per request in _meta and may probe with server/discover. Both are served by the same process."
            },
            ["transports"] = new JsonObject
            {
                ["stdio"] = new JsonObject
                {
                    ["command"] = command, ["args"] = JsonRpc.Strings(StdioArgs()), ["framing"] = Framing,
                    ["options"] = CommonOptions(),
                    ["commandIsRelativeTo"] = relative ? "the folder containing this manifest (the Testy bundle)" : null
                },
                ["http"] = new JsonObject
                {
                    ["command"] = command, ["args"] = JsonRpc.Strings(HttpArgs()), ["urlTemplate"] = "http://127.0.0.1:{port}/mcp", ["startup"] = HttpStartup,
                    ["endpointFile"] = relative ? @"<workspace>\mcp\endpoint.json" : McpCommand.EndpointFile(workspace),
                    ["options"] = httpOptions,
                    ["protocol"] = "MCP Streamable HTTP: POST one JSON-RPC message with Content-Type: application/json to the URL. tools/call is answered as a text/event-stream when the Accept header lists it (closing the stream cancels the call), everything else as application/json. Handshake revisions receive an Mcp-Session-Id on initialize and send it on every later request (64 sessions at most, expired after 10 idle minutes); initialize on an existing session replaces it; DELETE ends the session; GET is not offered (405). Revision 2026-07-28 requests are stateless with MCP-Protocol-Version, Mcp-Method and Mcp-Name headers.",
                    ["security"] = Security
                }
            },
            ["clientConfiguration"] = new JsonObject
            {
                ["note"] = "A common MCP client configuration shape for a stdio server.",
                ["mcpServers"] = new JsonObject { ["testy"] = new JsonObject { ["command"] = command, ["args"] = JsonRpc.Strings(StdioArgs()) } }
            },
            ["launchPolicy"] = (policy ?? McpLaunchPolicy.Default).Describe(),
            ["defaultWorkspace"] = relative ? @"%LOCALAPPDATA%\Testy\Workspace" : AgentFiles.DefaultWorkspace,
            ["workspace"] = relative ? null : workspace,
            ["sampleApp"] = sampleApp,
            ["tools"] = new JsonArray(tools.Tools.Select(t => (JsonNode?)new JsonObject { ["name"] = t.Name, ["description"] = t.Description }).ToArray()),
            ["resources"] = new JsonArray(McpResources.List(null).Select(r => (JsonNode?)new JsonObject { ["uri"] = r!["uri"]!.GetValue<string>(), ["description"] = r["description"]!.GetValue<string>() }).ToArray()),
            ["resourceTemplates"] = new JsonArray(McpResources.Templates().Select(r => (JsonNode?)new JsonObject { ["uriTemplate"] = r!["uriTemplate"]!.GetValue<string>(), ["description"] = r["description"]!.GetValue<string>() }).ToArray()),
            ["prompts"] = new JsonArray(McpResources.Prompts().Select(p => (JsonNode?)new JsonObject { ["name"] = p!["name"]!.GetValue<string>(), ["description"] = p["description"]!.GetValue<string>() }).ToArray()),
            ["instructions"] = McpResources.Instructions
        };
    }

    /// <summary>
    /// An identity card in the MCP Registry server.json shape (name, description, version). A portable folder is neither a package registry
    /// entry nor a public remote, so there are no packages or remotes; the local launch details are publisher-provided data under _meta.
    /// `mcp --describe` (clientConfiguration) is the actionable source for a harness.
    /// </summary>
    public static JsonObject ServerJson(McpToolCatalog tools, bool relative)
    {
        var (command, prefix) = Launcher(relative);
        return new JsonObject
        {
            ["$schema"] = RegistrySchema,
            ["name"] = RegistryName,
            ["title"] = McpServer.Title,
            ["description"] = Description,
            ["version"] = McpServer.Version,
            ["_meta"] = new JsonObject
            {
                [PublisherMetaKey] = new JsonObject
                {
                    [LocalServerKey] = new JsonObject
                    {
                        ["kind"] = "locally installed executable (not published to a package registry)",
                        ["transports"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "stdio", ["command"] = command, ["args"] = JsonRpc.Strings([.. prefix, "mcp"]), ["framing"] = Framing },
                            new JsonObject { ["type"] = "streamable-http", ["command"] = command, ["args"] = JsonRpc.Strings([.. prefix, "mcp", "--transport", "http", "--port", "0"]), ["urlTemplate"] = "http://127.0.0.1:{port}/mcp", ["startup"] = HttpStartup }
                        },
                        ["commandIsRelativeTo"] = relative ? "the folder containing this file" : null,
                        // The portable file must not bake in this machine's user profile path.
                        ["workspaceOption"] = "--workspace DIR (default " + (relative ? @"%LOCALAPPDATA%\Testy\Workspace" : AgentFiles.DefaultWorkspace) + ")",
                        ["protocolVersions"] = JsonRpc.Strings(McpServer.SupportedVersions),
                        ["tools"] = JsonRpc.Strings(tools.Names),
                        ["security"] = Security,
                        ["describeCommand"] = string.Join(' ', [.. new[] { command }, .. prefix, "mcp", "--describe"]),
                        ["documentation"] = "AGENT-ACCESS.md and docs/MCP.md next to this file"
                    }
                }
            }
        };
    }
}
