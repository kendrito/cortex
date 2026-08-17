# Cortex

Cortex (`cortex`) is a local, plugin-based agent harness.

Everything is a plugin. The composition layer is [Cordis](https://github.com/cordiverse/cordis),
whose design is described in
[_A Programming Paradigm for Spatiotemporal Composability_](https://github.com/cordiverse/paper).

This is a personal build, run from source rather than installed from a registry.

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
