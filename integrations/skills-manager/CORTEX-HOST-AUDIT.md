# Cortex host port review

Upstream: [MichengAI/dsh-skills-manager](https://github.com/MichengAI/dsh-skills-manager/tree/9881133c2fb9b1ea16492675e7fdf3260ab42098), package `@michengai/dsh-skills-manager@1.1.13`, commit `9881133c2fb9b1ea16492675e7fdf3260ab42098`. This is a local Cortex port; it is not an upstream compatibility claim. Archive and dependency lock hashes are recorded in `STAGING.json`.

## Host behavior

- All 25 GET/HEAD/POST method registrations use Cortex Connection's shared authenticated `/api` carrier. Authentication and Host/Origin checks happen before plugin body reads or Session lookup. No separate unauthenticated WebServer route is installed.
- POST actions require the UI action marker and JSON. The native carrier streams request bodies; this plugin stops and cancels above 1 MiB (88 MiB for local ZIP uploads), rather than buffering the carrier's larger default allowance.
- Home resolution uses `@cortex/home-paths.resolveCortexHome()`. Writable native skills use the selected Cortex home and the selected Session project's `.cortex/skills`. Internal `dsh` state keys and API names remain for data compatibility, not as an alternate home lookup.
- Project selection reads the requested persisted Session with `sessionQuery.observeSession`, then releases the observation. It neither activates an Agent nor falls back to another Session.
- Foreign application skill roots are visible but disabled by default. The manager does not scan their contents into the catalog until that source is explicitly enabled. Foreign source files remain read-only.
- Native skills without an explicit manager policy remain owned by Cortex's filesystem provider. Policy overlays occupy the actual preset scope, preserving custom-source ranks and cold Session catalogs. Disabling masks both invocation channels; enabling preserves the skill's own `disable-model-invocation` and `user-invocable` restrictions.
- Preset diagnostics run outside Host plugin activation. Disposal does not wait on diagnostics that may themselves be awaiting Loader settlement; late leases are released without registering providers. Owned registrations and leases are released on unload.
- UI mutations invalidate the registered skill providers. The accompanying small Cortex change forwards the existing `skills/change` event to the browser; native `ui-skill` clears cached catalogs and pending previews. It does not manufacture unrelated preset or connection events.
- The optional `create_skill` model tool requires explicit `exposeCreateSkillTool: true` configuration and the actual Cortex tools service. It defaults off, and UI creation remains available. No provider/model configuration is read or changed by this port.

## Network and filesystem review

The npm plugin updater has been removed. Its compatibility source file is an empty module, and no updater route, timer, subprocess, telemetry sender, or background version request is installed. Construction, page state, repository listing, and repository details use local data.

Explicit repository scan/download actions retain upstream GitHub support. Validated owner/repository/ref values form GET requests only to `https://codeload.github.com/...`; redirects are rejected, requests time out after 60 seconds, and archive transfers are bounded. New scans cache archives by SHA-256, and later install/update previews use that local verified cache. Old 40-character commit records can download their pinned archive during an explicit operation. No skill contents or credentials are uploaded. Tests substitute an in-memory download response; they make no external requests.

Local folder/ZIP imports, Trash restoration, update preview tokens, backups and rollback remain supported. Tests cover traversal, absolute/drive paths, Windows device/trailing-dot aliases, directory junctions, forged Trash destinations, modified files, stale update tokens, and rollback. Read-only source discovery intentionally permits directory links with cycle/budget guards; this is explicit source discovery, not a filesystem sandbox. Uploaded/imported and writable destination operations retain their stricter link checks.

## Verification

- Strict stage TypeScript check and combined Host/client build pass.
- Independent published-artifact tests use real Cortex Connection, SkillRegistry and filesystem providers. They verify route admission before reads, streaming limits, absence of updater routes and background fetches, native policy preservation, unload/reload, two preset scope precedence, cold catalog reads, lease cleanup, and deferred-loader startup/disposal.
- Independent real-filesystem tests cover source opt-in, invocation restrictions, import/Trash containment and manual repository backup/update/rollback using synthetic data only.
- The final combined acceptance command passes 15/15 tests: `node --test test/cortex-core-safety.test.mjs test/cortex-host-runtime.test.mjs test/cortex-client-runtime.test.mjs` with `CORTEX_CHECKOUT` set to the built original Cortex checkout (core 7, host 7, client 1, including subtests).
- The original Cortex remote-event suite passes 5 tests. Both API remotes TypeScript leaves and runtime bundles are rebuilt. Native `ui-skill` passes 33 browser-plugin checks, including catalog replacement and cancellation of a pending preview; its TypeScript leaves and runtime bundles are rebuilt.
- The supported isolated Cortex web profile on port 3093 reaches normal ready state with this port enabled. It uses a synthetic home and no copied provider credentials. Browser acceptance is recorded separately by the coordinating task.
- Real HTTP checks confirm authenticated state/repository reads return 200, unauthenticated reads return 401, cross-origin requests return 403, and updater routes return 404. The content-free record is `../../skills-manager-smoke-home/host-check.json`.

This review does not claim the legacy upstream aggregate test command passes: it still contains retired updater/host-stub assumptions unless replaced by the coordinator. No live provider files, user skill files, live enable flags, or historical Sessions were changed by these host implementation or test steps. Live installation is a separate coordinator action and defaults disabled.
