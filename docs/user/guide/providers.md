# Configure models

Start Cortex using the [root README](../../../README.md#run), then open **Settings → Models**. Cortex's Models page configures a local gateway only. No external catalog or official cloud provider is enabled by default.

## Local gateway

Run an OpenAI-compatible gateway, such as LiteLLM, on the same machine as Cortex. Use an HTTP(S) loopback URL: `localhost`, `127.0.0.1`, or `[::1]`. Select OpenAI Chat Completions or OpenAI Responses to match the gateway. Add the model IDs exposed by that gateway, or discover its models through the form. External URLs and unsupported protocols are rejected before discovery makes a request.

The optional API key authenticates Cortex to the local gateway. Keys are stored through the credentials service in `$CORTEX_HOME/.credentials.yaml`; configuration stores a credential reference and the page receives a redacted descriptor. The gateway itself owns any upstream model credentials and routing policy.

Select the configured model in the composer. Model edits apply to subsequent requests. Configure a route before sending a first model turn; a fresh profile has none.

## Stored configuration

Models settings write to `$CORTEX_HOME/profiles/<profile>/cordis.patch.yml`, using `web` for the default `cortex web` profile. An earlier `settings.yaml` is imported once and renamed to `settings.yaml.imported`; unsupported sections stay in that backup. Preserve the backup until the new configuration has been checked.

The [pi-ai adapter reference](../../../packages/llm/llm-pi-ai/README.md) documents model capabilities, reasoning effort, request compatibility, timeout, and retry fields. Administrators can use those fields in profile configuration. The Models page does not expose external-provider creation, editing, deletion, or probing; existing external file-configured routes are read-only there. Backend configuration remains an administrator capability, so this UI restriction is not an egress firewall.

## Subagents

Codex and Claude Code are separate subagent providers. Their own authentication and installation requirements do not add entries to the Models page. Cortex applies supported privacy controls and excludes their web search/fetch features when launching them. See the [synchronization notes](../../cortex-upstream-sync.md) for scope and limitations.
