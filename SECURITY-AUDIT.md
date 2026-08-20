# Cortex — Codebase Trust Audit

**Repository-level review of this repository for telemetry, phone-home behaviour, hidden or
obfuscated code, supply-chain integrity, and default outbound network surface.**

| | |
| --- | --- |
| **Commit audited** | `9182f62` (4,867 tracked files) — later commits touch `LICENSE` and documentation only |
| **Upstream** | [`deepseek-ai/deepseek-harness`](https://github.com/deepseek-ai/deepseek-harness) (public, MIT) |
| **Date** | 19 August 2026 |
| **Method** | Static deep-read + rebuilds + live registry verification, performed with Claude Code (six parallel audit passes) |

## Overall verdict

> **Trustworthy — no telemetry, no phone-home, no hidden code.**
>
> No analytics, crash reporting, usage reporting, update checks, or upstream-owned endpoints
> are reached by the shipped code. Every outbound connection is either configured by the user
> or triggered by the model through a tool the user composes.
>
> **0 high · 3 medium · 7 low** findings. The medium items are configuration defaults and
> upstream leftovers — none leaks data today, and all are quick fixes (see the
> [remediation checklist](#10--remediation-checklist)).

---

## 1 — Executive summary

The question was specific: *this harness was forked from an online project — does the code
send anything anywhere it shouldn't?* The audit therefore focused on trust rather than
hardening: telemetry and analytics, phone-home and update checks, machine fingerprinting,
hidden or obfuscated code, tampered dependencies, and what the software contacts by default.

| Area | Result |
| --- | --- |
| **Telemetry & analytics** | ✅ Clean — no analytics SDK, no OTel exporter, no usage or crash reporting. The telemetry "seam" has no backend. |
| **Phone-home / update checks** | ✅ Clean — no version checks, first-run beacons, or self-update. Startup opens only the local web server. |
| **Hidden or obfuscated code** | ✅ Clean — no embedded payloads, opaque blobs, or covert channels; dynamic code is limited to documented plugin/YAML features. |
| **Supply chain (npm lockfile)** | ✅ Clean — all 1,090 resolutions from registry.npmjs.org; every integrity hash verified live; all install scripts allow-listed. |
| **Vendored framework code** | ✅ Clean — cordis / cosmokit / schemastery diffed against upstream: every change is a documented local modification; no injected code. |
| **Embedded third-party servers** | ✅ Clean — mcp-atlassian is byte-identical to upstream; the prebuilt mcp-bitbucket bundle was rebuilt from upstream and matches. |
| **Default outbound behaviour** | ⚠️ 1 item — no LLM, search, or MCP endpoint is contacted until configured; one default-on sidecar (VS Code) reaches Microsoft if installed. |
| **Upstream / rebrand residue** | 🧹 Housekeeping — placeholder repo URLs, a stale internal registry default, dead provider ids, stale telemetry docs. |

### Headline conclusions

- **Nothing in the shipped code reports back to the upstream author or any third party.**
  The only telemetry references are an abstract capture interface
  (`packages/session/session-telemetry`), a no-op switch (`CORTEX_TELEMETRY_DISABLED`) for a
  backend row that does not exist, and stale docs.
- **The anonymous user id is a random UUID that never leaves the machine.** Its only consumer
  echoes it in the `/feedback` acknowledgement; `/feedback` itself appends to the local
  session log.
- **LLM traffic goes only where you point it.** The harness source has no default model
  endpoint; the provider catalog comes from the open-source `@earendil-works/pi-ai` library
  (official vendor hosts, every base URL overridable), and no route is mounted until you
  configure one.
- **Dependencies and vendored code are what they claim to be.** Lockfile integrity was
  verified against the public registry; vendored forks and embedded MCP servers were diffed
  or rebuilt against their upstreams.
- **The fork is upstream + rebrand + the owner's own edits, and nothing else.** It matches
  DeepSeek AI's public `deepseek-harness` 0.1.0-rc.5 after rename-normalisation; the fork
  removed the upstream's (default-off) OTLP telemetry exporter, the DeepSeek API adapters and
  all CI/docs, and added no dependency or external host (§8).

---

## 2 — Scope and method

| | |
| --- | --- |
| Tracked files audited | 4,867 |
| TypeScript / TSX sources | 2,567 |
| Python files (SDK + vendored MCP) | 308 |
| npm packages integrity-verified | 1,090 |
| Lines of prebuilt bundle rebuilt & diffed | 84,101 |

| Area | What was examined | Technique |
| --- | --- | --- |
| **Host packages** (`packages/*`, 48 families) | Every network primitive (fetch, http/https, net, tls, dgram, WebSocket, dns), child-process spawns, env reads, credential handling, identity, feedback, session telemetry seam, LLM adapter, web tools, MCP client, sandbox, Atlassian integration, E2B. | Exhaustive grep + file reads; pi-ai provider catalog read from the pnpm store. |
| **Client & apps** (`apps/cli`, `apps/web`, `packages/client/*`, `examples/`) | index.html, Vite config, manifest, service workers, analytics snippets, beacons, XHR/fetch/WebSocket targets, localStorage identifiers, CLI boot path, env vars, presets, first-run behaviour. | Static read of every fetch/WS/EventSource site; preset YAML review. |
| **Supply chain** (`pnpm-lock.yaml`, 231 `package.json`, `patches/`, `vendor/`, `scripts/`) | Resolution sources, integrity hashes, install scripts vs allow-list, unusual packages, patch content, vendored forks vs upstream, build/publish/CI scripts, git hooks. | Live comparison of all 1,090 integrity hashes with registry.npmjs.org; `npm pack` of upstream versions and diff. |
| **Embedded third parties** (`third_party/`, `python/`, `native/`) | mcp-atlassian (Python) and mcp-bitbucket (prebuilt 3.2 MB esbuild bundle), Python SDK/runtime, Landlock launcher. | Byte comparison with upstream tags; full rebuild of the bundle from upstream source and diff; binary import inspection. |
| **Whole-tree sweep** | Obfuscation markers, long opaque literals, binary artifacts, data-harvesting reads, DNS/covert channels, scheduled behaviours, hard-coded secrets, webhooks/vendors. | Pattern census over all tracked files with every hit triaged by reading its context. |
| **Provenance** | Identification of the upstream project and comparison of the fork against it. | Normalised rename + tree diff (§8). |

**Severity scale.** *High:* data leaves the machine to a party the user did not choose, or
hidden/tampered code executes — **none found**. *Medium:* a default or leftover that *could*
send traffic or credentials somewhere unexpected under realistic conditions, or misleads
users into installing the wrong code. *Low:* minor metadata leakage, content-driven channels,
thin provenance, or trust-adjacent robustness issues. *Info:* rebrand residue, dead
configuration, stale docs, functional bugs — no data-flow impact.

**Out of scope.** Behaviour of external binaries the harness can launch once configured
(Codex CLI, Claude Code, VS Code `serve-web`, `uv`) is governed by those vendors and was not
audited. General vulnerability hardening was not the objective. Build outputs (`dist/`,
`lib/`) and untracked local files were outside the tracked-tree review.

---

## 3 — Findings

### Medium

#### M1 — Embedded VS Code sidecar is started automatically when VS Code is installed

This is a feature added in the fork (not present upstream). The web bundle defaults to
`editor: 'auto'`. On every `cortex web` start, if `code-server` or `code` (or the macOS
VS Code app binary) is found, it is spawned on `127.0.0.1:3082` with `--disable-telemetry`
(and `--disable-update-check` for code-server, `--accept-server-license-terms` for
`code serve-web`). The harness itself downloads nothing — but `code serve-web` fetches the
VS Code Server build from Microsoft on first run and the embedded workbench may contact the
extension marketplace. The license is accepted on the user's behalf.

- **Location:** `packages/bundle/web-app/src/index.ts:61-67, 132-157, 180-200, 303-318`;
  client probe `packages/client/ui-workspace/src/client/CodeView.tsx:19, 55, 117`
- **Destination:** Microsoft (VS Code update/marketplace endpoints) — VS Code's behaviour,
  not Cortex code
- **Trigger:** default-on; `editor: off` in a patch layer disables it
- **Fix:** default to `editor: off` (opt-in), or surface the sidecar launch clearly; make the
  `127.0.0.1:3082` client origin configurable.

#### M2 — Manual publish script defaults to an upstream-internal npm registry

`scripts/publish-npm-baseline.ts` sets `DEFAULT_REGISTRY =
'https://registry.npm.harnessment.com'` — the only occurrence of that domain in the
repository. The domain is real and its public A record resolves to a private RFC-1918
address, i.e. an upstream-internal registry that survived the scrub. The `pack` subcommand
runs `npm install --registry=<that host>` in a temporary consumer and `publish` runs
`npm ping/whoami/view/publish` against it. It is reachable only through the explicit
`pnpm publish:npm-baseline` script, is referenced by no hook, gate, or CI, reads no token
itself (npm auth is registry-scoped), and is currently blocked by a stable-version check
(`0.1.0-rc.5` is rejected before any network call).

- **Location:** `scripts/publish-npm-baseline.ts:23, 449-455, 625-765, 904-909, 942-946`;
  entry `package.json:115`
- **Trigger:** manual only; inert today
- **Fix:** default to `https://registry.npmjs.org` or require an explicit `--registry`;
  remove the stale `minimumReleaseAgeExclude` block that implies the upstream CI `.npmrc`.

#### M3 — Python SDK docs point at a PyPI name owned by someone else

The docs instruct `pip install cortex-sdk`; on PyPI, `cortex-sdk` is an unrelated package by
a third party ("Nearly Human Cortex SDK", v2.4.1). A user following the guide installs
someone else's code. Additionally `python/sdk/pyproject.toml` depends on
`cortex-runtime-bin==0.0.0.dev0`, which is unregistered on PyPI and therefore squattable. A
related rename bug makes the runtime loader look for `cortex-runtime.json` while the shipped
file is `cortex-harness-runtime.json`.

- **Location:** `docs/user/guide/python-sdk.md:23`, `python/sdk/README.md:9,12`,
  `python/sdk/pyproject.toml:14`, `python/sdk-runtime/src/cortex_harness_runtime/__init__.py:30`
- **Fix:** choose and register a unique PyPI name (and the runtime-bin name) before
  publishing; update docs; fix the runtime JSON filename.

### Low

| # | Finding | Location | Fix |
| --- | --- | --- | --- |
| **L1** | Every LLM request sends `User-Agent: cortex/<version> (+https://github.com/local/cortex)` — a placeholder scrub artifact ("nothing can suppress attribution entirely"). Only product name + version leave, to the provider you configured. Stale `cortex/0.0.1` UAs in web-fetch/search providers. | `packages/llm/llm/src/attribution.ts:38-44,64-68`; `packages/web/web-fetch-http/src/index.ts:25` | Point the attribution URL at the real repository. |
| **L2** | `repository.url = git+https://github.com/local/cortex.git` in 231 manifests (enforced by a workspace gate); `github.com/local/cordis\|cosmokit\|schemastery` in vendor README and notices. "local" is an existing, unrelated GitHub org. | 231 × `package.json:10`; `scripts/check-workspace-constraints.ts:43,49`; `vendor/README.md:15-23`; `THIRD_PARTY_NOTICES.md:18-26` | Replace with the real repo URL; update the gate + spec. |
| **L3** | The vendored mcp-atlassian server calls `load_dotenv(override=True)` on its cwd, and Cortex mounts it with an inherited cwd — a `.env` in an untrusted workspace could override `JIRA_URL`/`*_PERSONAL_TOKEN` and redirect the PAT. Upstream behaviour, not hidden code. | `third_party/mcp-atlassian/src/mcp_atlassian/__init__.py:308-313`; `packages/atlassian/atlassian/src/mounts.ts:77-85,97,133` | Launch the MCP child with an explicit neutral cwd. |
| **L4** | `node-addon-require-builtin@0.1.4` ships a prebuilt `.node` addon (no author/repo fields; maintainer first published 2026-07). Static inspection: imports libc++/libSystem/N-API only — no socket/exec/dlopen; the loader sha256-verifies before loading. An upstream-cordis choice, not fork-injected. | `apps/cli/package.json:83`; `vendor/loader/src/internal.ts:107-131` | Keep the integrity pin; consider building from source or `--expose-internals`. |
| **L5** | The markdown renderer allows `http(s)` `<img src>` — model output can make the browser GET an arbitrary URL (classic prompt-injection exfil channel; content-driven, not code-driven). | `packages/client/ui-primitives/src/markdown/render.tsx:37-57,472-480` | Proxy/block remote images by default. |
| **L6** | The CLI auto-loads `./.env` of the invoking directory (plus `$CORTEX_HOME/.env`). Bootstrap-sensitive names are refused from files — good — but provider keys in an untrusted repo's `.env` are imported. | `apps/cli/src/bin.ts:33`; `packages/boot/app-boot/src/index.ts:91-115,176-197` | Restrict file-sourced env or require opt-in. |
| **L7** | Fallback launch lines for the Atlassian servers are unpinned (`uvx mcp-atlassian` latest; `ghcr.io/...:latest`) — only used if the vendored `third_party/` trees are missing. | `packages/atlassian/atlassian/src/settings.ts:54,68` | Pin the version and image digest. |

### Informational — rebrand residue, dead config, stale docs

| # | Item | Location |
| --- | --- | --- |
| I1 | Default model route points at a provider that no longer exists (`cortex-official` / `cortex-v4-flash`); `web.searchProvider` likewise. Net effect: **nothing is contacted by default** — Models page shows "adapter absent" until `llm-pi-ai` is configured; `web_search` fails with `WEB_PROVIDER_CONFIGURED_MISSING`. | `packages/bundle/base/cordis.patch.yml:62-66,363-378`; ~15 example `*.cordis.yml` |
| I2 | Stale telemetry docs and switch: `CORTEX_TELEMETRY_MODE`/`_OTLP_URL` documented but nonexistent; `session-telemetry-otel` row referenced but absent; `CORTEX_TELEMETRY_DISABLED` is a no-op. | `apps/cli/reference/README.md:75-77`; `apps/cli/src/profile-boot.ts:56-83`; `scripts/gen-doc-graphs.ts:196` |
| I3 | Embedded chip font renamed in CSS (`CortexChipCell`) but the TTF name table still reads `DshChipCellRegular`. | `packages/client/ui-conversation/src/client/skeleton/InputBar.module.css:6-8` |
| I4 | Upstream developer paths in test fixtures (`/Users/cty/…`, `/Users/qyj/work/cortex`). | `examples/acp-agent/tests/snapshots/session-sandbox-root/session.jsonl:1`; `packages/session/session-persistence-jsonl/tests/jsonl.spec.ts:164` |
| I5 | Upstream repo name fragment-assembled in a gate; release script expects `cortex_harness_sdk-*.whl` while the package is `cortex-sdk`. | `scripts/verify-public-repository-links.ts:9-11`; `scripts/build-python-release.py:81` |
| I6 | Dangling CI / docs-site / hook references (`.github/`, `.gitlab-ci.yml`, `verify-archived-agent-notes.ts`, `docs:build`, broken `.claude/skills` symlink). | `package.json:36`; `scripts/ci-workflow.spec.ts`; `lefthook.yml:9,38`; `scripts/run-gates.ts` |
| I7 | Inconsistent vendor scrub: badges still point at real upstream authors while notices use placeholders; vendor README claims don't match manifests. | `vendor/cosmokit/README.md:3-4`; `vendor/schemastery/README.md:3-6` |
| I8 | `subagent_codex` tool enabled in the `standard`/`code` presets (upstream shipped it disabled). Runs only when the model calls it; drives the local `codex` CLI. `claude-code` stays disabled. | `apps/cli/config/agent-presets/standard/agent.cordis.yml:203-209` |
| I9 | Prebuilt bundle not byte-reproducible from the README (esbuild version, dep lock, and sha256 not recorded). | `third_party/README.md:19` |
| I10 | `THIRD_PARTY_NOTICES.md` omits `third_party/` (generated from the pnpm workspace only). | `THIRD_PARTY_NOTICES.md` |
| I11 | Miscellany: tracked scratch file, stale `allowBuilds` entry, unused `@yarnpkg/cli-dist`, one `dsh_*` comment. | `random-file.txt`; `pnpm-workspace.yaml:47,57-68`; `package.json:142` |

---

## 4 — Telemetry and identity: why the "telemetry" code is inert

The word *telemetry* appears in the tree. The design is a **capability seam**: an abstract
interface that a deployment *may* implement. This tree ships the interface and a capture
coordinator but **no implementation, no exporter, and no OpenTelemetry SDK**. (Upstream did
ship one — an OTLP exporter to a DeepSeek-operated collector, off by default; the fork
removed it. See §8.)

```
Session events ──► Capture seam (@cortex/session-telemetry) ──✕──► Backend / exporter
(local session       abstract SessionTelemetryBackend,             NOT IN THIS TREE
 log on disk)        redaction waterfall; calls emit()
                     on a mounted backend
```

| Check | Evidence | Result |
| --- | --- | --- |
| Any concrete telemetry backend? | `packages/session/session-telemetry/src/index.ts:148` is abstract; no `extends SessionTelemetryBackend` anywhere; `git ls-files \| grep otel` is empty; `packages/bundle/base/tests/base.spec.ts:35` asserts zero `session-telemetry-otel` rows. | **None** |
| OTel SDK or OTLP exporter in dependencies? | Lockfile has `@opentelemetry/api` (no-op API) only, pulled transitively by `@mistralai/mistralai` and `vitest`. No `sdk-*`, no `exporter-*`. | **None** |
| Telemetry switch | `CORTEX_TELEMETRY_DISABLED` (`apps/cli/src/profile-boot.ts:56-83`) patches a row that is never present → no-op. Documented `CORTEX_TELEMETRY_MODE`/`_OTLP_URL` appear nowhere in code. | **No-op** |
| `/feedback` command | `packages/feedback/command-feedback/src/index.ts:72-76,91-96` appends to the local session log; `message-feedback` (Like/Dislike) writes a local KV table. | **Local** |
| Anonymous user id | `packages/identity/anonymous-user-id/src/index.ts:29,68-100`: `crypto.randomUUID()` persisted to `$CORTEX_HOME/.anonymous-user-id`; not derived from hostname/MAC/git. Sole runtime consumer: the `/feedback` acknowledgement text. Never in a header, user-agent, request body, or export. | **Local** |
| Machine fingerprinting | No `os.hostname`/`os.userInfo`/`os.cpus` in source; `os.networkInterfaces()` only to print LAN URLs when bound to `0.0.0.0`; no persistent client/install id in localStorage. | **None** |
| Analytics / crash SDKs | Zero hits for Sentry, Segment, PostHog, Mixpanel, Amplitude, Datadog, Bugsnag, Rollbar, GA/gtag, Hotjar, FullStory, LogRocket, Plausible, Umami, Fathom, Vercel/Cloudflare insights — in source and in the lockfile. | **None** |

---

## 5 — Network egress map

Compiled from every network primitive in the tree. "Default state" describes a fresh install
with the shipped presets and no user configuration.

| Component | Destination | Configurable? | Trigger | Default state |
| --- | --- | --- | --- | --- |
| `llm-pi-ai` (LLM adapter) | The configured route's base URL. Catalog defaults come from `@earendil-works/pi-ai` — official hosts of ~25 vendors (OpenAI, Anthropic, Google, Bedrock, Mistral, Groq, xAI, OpenRouter, DeepSeek, Moonshot, Zhipu, Qwen, …). | Yes — `baseURL`, `headers`, `apiKeyEnv` per provider | Every agent turn | **Dormant — zero routes** |
| `llm-pi-ai` model discovery | `<user-typed base URL>/models` | User-typed | Models page fetch | User action |
| pi-ai `radius` provider | `radius.pi.dev` (the library author's gateway) | Only if a `radius` route is configured | `refreshModels()` — never called by the harness | **Unreachable** |
| `web-search-exa` / `-perplexity` | `api.exa.ai`, `api.perplexity.ai` | `baseURL`, API-key env | Model `web_search` | **Not mounted** |
| `web-fetch-http` | Model-chosen URL (no SSRF guard) | — | Model `web_fetch` | **Not mounted; `fetch: false`** |
| MCP client | User-configured `url` or child command (stdio, scrubbed env) | Fully | User composition | **No default servers** |
| Atlassian integration | User's Jira/Confluence/Bitbucket DC hosts (REST + vendored MCP children); `pypi.org` once via `uv run --frozen`; fallbacks `uvx`/`ghcr.io` only if vendored trees are missing | URLs + tokens in settings/credentials | After URL **and** token are set | **Mounted, inert** |
| mcp-atlassian (OAuth/Cloud mode) | `auth.atlassian.com`, `api.atlassian.com`; optional WPAD PAC fetch (**opt-in**) | OAuth env | OAuth flow | **Off (DC/PAT mode)** |
| E2B sandbox | `api.e2b.app` (or `E2B_DOMAIN`) | `E2B_API_KEY` | Plugin construction | **Not mounted** |
| `subagent-codex` | Local `codex app-server` → wherever `~/.codex` points | Codex's own config | Model tool call | **Preset-enabled** |
| `subagent-claude-code` / ACP / LSP / hooks | User-configured local binaries | Fully | Tool rows | **Disabled / not mounted** |
| Web-app editor sidecar | Local `code-server` / `code serve-web` on `127.0.0.1:3082`; VS Code itself fetches server bits from Microsoft on first run | `editor: off`, `editorPort` | `cortex web` start, if binary on PATH | **Default-on (M1)** |
| Host web server / API proxy | Listens on `127.0.0.1:3080`; Host/Origin trust fence on every API request | host/port | Browser ↔ host | **Loopback** |
| Browser client | Same-origin only (`/api/*`, `/plugins/*`, WebSockets to page origin); remote `<img>` in markdown (L5) | n/a | UI actions | **Same-origin** |
| `tool-bash` / `tool-pwsh` / terminal | Anything — the sandbox restricts *files*, not network | Sandbox policy | Model-run commands | By design |
| Build / release scripts (manual) | `nodejs.org` (sha256-checked), `archive.ubuntu.com` (sha256-pinned), `pnpm dlx @yao-pkg/pkg`, npm publish; `registry.npm.harnessment.com` (M2) | Flags | Developer invokes | Manual only |

> **What a fresh install contacts with no configuration:** nothing beyond loopback — unless
> VS Code is on PATH, in which case `cortex web` starts the VS Code sidecar (M1), and unless
> the model calls `subagent_codex`, which delegates to a locally installed Codex CLI. No LLM,
> search, MCP, or cloud-sandbox endpoint is reached until you configure one.

---

## 6 — Supply chain

### Lockfile provenance

- 1,090 entries, **all** `{integrity: sha512-…}` from the default registry; zero `tarball:`,
  `commit:`, `repo:`, `git+`, or out-of-workspace `file:`/`link:`; no `npm:` aliases; no
  tracked `.npmrc` or `pnpmfile`.
- **1,090 / 1,090** integrity hashes match `registry.npmjs.org` `dist.integrity`, compared
  live during the audit.
- One patched dependency: `patches/node-pty@1.1.0.patch` — changes only how the PTY
  spawn-helper path is resolved. Its sha256 matches the lockfile pin.

### Install-time code

pnpm's `strictDepBuilds` is in effect: any package with a lifecycle script must be
allow-listed or install fails. Every package that actually ships an install script is listed
in `pnpm-workspace.yaml allowBuilds` — esbuild, lefthook, node-pty, koffi, plus the two
in-repo postinstalls (`scripts/install-lefthook.mjs` — local git hooks, no network; a chmod
for node-pty's spawn-helper). No `curl`/`wget` in any package script.

### Notable dependencies

| Package | Publisher | Network code | Verdict |
| --- | --- | --- | --- |
| `@earendil-works/pi-ai` 0.82.1 | Mario Zechner (earendil-works/pi), MIT | LLM vendor hosts only; `radius.pi.dev` never called | Benign |
| `node-addon-require-builtin` 0.1.4 | npm user `imccyu`, no repo field | None (binary: libc++/libSystem/N-API imports only) | Thin provenance (L4) |
| `koffi` 3.1.1 | Koromix | None (Win32-only FFI) | Benign |
| `e2b` 2.29.1 | e2b-dev | `api.e2b.app` when configured | Expected |
| `@anthropic-ai/claude-agent-sdk`, `@openai/codex` | Anthropic, OpenAI | Vendor endpoints when used | Official |
| `@yarnpkg/cli-dist` 4.17.1 | yarnpkg | None | Unused — remove |

### Vendored framework forks

`vendor/` carries forks of cordis, cosmokit, schemastery, and `@cordisjs/*` (group, hmr,
include, loader, logger-console, timer). All nine were obtained from the public registry /
GitHub at the versions the vendor manifest names and diffed against `vendor/*/src`:

> **No injected code.** Every semantic hunk maps to a numbered entry in `vendor/README.md`
> "Local modifications" or is JSDoc-only; four packages are byte-identical after
> comment-stripping. Zero network, child-process, or opaque-literal constructs in
> `vendor/*/src`. The dynamic-code features (`!!js` YAML tags, schemastery callback
> rehydration) are verbatim upstream and treat configuration files as trusted code by design.

---

## 7 — Embedded third-party servers

### mcp-bitbucket — prebuilt `server.mjs`

The single most natural place to hide code in the repository (a 3.2 MB, 84,101-line esbuild
bundle rather than source), so it received a full rebuild-and-compare.

| Property | Value |
| --- | --- |
| Artifact | `third_party/mcp-bitbucket/server.mjs` — sha256 `81df09f72c198a4d38983bf14aa701b0174357a217c84f1a3c70c3e42a2ff018` |
| Claimed upstream | `n11techhub/mcp-bitbucket` v2.1.2 (commit `8d3e002e`). Upstream publishes no npm package and no dist, so only a rebuild can verify it. |
| Verification | Upstream cloned at that tag, built, bundled with the README's esbuild command, then diffed against the shipped file. |
| Diff result | 107 hunks / 580 lines, **all classified**: esbuild-version cosmetics, one transitive dep drift, and **exactly the one documented patch** (inlined package name/version). **Zero other application-code differences.** |
| Network surface | axios to `${BITBUCKET_URL}/rest/api/1.0/…` with the user's token; honours `HTTP(S)_PROXY`. Opt-in HTTP listener only if `ENABLE_HTTP_TRANSPORT` (Cortex uses stdio). No `fetch`, `dns`, `child_process`, `eval`, base64 payloads, or host fingerprinting. |
| **Verdict** | ✅ **Faithful rebuild of upstream — no hidden changes** |

### mcp-atlassian — Python source

| Property | Value |
| --- | --- |
| Claimed upstream | `sooperset/mcp-atlassian` at `0838f79` (v0.23.0 + 32 commits) |
| Verification | `src/` and `uv.lock` are **byte-identical** to upstream; only `pyproject.toml` (fallback version) and `.gitignore` differ. |
| Telemetry | None. `opentelemetry-api` present only as a transitive no-op dependency; no SDK, no exporter, no `OTEL_*` reads. All 148 locked packages resolve from `pypi.org`. |
| Network surface | User-configured `JIRA_URL`/`CONFLUENCE_URL` through an SSRF-pinning adapter; Atlassian OAuth endpoints only in Cloud mode; WPAD proxy auto-discovery **opt-in**, off by default; `chatgpt.com` strings are an OAuth *redirect allow-list*, never contacted. |
| **Verdict** | ✅ **Identical to upstream — no telemetry** |

**Python SDK and native launcher.** `python/sdk` has no network code and
`python/sdk-runtime` none at runtime — naming issues aside (M3). `native/landlock-run` is a
C11 launcher with no socket includes, built locally; its binaries are git-ignored and
sha256-pinned at pack time; nothing is downloaded.

---

## 8 — Provenance: where this code comes from

The fork's history was squashed, so provenance was re-established by comparison. The codebase
is **DeepSeek AI's [`deepseek-harness`](https://github.com/deepseek-ai/deepseek-harness)**
(public GitHub, MIT) — a Cordis-based agent harness. After applying the rebrand mapping
(`@deepseek-ai/dsh-*` → `@cortex/*`, `DSH_` → `CORTEX_`, `~/.dsh` → `~/.cortex`, …) to a copy
of upstream, the fork's initial commit was compared file-by-file against the upstream tree.

| | |
| --- | --- |
| Upstream release matched | **0.1.0-rc.5** (commit `47f9438`, 2026-08-13) |
| Byte-identical after rename | **3,467** of 4,380 fork files |
| Differ | **913** — rebrand residue, translations, owner edits (every non-rename code hunk read) |
| Removed from upstream | **3,032** files |
| New dependencies / hosts / lockfile changes | **0** |

**What the fork removed:** 2,057 agent notes and 21 skills under `.agents/`; all CI
(`.github/`, `.gitlab-ci.yml`); 788 Chinese-language docs/locale files; the `website/`; and
five whole packages — `llm-deepseek` (native DeepSeek API adapter), `web-search-deepseek`,
`skill-badge`, the DeepSeek onboarding/models UI, and **`session-telemetry-otel`**.

> **The telemetry question, answered at the source.** Upstream rc.5 *does* ship an
> OpenTelemetry log exporter: `packages/session/session-telemetry-otel`, mounted in the base
> bundle with `mode: DSH_TELEMETRY_MODE || 'DISABLED'` and exporter URL
> `DSH_TELEMETRY_OTLP_URL ?? https://harness-telemetry.deepseeksvc.com/v1/logs`, tagging
> records with the anonymous user id. It is off by default upstream and **entirely absent
> from this fork**: the package, its mount, its OpenTelemetry dependencies and its references
> were removed, and the fork's bundle test asserts the row is gone.

**What the fork changed or added** (each verified benign): the embedded editor sidecar +
Code view (M1), turn-activity counters in the UI, base-bundle composition changes (telemetry
and DeepSeek packages out, `subagent-codex` in and enabled in presets), a Models-page
provider allow-list, removal of the automatic `llm-deepseek` fallback (fresh installs boot
with **no** LLM route until configured), UI removals and de-localisation, script cleanups
(including switching a Node mirror from `npmmirror.com` to `nodejs.org`), branding, and the
Atlassian DC integration (480 added files; lockfile gains only two workspace importers).

> **Provenance verdict:** the fork is **upstream `deepseek-harness` 0.1.0-rc.5 + a
> mechanical rebrand + the owner's documented changes**. No third-party dependency was
> added, the lockfile is upstream's pruned of removed packages, no new external host appears
> in any file, no tests were skipped, and sandbox, credential, guard, subprocess, identity
> and feedback logic is identical to upstream apart from comments and casing. `apps/cli/src`
> and `apps/web/src` have zero differing files.

---

## 9 — Hidden-code and data-harvesting sweep

Pattern census across all tracked files; every hit was read in context and classified.

| Pattern | Hits | Disposition |
| --- | --- | --- |
| `eval(` / `new Function(`; computed `import()` | 6 / few | Computed imports are only the cordis plugin loaders. The rest are documented product features: `!!js` YAML config tags, schemastery callback rehydration, model-authored plugin sandboxes in `node:vm`, a workflow worker, a parse-only verifier. |
| `atob`/`btoa`/`Buffer.from(base64\|hex)` | 21 | Runtime data encoding; two tiny PNG test fixtures. No decoded-then-executed payloads. |
| `String.fromCharCode` chains; escape runs; opaque literals; tracked binaries | 9 / 0 / — / 5 | Character utilities; long literals are SVG paths, generated catalogs, fixtures; the only binaries are three PNGs and two SVGs. |
| `child_process` (files); `shell: true` | 36 / 1 | Sandbox probes, `ps`/`which`, dialogs, ripgrep, tmux, editor sidecar, subagent CLIs, one Windows `.cmd` shim. No `curl`/`wget`/`ssh`/`nc`. |
| `os.hostname`/`userInfo`/`cpus`; `networkInterfaces` | 0 / 1 | Only LAN-URL display when bound to `0.0.0.0`. |
| Wholesale `process.env` use | 20 | All child-spawn env bases; children receive a scrubbed env dropping `/KEY\|PASSWORD\|SECRET\|TOKEN/i` and `CORTEX_*`. |
| Reads of `~/.ssh`, `~/.aws`, gcloud, `.netrc`, `.npmrc`, `.gitconfig`, keychain, cookies | 0 | — |
| `dns`/`dgram`/`tls`/WebRTC/`sendBeacon`; `setInterval`; launchd/systemd/crontab | 0 / 17 / 0 | No covert channels; intervals are UI clocks, HMR polling, an event-loop timer, the loopback editor probe; nothing installs scheduled jobs. |
| Webhooks, messaging, payment, email, licensing vendors | 0 | — |
| Hard-coded secrets (AWS/OpenAI/GitHub/Slack/PEM/Bearer patterns) | 0 real | Only obvious test placeholders. |
| Python `exec`/`eval`/`b64decode`/`marshal`/`shell=True`/`pickle` | 0 | — |

**Credential handling (verified good):** `$CORTEX_HOME/.credentials.yaml` written 0600 in a
0700 directory via atomic write, and the host refuses to start if group/other bits are set;
settings secrets are structurally redacted before crossing to the browser; Atlassian PATs
reach only the child MCP env / `Authorization` header — never session events; API keys reach
only the provider their route references; no key appears in logs or telemetry records.

---

## 10 — Remediation checklist

Priority order:

- [ ] **M1** — set the web-app editor sidecar to opt-in (`editor: off` default) or document
  the auto-launch; make the client's `EDITOR_ORIGIN` configurable.
- [ ] **M2** — change `DEFAULT_REGISTRY` in `scripts/publish-npm-baseline.ts` to
  `https://registry.npmjs.org` (or require `--registry`); drop the stale
  `minimumReleaseAgeExclude` block.
- [ ] **M3** — register a unique PyPI name for the SDK and runtime-bin; update docs; fix the
  `cortex-runtime.json` filename lookup.
- [ ] **L1** — replace the attribution URL in `packages/llm/llm/src/attribution.ts` and the
  stale `cortex/0.0.1` user-agents.
- [ ] **L2** — replace every `github.com/local/*` URL (231 manifests, notices, vendor README,
  pyproject, native packages) and update `scripts/check-workspace-constraints.ts` + its spec.
- [ ] **L3** — launch the Atlassian MCP children with an explicit neutral cwd so a workspace
  `.env` cannot override `JIRA_URL`/tokens.
- [ ] **L4** — decide on `node-addon-require-builtin`: keep with the integrity pin, build
  from source, or switch the launcher to `--expose-internals`.
- [ ] **L5** — block or proxy remote images in rendered markdown by default.
- [ ] **L6** — limit file-sourced env to `$CORTEX_HOME/.env` or require opt-in for the
  workspace `.env`.
- [ ] **L7** — pin the `uvx mcp-atlassian` version and the `ghcr.io` image digest in the
  fallback launch lines.
- [ ] **I1–I2** — remove the dead `cortex-official` defaults and delete the stale telemetry
  docs/switch so the no-telemetry state is self-evident.
- [ ] **I3–I7** — rebrand residue: re-export the chip font, scrub fixture paths, fix the
  wheel name and repo-link gate, remove dangling CI/docs references and the broken
  `.claude/skills` symlink, reconcile vendor badges and README claims.
- [ ] **I9–I11** — record esbuild version + dependency lock + sha256 for `server.mjs`; add
  `third_party/` to `THIRD_PARTY_NOTICES.md`; remove `random-file.txt`, the stale
  `allowBuilds` line and `@yarnpkg/cli-dist`.

## Limitations of this audit

- Static analysis of the tracked tree at commit `9182f62` plus rebuilds and registry
  verification; build outputs (`dist/`, `lib/`) and untracked local files were not reviewed.
  Rebuilding with `pnpm build` before use guarantees the served bundle matches the audited
  source.
- External binaries the harness can launch — Codex CLI, Claude Code, VS Code
  `serve-web`/code-server, `uv` — carry their vendors' own telemetry and update behaviour,
  which is outside this repository.
- Vendor-SDK request headers (e.g. `X-Stainless-*`) and pi-ai's `prompt_cache_key`
  forwarding were read from source rather than wire-tested.

---

*Prepared 19 August 2026. Methodology: six parallel deep-read passes (host packages; client &
CLI; supply chain & vendor; embedded third parties; whole-tree pattern sweep; upstream
provenance diff) over the tracked tree, performed with Claude Code, with lockfile integrity
verified against registry.npmjs.org, vendored forks and the prebuilt MCP bundle
rebuilt/diffed against their upstreams, and the fork compared against
`deepseek-ai/deepseek-harness`.*
