---
description: "The browser GUI for cortex: interactive chat, model and settings management, and session history, for users running the cortex web surface."
kind: "package-bundle"
---

# @cortex/web-app


## Summary

Run `cortex --profile web` to open an interactive browser GUI with chat, model and settings management, and session history. It shares model access and safety defaults with other cortex surfaces and includes native computer use on Windows. Startup prints an authenticated URL and normally opens it in the default browser; SSH sessions and `--no-open` leave the URL for manual opening. You can change the port and allow extra hosts, but cannot bind all network interfaces. Choose this package for interactive browser work; use `cortex-headless` for one-shot command-line tasks.

## Table of Contents

- [Use this package](#use-this-package)
- [Understand the implementation](#understand-the-implementation)
- [Further Exploration](#further-exploration)
- [Model Experience](#model-experience)
- [Known Limitations and Deferred Work](#known-limitations-and-deferred-work)
- [Dev Note](#dev-note)

-----

<a id="use-this-package"></a>
## Use this package

Start the GUI, open your browser, and start talking to the agent. The flags fine-tune the invocation.

### Starting the Web GUI

```sh
cortex --profile web
cortex --profile web --no-open --port 8080
```

After startup you see a `cortex web:` line whose root URL carries a fresh process token. Unless `--no-open` or an SSH session suppresses it, the default browser opens that URL, receives a signed cookie, and redirects to the same directory without the token. You know it worked when the page loads and you can chat with the agent. Two failures to expect: if the frontend is not built, startup stops with a build hint (`pnpm run build` in a checkout); if the browser cannot be opened, a credential-free diagnostic prints to stderr while the server keeps running — open the printed startup URL yourself.

**Settings → Models** configures a local LiteLLM gateway using an OpenAI-compatible protocol. No route or model is selected by default. Administrator-provisioned external routes remain visible and usable, with their editing controls locked in the browser.

Saved model selections apply to new sessions. Browser provider controls accept only loopback HTTP(S) endpoints and OpenAI completions or responses protocols. The application includes no telemetry, external account page, session-log upload toggle, or web search/fetch tool. The application includes no bundled Atlassian or Bitbucket integration.

### Computer use

Windows Web and Desktop sessions include Computer Use through the bundled native Cua Driver. The driver exposes desktop tools and guidance to every session, including sessions reopened after an upgrade. It remains experimental, requires an interactive desktop, and uses the launching application's OS permissions. Screenshots reach only model routes that declare image input; an OpenRouter model without image support cannot use screenshots.

To turn it off, add the following to the profile's `cordis.patch.yml`:

```yaml
- id: computer-use-cua-driver-native
  disabled: true
```

Disabling removes its tools from existing and new session scopes. To choose another computer-use provider, disable the native row first, then insert the chosen provider beside the existing `computer-use` service. The shared registration rejects simultaneous providers. Other operating systems require explicit provider activation; headless, SDK, and ACP profiles do not enable desktop control. Provider installation, OS permissions, and shared-desktop limitations are documented in [Computer use](../../../docs/subsystems/computer-use.md).

### Browser use

Web and Desktop profiles enable Playwright Browser Use by default. Each live session owns an isolated headless Chromium browser, launched when a browser action needs it. Install the pinned runtime's Chromium or configure an installed Chromium executable as described in [Browser use](../../../docs/subsystems/browser-use.md). Profile patches can disable `browser-use-playwright-mcp`; enabling or replacing a provider applies when sessions are created or resumed after unloading. This adds browser interaction tools, not web grounding or a separate model provider.

Windows x64 Web and Desktop profiles also include [Testy](../../extensions/testy/README.md). Its testing pane and native MCP tools share the packaged engine, saved tests, and evidence. AI testing uses the selected Cortex chat's model and workspace; the plugin can be disabled in Built-in plugins.

### Conversation search

Sidebar search matches conversation names and saved message content. The local SQLite index opens on the first search, stays in memory, and rebuilds from saved session logs after restart. Search sends no messages to a model or external service. A profile can restore `session-query-sqlite.config.openAt: never` to disable content search.

### Configuration

Most users never set these; the command-line flags feed the four settings below — `--host`, `--port`, and `--trusted-host` come from the invocation, and `--no-open` turns the browser handoff off for that invocation:

| Field | Default | Meaning |
|---|---|---|
| `openBrowser` | `true` | Open the default browser after startup; SSH launches suppress it |
| `printUrl` | `true` | Print the `cortex web:` URL line at startup |
| `surfaceContext` | `true` | Give the agent GUI-orientation context and expose `CORTEX_WEB_URL` to its shell commands |
| `trustedHosts` | `[]` | Extra hosts allowed to reach the GUI from the network |

The generated [configuration catalog](../../../docs/config-catalog.md#deepseek-aicortex-web-app) is the exhaustive source for every accepted field and its JSDoc. The shipped composition carries no `time-context`, `schedule`, or `ui-schedule` row; the optional experimental bundle `@cortex/experimental-schedule-bundle` inserts those three rows from the Plugins page.

### LAN access and trusted hosts

By default the GUI accepts connections from this machine only. A deployment that binds all network interfaces also allows browsers from the LAN, and the printed URL then includes a LAN address; `--trusted-host` adds extra hosts in either case. Host and Origin checks control reachability, while the token exchange authenticates every Host API method and WebSocket stream. The LAN addresses are sampled once at startup, so a network change later is not picked up — restart the GUI to re-advertise.

### Running over SSH

When you launch `cortex --profile web` over SSH, the URL line still prints but the browser is not opened for you: the SSH client or editor owns the local forwarding address. Open the forwarded URL on your machine yourself; the printed URL names the remote host's loopback endpoint.

### Per-session agent setup

Each browser session selects a shipped preset (`standard` by default). The Agent presets settings page changes the default and edits preset child plugins; saves persist in `$CORTEX_HOME/profiles/web/cordis.patch.yml`. Creator's plugin-management tool is enabled only when the Host provides an editable profile.

-----

<a id="understand-the-implementation"></a>
## Understand the implementation

<details>
<summary>Implementation internals — click to expand</summary>

The bundle is one patch layer of five files plus one runtime glue plugin: `cordis.patch.yml` carries the host rows and the preset registry, and each `presets/<id>.patch.yml` inserts one shipped preset declaration, applied in the order `cortex.bundle.patch` lists them. The storage stack and projection cache come from `cortex-base`; the web overlay's workspace and message-feedback rows consume that shared `storageDomain` service. The patch restates the surface-specific values the base deliberately omits, inserts the web-only host rows and browser roster, then moves the agent plane behind presets. The glue plugin owns dist serving, trust sampling, prompt sections, the bash variable, and the readiness announcements. The `office-to-pdf` row mounts one lazy [Office conversion provider](../../document/office-to-pdf/README.md) for Host consumers, including Desktop compositions using this bundle. The conversion service's Remote methods authorize preview reads, while Document Preview owns the Office viewer and Client cache.

### Patch semantics

A patch replaces the targeted row's whole `config`, so each web row restates every key it owns: the persona prefix and suffix templates, the `CORTEX_TOOLS_MODE` PTC mode opt-in, and the `session-query-sqlite` values on the base rows, then `insert` adds the web host rows, transport, and browser roster. The per-agent tool rows the base mounts process-wide are disabled here and the preset roster takes over; the reasoning for each host-plane versus preset-plane decision is inline in the patch.

### Readiness

The URL line and browser handoff are readiness signals: supervisors RPC as soon as they observe the line, and a browser requests the page as soon as it opens, so both run only after the Loader tree settles, the required-startup audit passes, and Connection authentication is available — or immediately in a hand-built tree without a Loader. Client combo JavaScript and source maps remain unmaterialized at this point. Optional plugin failures do not suppress readiness; a required startup failure or a tree disposed mid-boot announces nothing.

### LAN trust sampling

`resolveLanTrust` samples the network once at boot: a loopback bind (`127.0.0.1`) derives no LAN addresses, while an all-interfaces bind adds every non-internal IPv4 literal. The derived literals plus the explicit `--trusted-host` authorities form the `/api` browser-trust fence, and the printed LAN URL always matches that fence.

### Source map

| File | Role |
|---|---|
| [`src/index.ts`](src/index.ts) | The `web-app` glue plugin: dist resolution, LAN trust sampling, prompt sections, bash variable, URL line, browser handoff |
| [`src/startup.ts`](src/startup.ts) | The `web-startup` provider: `--host`, `--port`, `--trusted-host`, `--no-open`, `--help` |
| [`cordis.patch.yml`](cordis.patch.yml) | The web patch: restated base values, web host rows, browser roster, preset registry |
| [`presets/`](presets) | One `@cortex/agent-preset` declaration per shipped preset (`standard`, `ptc`, `minimal`, `cordis`), each its own patch file |
| — | No runtime invariant companion is published; every contribution (frontend-static child plugin, prompt section, bashEnv registration) is registry-disposed with the fiber, and each owning registry's package carries that relation's invariant; the package holds no mutable state of its own to audit. |
| [`tests/web-app.spec.ts`](tests/web-app.spec.ts) | Dist resolution, fallback seat, prompt sections, readiness |
| [`tests/startup.spec.ts`](tests/startup.spec.ts) | Command-line parsing over a real Loader tree |
| [`tests/trusted-hosts.spec.ts`](tests/trusted-hosts.spec.ts) | LAN-trust sampling |
| [`tests/browser-open.spec.ts`](tests/browser-open.spec.ts) | Default-browser handoff after the page is reachable |

### Invariant ownership

No invariant companion is published because every contribution — the frontend-static child plugin, the prompt sections, and the bash variable registration — is registry-disposed with the fiber, and each owning registry package carries that relation's invariant.

</details>

-----

<a id="further-exploration"></a>
## Further Exploration

Read these pages when you want to go deeper into the shared core, the browser reload pipeline, or the built frontend.

- [Bundle package map](../README.md) — the surfaces built on the same core.
- [cortex-base](../base/README.md) — the shared core the GUI runs on.
- [cortex-client-hmr](../../client/hmr/README.md) — how client-plugin changes reload during development.
- [frontend-static](../../host/frontend-static/README.md) — how the built frontend is served.
- [Generated configuration catalog](../../../docs/config-catalog.md#deepseek-aicortex-web-app) — every accepted config field and its source declaration.

-----

<a id="model-experience"></a>
## Model Experience

### Harness-source and Web-surface context

#### What the model sees

When `surfaceContext` is true, the `harness:source` section identifies the on-disk Harness implementation without claiming it is the working directory, and the `app:web-surface` global section (first-party order 10100, after reusable instructions) orients the model to the GUI: the canonical local URL, the "this page" referent, the update contract (the reload receiver is always on; no-refresh reloads additionally need the `pnpm run dev:web` watcher), and the instruction not to start replacement servers. `CORTEX_WEB_URL` additionally appears in the managed bash environment with its description, resolved per invocation from the live server. When it is false, neither section nor the variable is registered.

#### Token effect

One source line and one prompt paragraph per session plus two managed-environment variable lines; constant per process.

#### KV Cache effect

Source and Web sections follow first-party reusable instructions. Different checkout paths or local ports leave that preceding prefix unchanged when tools and configuration match; provider cache reuse is not guaranteed.

## Known Limitations and Deferred Work

<a id="known-limitations-and-deferred-work"></a>


These limits tell you what to expect in unusual setups — a source checkout, SSH sessions, or strict networks. They are current package constraints, not a general browser comparison or a task backlog.

- **The frontend must be built** — a source checkout needs `pnpm run build` first; startup stops with a build hint when the dist is missing, and there is no source-serving fallback.
- **LAN addresses are sampled once at startup** — interface changes after boot are not re-advertised; the printed LAN URL always matches what was sampled.
- **Only the handoff start is observable** — the GUI reports that the browser was asked to open, not that it actually opened; a later browser exit is never reported, and the printed URL is your manual fallback.
- **SSH sessions keep the URL but skip the browser handoff** — the printed URL names the remote host's loopback endpoint; the SSH client or editor must expose and open the local forwarded address.
- **`BROWSER` overrides only come from the environment** — a discovered `.env` cannot set `BROWSER`; only an inherited value can choose the executable for the automatic handoff.
- **Binding all network interfaces is not supported** — `--host 0.0.0.0` is rejected at startup for safety; use the default loopback host.

<a id="dev-note"></a>
### Dev Note

<details>
<summary>Working context for maintainers — click to expand</summary>

None.

</details>
