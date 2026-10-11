---
kind: upgrade-guide
description: "Selective local capability and reliability improvements from DeepSeek Harness 0.2.1-alpha.2, preserving Cortex providers and privacy defaults."
---

# Cortex local capability improvements

## Change

This selective backport adds plugin style disposal and uninstall repairs, Windows shell reliability fixes, independent text/code/terminal font controls, Session plugin records, working-directory switching, optional local Git worktrees, dynamic model context/tool support, and managed agent activations. It does not replace the baseline recorded in [upstream.json](../../../../upstream.json) or import every upstream release feature.

The immutable Session header continues to identify the original project. The working-directory service records the effective directory and presents changes to the model. Existing processes retain their launch directory. File, shell, skill, instruction, workflow, SDK, and child-agent operations use the effective directory when supported. Permissions do not expand automatically.

Git Worktrees is an optional built-in plugin and stays disabled until selected. Creation uses only local refs and disables Git hooks, filesystem monitors, checkout filters, and lazy fetching. It requires Git 2.45 or newer. Disabling the plugin retains created branches and directories.

Subagent integrations use managed activations instead of the removed one-shot/continuable start methods. Local child conversations support continuation; external execution records are not local browsable Sessions. Agent-team messages steer their recipients directly. Swarm uses the managed API and retains its own run/result delivery.

Font settings store independent sizes and local/system font family names for text, code, and terminal roles. Existing text-size preferences remain valid. No fonts are downloaded.

## Migration

1. Back up the Cortex profile, Session files, and MemOS home. Install locked dependencies and build Host contracts before Client bundles. Keep existing model/provider configuration; this update adds no default route.
2. Replace `subagents.start` and `startContinuable` calls with `startActivation`. Choose caller or parent result delivery explicitly. Remove obsolete `backgroundMode` tool configuration. Await child completion and descendant teardown before retiring providers.
3. Theme consumers read `ThemeSnapshot.fontSizes` and `fontFamilies`; use `setFontSize(role, px)` instead of the former single-argument method.
4. To use worktrees, enable **Plugins → Official → Git Worktrees**. Configure its `gitCommand` if the system Git is older than 2.45. `working_directory({ cd: path })` leaves a worktree without removing it.
5. Plugin authors register namespaced Session records and use the record APIs for durable state. Existing Session generations remain readable; do not modify historical formats.
6. MemOS keeps ownership tied to the Session's original project and records the effective directory as metadata. Fresh Sessions created in separate worktrees remain isolated. Rebuild linked MemOS and Swarm packages against the updated contracts. Brain continues to show only the genuine local database, including empty states.
