# Python SDK example

English | [中文](README.zh.md)

Runnable Python SDK example over the sole application launcher, `cortex --profile sdk-minimal`. The Python client owns JSON-RPC stdio; the profile owns the agent composition, persistence, execution policy, and plugins.

## Run the minimal agent

Install `cortex-sdk`, export a model credential, then supply an isolated Harness home and workspace:

```sh
export CORTEX_API_KEY=sk-your-key-here
python python/sdk/examples/minimal.py \
  --cortex-home /absolute/path/to/example-cortex-home \
  --workspace /absolute/path/to/disposable-workspace \
  --session-id example-001 \
  "Inspect the repository and fix the failing tests."
```

Set `CORTEX_BASE_URL` for a compatible proxy, `CORTEX_MODEL` for the script's default model, or `CORTEX_SYSTEM_PROMPT` for the deployment persona. `--model` is the single runtime model selection; no matching environment variable is required. `--profile` can select another SDK-serving profile. The selected home stores the generated `sdk-minimal` profile and uncompressed JSONL session logs under `sessions/`; the script never reads `~/.cortex` implicitly.

The shipped [`@cortex/sdk-minimal` bundle](../../../packages/bundle/sdk-minimal/README.md) is the complete explicit Cordis tree for this mode. It exposes exactly:

- owner-scoped persistent `bash` on Linux/macOS or `pwsh` on Windows

The bundle does not include `cortex-base`, so every additional row is an explicit profile change. Runtime context, filesystem tools, local instruction discovery, compaction, settings, managed credentials, telemetry, Web tools, subagents, and the full default tool roster are absent. The tree retains SDK startup and JSON-RPC serving, a pi-ai adapter with no configured routes, local execution, and JSONL persistence.

The persistent PTY can modify any path available to the runtime process, so use a disposable checkout or container.

## Add plugins

Use the runtime wheel's `cortex` command against the same explicit home for persistent profile changes:

```sh
export CORTEX_HOME=/absolute/path/to/example-cortex-home
cortex plugin --profile sdk-minimal add file:/absolute/path/to/my-plugin-bundle
```

Use `sdk-minimal` in that command to extend this example, or `sdk` to extend the full base-backed SDK profile. The Python call can also pass additional absolute patch paths in `patches=(...)`; later files win. A selected profile must retain `@cortex/sdk-app` or another JSON-RPC server row. The example accepts no complete Cordis file or arbitrary process argv.

The same runtime wheel packages the `web` profile and its frontend assets for direct CLI use: `cortex web` starts that separate application. A Python SDK client cannot select `web` because it has no JSON-RPC server row.

See the [Python SDK tutorial](../../../docs/user/guide/python-sdk.md) and [SDK reference](../README.md).
