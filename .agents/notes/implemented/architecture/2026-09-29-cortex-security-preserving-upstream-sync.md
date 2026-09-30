---
kind: architecture
description: Preserve Cortex privacy policy while adopting current upstream application and plugin interfaces.
---

# Security policy across the upstream synchronization

Cortex imports the current upstream architecture and maintains its privacy policy at composition boundaries and child-process launch. Keeping the old plugin implementations would preserve obsolete settings, session, and UI APIs and prevent newer orchestration features from composing reliably. The embedded editor and its sidecar launcher are removed at the user's request. The enterprise MCP integration and its bundled servers are removed; inert historical event types preserve existing session readability.

Telemetry exporters, web grounding, account onboarding, and native official-model defaults remain absent. Type-only analytics declarations satisfy UI contracts without creating an analytics runtime. The Models UI remains restricted to loopback gateway configuration; backend file configuration keeps the administrator capability that predates this update. Codex and Claude privacy overrides apply after user-supplied launch configuration. Stagehand requires a dependency patch because its logging switch does not disable trace export.

The user's request for default desktop control makes the native Cua Driver a narrow exception to experimental-package isolation in the Windows GUI composition. The driver remains experimental and shares the host tool registry so existing and new sessions receive its tools. Headless and SDK profiles do not gain desktop control. Its embedded SDK does not install a telemetry observer; the optional standalone MCP launch enforces telemetry and update-check opt-outs. Loader composition tests cover platform selection, profile overrides, session inheritance, provider exclusivity, and cleanup.

Required verification includes real Loader composition, missing prohibited services/tools, empty initial model routes, rejection of external UI probes, child-launch option precedence, Stagehand patch application, and compilation across host-generated remote contracts. Plugin installation uses no default fallback registry or automatic registry probes. Native child smoke tests use loopback fake model servers. The historical audit is scoped to its earlier revision rather than repeated as assurance about new dependencies.

The [synchronization guide](../../../../docs/cortex-upstream-sync.md) owns migration actions and limitations. No signed release, commit, push, or deployment is part of this local update.
