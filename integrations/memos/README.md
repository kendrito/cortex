---
description: "Local persistent memory, recall, reflection, and skill evolution for a Cortex profile."
kind: "package-bundle"
---

# @cortex/memos-local-memory

## Summary

This private MemOS fork gives Cortex persistent memory, automatic recall, reflection, learned policies, world models, and reusable skills. Storage and embeddings stay on the local host. Every language-model request uses the owning Cortex session's selected provider and model; a cloud provider still receives those requests. Preparing this checkout does not install or activate the bundle in a profile.

## Contents

- [Prepare the local package](#prepare-the-local-package)
- [Install into a Cortex profile](#install-into-a-cortex-profile)
- [Runtime and model behavior](#runtime-and-model-behavior)
- [Explore learned memory](#explore-learned-memory)
- [Maintenance](#maintenance)

## Prepare the local package

Use Node 22.19 or newer and a built Cortex checkout with its dependencies installed. Provision a compatible 384-dimensional MiniLM embedding model separately. Its absolute local directory must contain `config.json`, `tokenizer.json`, `tokenizer_config.json`, and `onnx/model_quantized.onnx`. The setup script does not download or run the model.

From this package directory, install the locked dependencies and explicitly rebuild the two native dependencies:

```sh
npm ci --ignore-scripts --legacy-peer-deps
npm rebuild better-sqlite3 onnxruntime-node
```

Dependency installation and native rebuilds can access package or binary distribution servers. Runtime privacy does not make dependency provisioning offline; use preprovisioned caches when provisioning must also stay offline. The upstream postinstall script is not part of this setup.

Run setup with absolute paths, then build both the adapter and viewer:

```sh
node scripts/cortex-setup.mjs --cortex-root "C:/work/cortex" --home "C:/work/cortex-memory" --model-dir "C:/models/all-MiniLM-L6-v2"
npm run build:package
```

On macOS or Linux, supply equivalent absolute paths. Setup discovers the package's optional Cortex peers under `packages/*/*` and `vendor/*` in the supplied checkout, checks their built JavaScript and declarations, and links them into this package's `node_modules/@cortex`. Windows uses directory junctions; other systems use directory symlinks. Setup creates only a new memory `config.yaml` under `--home`, including the local embedding path. It does not edit Cortex providers, credentials, profiles, or the process environment.

Setup refuses existing peer destinations, including broken links, and an existing `config.yaml`. It validates all inputs before creating links. Use a fresh dependency installation and a new memory home for a new setup; preserve an existing database and configuration when changing an established installation. If a filesystem error interrupts setup, it removes only links and the configuration created by that invocation; empty directories may remain.

## Install into a Cortex profile

After preparation, use the built Cortex CLI to link this development checkout into the intended profile:

```sh
cortex plugin --profile web add --ignore-scripts --offline "link:C:/work/cortex/integrations/memos"
```

Replace the path with this package's actual absolute directory and `web` with the intended profile. The package declares its [Cortex bundle patch](adapters/deepseek-harness/cordis.patch.yml); installation adds that layer to the selected profile. This command is a separate activation step, not an action performed by setup. The integration owner must verify installation and startup against the target Cortex build.

Point the adapter's `home` setting at the same directory passed to setup, or set `MEMOS_HOME` for the Cortex process. For example, in PowerShell:

```powershell
$env:MEMOS_HOME = 'C:\work\cortex-memory'
cortex --profile web
```

An explicit `MEMOS_HOME` takes precedence over the adapter setting. Without an override, the adapter uses `$CORTEX_HOME/memos-plugin`, or `~/.cortex/memos-plugin`. Do not point it at another agent's existing MemOS home. See the [adapter configuration](adapters/deepseek-harness/README.md#configuration) for profile settings and startup failures.

## Runtime and model behavior

The full memory pipeline remains available through the Cortex host bridge, including recall, capture, reflection, policy induction, world-model updates, and skill evolution. The [adapter reference](adapters/deepseek-harness/README.md) owns model routing, workspace isolation, tools, recall timing, and failure behavior. Recovery jobs that lack an owning Cortex model route are disabled.

The private runtime disables telemetry, remote sharing, update checks, separate memory providers, and model downloads. The viewer binds to `127.0.0.1` on port `18801` by default and serves its assets locally. It retains memory browsing, local settings, explicit backup import/export, and embedding maintenance; see the [viewer reference](viewer/README.md). A missing local model or required host service stops startup instead of selecting a network fallback.

Memory state is separate from this checkout: the selected home contains `config.yaml`, `data/memos.db`, learned `skills/`, and `logs/`. Back up that home before maintenance. Logs and learned memories can contain session content. At idle, Cortex waits for the completed turn's memory checkpoint before admitting the next turn or finishing a headless run.

## Explore learned memory

The separate private `@cortex/memos-brain` bundle adds **Brain** to Cortex's native sidebar. It serves the Brain-only frontend through Cortex authentication and reads the selected local database without starting the memory pipeline. An absent database remains absent and displays an empty collection. This panel can be installed independently of the full memory adapter. Its [package reference](../memos-brain/README.md) records the host routes and verification.

Open **Brain** in the local viewer, or use `http://127.0.0.1:18801/#/brain` when running on the default port. The view connects stored memories, tasks, experiences, environment knowledge, and skills through their recorded evidence. Select a node to inspect its content and sources; use search, filters, and the list or timeline to explore the records. Counts and pagination distinguish the records currently loaded from the full local collection.

The 3D constellation uses locally bundled Three.js, lit spheres, perspective, and an orbit camera. Drag to rotate, Shift-drag to pan, scroll or pinch to zoom, and reset to restore the view. Keyboard controls support orbit, zoom, node navigation, and evidence selection. Drawing happens only when the view changes; there is no idle animation or remote graphics resource. Browsers without WebGL can use the list.

The graph displays saved experience and generated abstractions. Connections indicate recorded evidence, not independently verified facts. Missing or unloaded references are identified rather than replaced with invented links. Drawing, searching, and browsing the Brain data do not invoke a language model or an embedding provider.

Brain displays only records from the selected local database. An empty collection remains empty until the memory pipeline has captured data. Legacy `demo=1` bookmarks also read the local collection. The viewer retains its existing local authentication and network restrictions.

## Maintenance

[UPSTREAM.json](UPSTREAM.json) pins the upstream source and this package version. Preserve [LICENSE](LICENSE) when redistributing. This source package includes the Cortex adapter and local runtime; other-agent installers and adapters are outside its supported distribution.

Rebuild after source edits with `npm run build:package`. A development link keeps using this checkout and its own dependencies. Reinstalling dependencies removes local peer links; the setup script intentionally does not rewrite an existing memory configuration. Dependency refreshes for an established installation require deliberate peer relinking while preserving that configuration.

Run `npm run lint` and `npm run test:cortex` for the supported Cortex and Brain checks. Set `MEMOS_TEST_MODEL_DIR` to the provisioned model directory to include actual ONNX embeddings, SQLite capture, and persistent recall rather than skipping that test. Tests create separate synthetic memory homes; they do not use the live database or credentials. Generated builds, dependencies, provisioned models, memory databases, learned skills, logs, and credentials are excluded from Git.
