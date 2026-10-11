# Micheng Skills Manager for Cortex

Private local port of `@michengai/dsh-skills-manager@1.1.13`, pinned to upstream `9881133c2fb9b1ea16492675e7fdf3260ab42098`. Preserve the upstream Apache-2.0 license ([LICENSE](LICENSE)); files changed for Cortex are listed in the git history of this directory.

## Built into Cortex

Since 2026-10-11 the manager ships as part of Cortex rather than as a profile plugin. The `@cortex/web-app` bundle depends on this directory and inserts its `skills-manager` row, the row is on the plugin manager's protected list, and the package is no longer installed into profiles. It therefore never appears in the Plugins page and cannot be switched off or uninstalled there. This directory's own `cordis.patch.yml` is kept only for a standalone install outside Cortex.

The manager provides local global/project skill browsing, policy toggles, creation/import, public repository subscriptions, update previews, backups/rollback and Trash/restore. Existing skill bodies are read-only in this manager. Other agents' skill source files remain read-only.

The Cortex port uses the selected `CORTEX_HOME`, Cortex skill scopes and persisted Session queries, and authenticated same-origin Connection handlers. Provider/model configuration is not part of the manager. Its model-facing create tool is disabled by default; UI creation remains available.

There is no plugin updater or automatic npm check. Activation and ordinary local management make no third-party requests. Public GitHub archive downloads occur only for explicit repository scan/refresh/install/update actions; repository metadata and backups remain local. Foreign skill sources default to opt-in. Local filesystem imports do not require a model or repository download.

Dependencies are provisioned separately with lifecycle scripts disabled. Built Cortex peers and prepared public dependencies are linked locally. Use the supported Cortex plugin CLI with `--ignore-scripts --offline` for the prepared package. Runtime and installation acceptance are recorded in `STAGING.json`; do not infer successful installation from this source directory alone.

`npm test` now runs the maintained Cortex typecheck/build, 15 actual core/host/client checks and four client script suites (including 2,301 locale assertions). Set `CORTEX_CHECKOUT` to the built Cortex checkout. Native Cortex changes forward `skills/change` through API remotes and invalidate the slash-menu cache; five remotes checks and 33 UI-skill checks pass. [CORTEX-HOST-AUDIT.md](CORTEX-HOST-AUDIT.md) explains the host boundaries.

The isolated full browser check created a synthetic skill, opened its body, disabled and reenabled it, moved it to Trash and restored it, then opened the repository view without downloading anything. The isolated profile had no model/provider credentials; no model calls were made. Repository ZIP installation, update and rollback use synthetic archives in tests; an actual external repository download was not performed during browser acceptance.
