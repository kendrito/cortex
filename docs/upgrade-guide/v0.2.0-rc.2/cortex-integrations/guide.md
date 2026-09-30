---
kind: upgrade-guide
description: "Cortex removes the bundled enterprise MCP integration and automatic registry fallbacks, removes the embedded editor, and enables GUI automation and local conversation search."
---

# Integration and download defaults

## Change

Cortex no longer ships the Atlassian host or client plugins, their Jira/Confluence/Bitbucket MCP servers, the work and settings panels, or the `/ticket` and `/pr-review` commands. Existing session event records remain readable through inert historical types.

Plugin installation uses the primary package registry without a default fallback, automatic registry probes, or regional download shortcuts. Explicitly configured additional registries remain supported. Voice model setup defaults to the official source and retains custom source configuration.

Windows web and desktop profiles enable the experimental native Computer Use provider by default. Headless and SDK profiles require explicit activation. Desktop observations, including screenshots when supported, become model input and use the configured provider.

## Migration

1. Remove `atlassian` and `ui-atlassian` overrides from profile, home, and invocation patch files. Existing credentials and session files are not deleted automatically.
2. Review `plugin-manager.config.fallbackRegistries` overrides. Remove them or set `[]` to keep package operations on the primary registry. Existing package-manager configuration remains authoritative.
3. Restart Cortex. The removed integration no longer appears in Settings or session actions. Configured model routes remain available.
4. Review [Computer Use configuration](../../../subsystems/computer-use.md) to disable desktop control or select a different provider. Disable the native provider before enabling the MCP provider; only one provider can own the desktop tools.

The embedded editor integration is removed, including its Code tab, pinned pane, editor process startup and theme synchronization. Remove `editor` and `editorPort` from `web-runtime` overrides. Existing VS Code/code-server installations and their files are not deleted. Web and desktop profiles now enable Playwright Browser Use and local full-text conversation search; both retain profile configuration overrides.

The Windows x64 Web and Desktop profiles also include [Testy](../../../../packages/extensions/testy/README.md). Its testing pane and chat tools share a Cortex-owned workspace. Integrated AI execution follows Cortex's model selection; separate Testy provider settings are ignored. Existing standalone Testy data is not copied automatically. The `testy` plugin can be disabled in Built-in plugins or with an `id: testy, disabled: true` profile patch. Disabling stops owned work while preserving saved tests and results.
