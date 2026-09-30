# Cortex

Cortex is a security-focused, plugin-based agent harness built on [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) and [Cordis](https://github.com/cordiverse/cordis). This source tree incorporates upstream **0.2.0-rc.2**, commit `639ed015397290b3745d163aafe02ffee4aa3f84` (2026-09-29), while retaining Cortex's local model settings.

## Privacy policy

The shipped profiles have no telemetry exporters, product analytics service, DeepSeek account onboarding, automatic session uploads, official provider defaults, or web grounding packages. Neither `web_search` nor `web_fetch` is registered. Settings → Models configures only an HTTP(S) loopback OpenAI-compatible gateway, such as a local LiteLLM instance. Existing external provider entries cannot be added, edited, deleted, or probed through that UI. Advanced file configuration remains an administrator-controlled capability.

Both Codex and Claude Code subagent providers are available. Cortex disables their supported telemetry and feedback settings and excludes their web search/fetch features when launching them. The experimental Stagehand browser dependency is patched to disable trace collection and export. Desktop update checks and remote update feeds are disabled. Plugin installation has no default fallback registry or automatic registry probes. These settings do not make tool execution a network firewall: configured models, browser automation, shell commands, explicitly configured MCP servers, and downloads can use the network when invoked.

[SECURITY-AUDIT.md](SECURITY-AUDIT.md) describes the earlier fork revision only. It is not an audit of this updated tree. See [the synchronization and migration notes](docs/cortex-upstream-sync.md) for scope, retained restrictions, compatibility changes, and validation limits.

## Run

Use Node.js within the supported engine range and the package manager version declared in `package.json`.

```sh
pnpm install
pnpm run build
pnpm cortex web
```

The Web UI listens on `http://127.0.0.1:3080` by default. Configure a local model gateway before starting a model turn. Other entry points include `pnpm cortex --profile headless "run the tests"`, `pnpm cortex --profile tui`, and `pnpm cortex --dump-config`.

Windows web and desktop profiles enable the experimental native Computer Use driver by default. It can observe and interact with the desktop through session tools; screenshots used by an image-capable model are sent to your configured model provider. See [Computer Use](docs/subsystems/computer-use.md) for permissions and disabling or switching providers. Headless and SDK profiles do not enable it automatically.

Web and desktop profiles also enable Playwright browser automation and local full-text conversation search. Browser automation does not add web grounding tools. The embedded code editor is removed; Cortex does not launch a VS Code or code-server sidecar.

On Windows x64, [Testy](packages/extensions/testy/README.md) is included as an optional built-in plugin. Describe a workflow to discover the application, create and run an AI test, and review screenshots and assertions inside Cortex. Testy uses the selected Cortex model and is also available as MCP tools in chats.

## Configuration

Configuration lives in `$CORTEX_HOME` (default `~/.cortex`). The active profile's `cordis.patch.yml` holds provider routes, model selections, and UI preferences; `.credentials.yaml` holds referenced credentials; `AGENTS.md` supplies workspace instructions. Legacy `settings.yaml` is imported once and renamed to `settings.yaml.imported`; rejected sections remain in that backup. Back up the home directory and persisted sessions before upgrading. Consult the [upstream upgrade guides](docs/upgrade-guide) alongside the [Cortex migration notes](docs/cortex-upstream-sync.md).

## Development

Start with [development](docs/development.md) and [architecture](docs/architecture.md). `pnpm run typecheck`, `pnpm run lint`, and `pnpm run test` cover separate validation surfaces. Real model fixture recording requires an explicitly configured local test gateway; it is not part of a keyless test run.

Cortex repository versions use annotated `cortex-v<version>` tags. See [preparing and tagging a version](docs/cortex-releases.md) for the version update and GitHub tagging procedure.

## Licence

[MIT](LICENSE). Upstream and third-party attribution remains in the source tree.
