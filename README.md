# Cortex

Cortex (`cortex`) is a local, plugin-based agent harness.

Everything is a plugin. The composition layer is [Cordis](https://github.com/cordiverse/cordis),
whose design is described in
[_A Programming Paradigm for Spatiotemporal Composability_](https://github.com/cordiverse/paper).

This is a personal build, run from source rather than installed from a registry.

## Provenance and security

Cortex is a privacy-focused fork of
[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) (MIT), taken at
0.1.0-rc.5. Relative to upstream, this fork **removes** the OTLP session-telemetry exporter
(`session-telemetry-otel`), the DeepSeek API adapters (`llm-deepseek`,
`web-search-deepseek`), the vendor onboarding/default-provider wiring, and the CI and
localisation trees — and **adds** an Atlassian Data Center integration, an embedded
editor/Code view, and small UI features. No dependency or external endpoint was added.

The full tree was audited for telemetry, phone-home behaviour, hidden code, and
supply-chain integrity — including a live verification of every lockfile hash against
registry.npmjs.org, diffs of all vendored code against its upstreams, a rebuild of the
prebuilt `third_party/mcp-bitbucket` bundle, and a file-by-file comparison against
upstream. **Verdict: no telemetry, no phone-home, no hidden code.** A fresh install
contacts nothing until you configure a model provider.

Read the full report: [SECURITY-AUDIT.md](SECURITY-AUDIT.md).

## Run

```sh
pnpm install
pnpm run build
pnpm cortex web
```

The Web UI is served at `http://127.0.0.1:3080` by default. See the
[Web UI guide](docs/user/guide/index.md).

Other entry points:

```sh
pnpm cortex --profile headless "run the tests"   # answer one task, print it, exit
pnpm cortex --profile tui                        # terminal UI
pnpm cortex --dump-config                        # print the composed profile tree
```

## Configuration

Configuration lives in `$CORTEX_HOME` (default `~/.cortex`):

| file | holds |
| --- | --- |
| `settings.yaml` | providers, models, default model selection, UI preferences |
| `.credentials.yaml` | API keys, referenced by name from `settings.yaml` |
| `AGENTS.md` | instructions applied to every workspace |

Models are configured from **Settings → Models** in the Web UI, or by editing
`settings.yaml` directly. See the [model configuration guide](docs/user/guide/providers.md)
for custom providers, reasoning levels, and per-model overrides.

## Development

Start with the [development guide](docs/development.md) and the
[architecture documentation](docs/architecture.md).

```sh
pnpm run test        # unit tests
pnpm run typecheck
pnpm run lint
pnpm run build
```

## Licence

[MIT](LICENSE).

Third-party dependencies and their licences are disclosed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
