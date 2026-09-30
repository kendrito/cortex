# Cortex upstream synchronization

## Imported scope

The exact DeepSeek Harness baseline is pinned in [upstream.json](../upstream.json), including its version, official tag, full commit, and import date. The commit identifies the imported snapshot even if an upstream tag later moves. Each [Cortex version tag](cortex-releases.md) preserves its own copy of this file. The import covers the current agent and session architecture, V4 session storage and migrations, profiles and bundles, settings/configuration services, PTC, SDKs, native integrations, browser and computer-use providers, office tooling, desktop application, and UI. Both Codex and Claude Code participate in the current subagent orchestration interfaces.

The embedded editor, its Code tab, pinned pane, sidecar launcher, and theme synchronization are removed. Conversation and trajectory views remain available. Cortex no longer starts or configures VS Code or code-server.

## Excluded services

The import omits the web grounding package family, DeepSeek-native model adapters and account provider defaults, session upload service, OpenTelemetry exporters, product analytics runtime, account settings UI, and web-search UI. Product analytics event declarations remain as types for upstream component compatibility; there is no corresponding runtime service, RPC endpoint, or exporter. The generic pi-ai adapter starts with an empty provider map. The Models UI admits only loopback OpenAI-compatible routes and rejects external probes before making a request. Administrator-authored backend configuration retains the earlier fork's generic provider capability.

The Atlassian integration, settings and work panels, Jira/Confluence/Bitbucket tools, and bundled servers are removed. Historical integration, receipt, and web-request event schemas remain readable so existing session logs migrate without losing data. Those inert formats do not register tools or send requests. Tests dedicated to removed account onboarding, vendor upload, and provider configuration features are excluded with their implementations.

Plugin installation uses the operator's package registry with no default fallback or background registry probing. The installer has no regional mirror shortcuts; additional registries require explicit configuration. Voice model setup uses the official source by default and retains operator-supplied custom sources.

Desktop launches the workspace directly, with no account or credit onboarding and no automatic updater or remote feed. Codex launches disable web search, analytics, feedback, update checks, response storage, and OpenTelemetry exporters. Claude launches enforce the supported privacy environment after user overlays and disallow WebSearch/WebFetch. The pinned Stagehand dependency uses a reproducible pnpm patch to disable trace collection and exporters in both its SDK and browser extension. A supplied incompatible unpatched extension fails initialization rather than falling back to telemetry.

Windows web and desktop profiles enable the experimental native [Computer Use provider](subsystems/computer-use.md) by default; headless and SDK profiles require explicit activation. Its embedded SDK has no telemetry exporter. The optional Cua Driver MCP launcher disables the standalone executable's telemetry and update checks. Web and desktop profiles enable Playwright browser automation and local full-text conversation search by default. The search index opens on demand and uses saved local session logs; search requires no model request. These restrictions are not a universal egress filter for shell commands, MCP plugins, administrator configuration, enterprise-managed child policies, or third-party tools. Browser/runtime preparation can download dependencies. The historical [security audit](../SECURITY-AUDIT.md) covers its recorded revision only.

## Upgrade actions

1. Back up `$CORTEX_HOME`, workspace configuration, and session files before launching the new version. Keep the old executable and backups together for rollback; migrated session files are not a downgrade format.
2. Install the lockfile dependencies with pnpm so the pi-ai and Stagehand patches apply. Build the host before the client because the host build generates remote contracts.
3. Review [the versioned upstream upgrade guides](upgrade-guide) for configuration and session changes. Old package families such as `code-runtime` and `agent-presets` have been replaced by PTC and the current agent-preset registry. Do not copy removed telemetry, account, upload, or web-grounding rows into the new bundles.
4. Configure the local gateway in Settings → Models and choose its model. Settings writes go to `$CORTEX_HOME/profiles/<profile>/cordis.patch.yml` (`web` for the default browser profile). Legacy `settings.yaml` is imported once and renamed to `settings.yaml.imported`; inspect that backup for rejected sections. No official cloud provider is selected for a fresh profile. Existing file-configured routes remain administrator-owned.
5. TypeScript SDK consumers use `CortexHarness`, `CortexHarnessOptions`, and `createProcessCortexHarness`. Refer to the current Python SDK and examples for its corresponding client interfaces.
6. Real model tests require `CORTEX_TEST_BASE_URL` and `CORTEX_TEST_MODEL`, with optional `CORTEX_TEST_API_KEY`. The endpoint must be an HTTP(S) loopback gateway. Ordinary replay and mock tests need no model credentials.
7. Remove obsolete `atlassian` and `ui-atlassian` overrides from existing profiles. Their panels, `/ticket`, `/pr-review`, and bundled MCP servers are unavailable. Existing session records stay readable; saved credentials are not deleted automatically. Remove any old fallback registry overrides if you want plugin installation to use only the primary registry.

## Verification scope

Validation covers compilation and bundling, privacy/composition regressions, Models UI restrictions, SDKs, and native subagent smoke tests using loopback mock servers. Platform-specific and real-service tests need their own environments; local checks do not certify live cloud models, every OS sandbox, signed installers, or the entire dependency supply chain.

The complete historical model-recording snapshot corpus is not qualified against the new local-gateway adapters. Older record overlays can retain metadata for removed native-provider features; use the current explicit local test fixtures for new recordings. Windows file-symlink tests require Developer Mode or equivalent privilege and cannot be validated on a host that rejects their fixture setup.

The SDK `account-provider-signout` snapshot is excluded because it specifically exercises the removed vendor account provider and its sign-out cancellation lifecycle. It is not treated as generic local-provider coverage. Historical recordings for SDK `serial-created` and `subagent-cortex-sdk-diagnostic` (removed upload integration), and headless `web-fetch` and `web-search-endpoint-guidance` (removed web grounding), remain in the corpus but are excluded by name from executable scenario selection. Historical native-provider recording overlays remain unvalidated; the imported corpus has not been certified as a whole.
