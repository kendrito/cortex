$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$bridgeScript = Join-Path $sourceRoot 'src/Testy.Cli/HyperV/Scripts/Testy.HyperV.ps1'
foreach ($path in @($bridgeScript, (Join-Path $sourceRoot 'src/Testy.Cli/HyperV/Scripts/guest-run.ps1'))) {
    $tokens = $null; $errors = $null
    $tree = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw ($errors | Out-String) }
    if ($path -eq $bridgeScript) {
        $functions = $tree.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in @('Invoke-CortexModel','Test-Cancelled') }, $false)
        foreach ($function in $functions) { . ([scriptblock]::Create($function.Extent.Text)) }
        $guest = $tree.FindAll({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$guestCode' }, $true)[0].Right.Expression.Value
        $null = [Management.Automation.Language.Parser]::ParseInput($guest, [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw ($errors | Out-String) }
    }
}
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
public sealed class CortexRelayFixture : IDisposable {
    private readonly HttpListener listener = new HttpListener();
    public string Url, Authorization, Context, Body;
    public Task Completion;
    public CortexRelayFixture() {
        var socket = new TcpListener(IPAddress.Loopback,0); socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        Url = "http://127.0.0.1:"+port+"/v1/chat/completions";
        listener.Prefixes.Add("http://127.0.0.1:"+port+"/"); listener.Start();
        Completion = Task.Run(async () => {
            var exchange = await listener.GetContextAsync();
            Authorization = exchange.Request.Headers["Authorization"]; Context = exchange.Request.Headers["X-Testy-Cortex-Context"];
            using(var reader = new StreamReader(exchange.Request.InputStream)) Body = await reader.ReadToEndAsync();
            var data = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"local fixture\"}}]}");
            exchange.Response.ContentType = "application/json"; exchange.Response.ContentLength64 = data.Length;
            await exchange.Response.OutputStream.WriteAsync(data,0,data.Length); exchange.Response.Close();
        });
    }
    public void Dispose() { listener.Close(); }
}
'@
$saved = @{}
foreach ($key in @('TESTY_CORTEX_BRIDGE_URL','TESTY_CORTEX_BRIDGE_TOKEN','TESTY_CORTEX_CONTEXT')) { $saved[$key] = [Environment]::GetEnvironmentVariable($key) }
$fixture = [CortexRelayFixture]::new()
try {
    $CancelFile = ''
    $env:TESTY_CORTEX_BRIDGE_URL = $fixture.Url; $env:TESTY_CORTEX_BRIDGE_TOKEN = 'local-fixture-test-token-0000001'; $env:TESTY_CORTEX_CONTEXT = 'host-owned-context-00001'
    $id = '0123456789abcdef0123456789abcdef'; $body = '{"model":"cortex","messages":[]}'
    $result = Invoke-CortexModel @{ id=$id; body=$body; url='https://untrusted.invalid'; token='guest-input'; context='guest-input' }
    if (-not $fixture.Completion.Wait(5000)) { throw 'Host relay fixture did not finish.' }
    if ($result.status -ne 200 -or $result.id -cne $id -or $fixture.Body -cne $body -or $fixture.Authorization -cne ('Bearer '+$env:TESTY_CORTEX_BRIDGE_TOKEN) -or $fixture.Context -cne $env:TESTY_CORTEX_CONTEXT) { throw 'Host relay selected guest authority or changed the request.' }
    Write-Output 'PASS authenticated host relay uses only the pinned host URL, token and context.'
    $env:TESTY_CORTEX_BRIDGE_URL = 'https://untrusted.invalid/v1/chat/completions'
    $refused = $false
    try { $null = Invoke-CortexModel @{ id=$id; body=$body } } catch { $refused = $true }
    if (-not $refused) { throw 'External relay URL was accepted.' }
    Write-Output 'PASS external relay URL is refused before a request.'
    Write-Output 'PASS Windows PowerShell host and guest script syntax.'
} finally {
    $fixture.Dispose()
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key,$saved[$key]) }
}
