# Third-party embedded servers


Pinned copies of the two external MCP servers the Atlassian integration ([`@cortex/atlassian`](../packages/atlassian/atlassian/README.md)) launches, embedded so a fresh clone carries everything and no `docker pull` or ad-hoc checkout is needed. Unlike [`vendor/`](../vendor/README.md), nothing here is part of the harness build graph or the pnpm workspace: these trees are launched as external processes by their recorded commands.

| Directory | Upstream | Pinned at | License | Launched as |
|---|---|---|---|---|
| `mcp-bitbucket/` | https://github.com/n11techhub/mcp-bitbucket | `v2.1.2` (`8d3e002eb5a1fb4116d244de4bba7693935b9008`) | Apache-2.0 (`LICENSE`) | `node third_party/mcp-bitbucket/server.mjs` — `server.mjs` is a prebuilt self-contained bundle (esbuild `--bundle --platform=node --format=esm`, one file, no `npm install`). |
| `mcp-atlassian/` | https://github.com/sooperset/mcp-atlassian | `0838f7983d214bed113eda3f05cb86fe35a02e08` (2026-08-18, release 0.23.0) | MIT (`LICENSE`) | `uv run --frozen --project third_party/mcp-atlassian mcp-atlassian` — full pinned source with `uv.lock`; `uv` resolves the locked Python dependencies into its cache on first run. |

The default launch lines in the `atlassian` settings namespace resolve these paths from the repository root at runtime and fall back to the original docker/uvx commands when the trees are absent (see `packages/atlassian/atlassian/src/settings.ts`).

Local deltas against upstream, re-applied on every resync:

- `mcp-bitbucket/server.mjs` — built from the pinned tag with one patch: `McpServerFactory` had read `../../../../package.json` relative to its module file for the server name/version, which cannot resolve from a bundled single file; the name and version are inlined instead. The unmodified upstream manifest is kept as `upstream-package.json`.
- `mcp-atlassian/pyproject.toml` — `fallback-version` pinned to `0.23.0` (upstream computes the version from git tags, which a git-less vendored tree does not have).
- `mcp-atlassian/` drops upstream's `.git`, `.github`, `.devcontainer`, and `helm` directories; everything else (source, tests, docs, `uv.lock`) is verbatim.

Resync procedure: clone the upstream at the new pin, re-apply the deltas above (for mcp-bitbucket: `npm install && npm run build`, patch the factory, then `esbuild dist/index.js --bundle --platform=node --target=node18 --format=esm --banner:js="import { createRequire } from 'node:module'; const require = createRequire(import.meta.url);"`), update this table, and re-run the Atlassian integration's end-to-end smoke.
