# Agent Note: Atlassian Data Center integration (host seam + work panel + PR review mode)

Status: implemented

## Problem

The harness could mount an MCP server as agent tools, but every Jira / Confluence / Bitbucket result rendered as the generic tool card and nothing carried the touched entities to the person watching. A team asking the agent to "comment on this ticket" or "move it to In Review" saw JSON text, not the ticket. Reviewing a pull request with the agent had no place to accumulate findings, show the evidence, or post a chosen finding onto the exact diff line in Bitbucket.

## Decision

Two packages own the integration; both MCP servers stay the agent's tool surface, the host owns the panel data, and the browser owns the visuals.

`@cortex/atlassian` (`packages/atlassian/atlassian`, service `ctx.atlassian`, default-exported `TypertRemoteService`):
- Mounts `sooperset/mcp-atlassian` as `mcp__atlassian__*` and `n11techhub/mcp-bitbucket` as `mcp__bitbucket__*` as `@cortex/mcp-client` children from the flat `atlassian` settings namespace (URLs, credential references, filters, launch lines, write policy, toolsets) plus `ctx.credentials`; the two `serverName`s are fixed because the browser keys its cards on them. Mounts remount on settings/credential change; a startup failure is reported by phase and retried by `reconnect()`.
- Gates mutating Atlassian tools through `tools/pre-execute` from `writes: ask|allow|deny`; reads always delegate.
- Observes `tools/result`: appends `atlassian/activity` for every call, `atlassian/search` rows for list-style results, and re-fetches each touched issue / page / pull request over the Data Center REST APIs into `atlassian/snapshot` (bounded to three per call). All five `atlassian/*` events are log-only, non-surface, and registered in the generated known-event catalog. The `atlassian` session projection folds them into the whole-value panel state with fixed bounds.
- `/ticket` pins the session's ticket (`atlassian/pin` + snapshot, surfaced through the `atlassian:session` prompt context); `/pr-review` starts a review run: the host reads the comments already on the pull request (REST activities, flattened threads with inline anchors), records them on `atlassian/review start`, snapshots the PR, and queues one plugin-sourced `<pr_review>` instruction turn that lists those comments with the instruction not to repeat them. The agent records findings with `atlassian_review_finding` / `atlassian_review_complete` (globally registered, refusing outside a running review), each an `atlassian/review` event. The finding tool refuses an exact repeat (same file, line, category) and refuses a finding within `OVERLAP_LINES` of an existing comment unless `acknowledgeExisting: true`; an acknowledged finding carries the overlapped comment ids, which the panel renders as a "near an existing comment" marker with the comments quoted.
- Remote API for the browser: `status`, `reconnect`, `probe`, `open`, `pin`, `listPullRequests` (REST inbox / repository, since the Bitbucket MCP server has no listing tool), `postFinding` (anchor resolved against the live diff; general comment fallback), `dismissFinding`, `cancelReview`, `diffContext`.

`@cortex/client-ui-atlassian` (`packages/client/ui-atlassian`):
- A `conversation.session.header.actions` entry: trigger + portal drawer over `useProjection('atlassian')` with Work / Review / Activity tabs, a per-session `createPanelStore()` (open, tab, selection, auto-open bookkeeping) shared with the cards, and a `PanelFace` of host verbs. Prompt-shaped actions queue text into the session so writes go through the agent and the gate; only review-comment posting talks to Bitbucket directly on an explicit click.
- Keyed `tool.call.toolview` cards for the curated `CARD_TOOLS` names and the two review tools, replay-stable over the frozen block plus the projection.
- A `settings.plugins.tab` with the three service cards (URL, token → credentials store, scope field, live mount status, probe), launch lines, write policy, toolsets.

Registration surfaces touched: `tsconfig.base.json` paths (new `packages/atlassian` group + client package), `tsconfig.host.json` / `tsconfig.client.json` references, `packages/bundle/web-app/{cordis.patch.yml,package.json}`, `packages/api/remotes` (mount + types), `WEB_SETTINGS_NAMESPACES` in the apiproxy, the tool-catalog boot manifest, the model-experience allowlist, and the generated catalogs.

## Consequences

- The panel is per-session and replayable: reopening a session restores exactly what the agent touched. Cross-session boards would need host polling (not built).
- The panel needs the same URL + token the MCP mounts use; a deployment that only configures MCP over streamable HTTP without those settings gets activity rows but no entity snapshots.
- Data Center only (bearer PATs, `/rest/api/2`, `/rest/api`, `/rest/api/1.0`); Atlassian Cloud would need a second REST adapter and the Cloud MCP configuration.
- Every Atlassian read costs one extra REST round-trip for the snapshot; bounded to three entities per call.
- The drawer floats over the columns instead of adding a fourth layout column, so it can cover the details column while open.

## Alternatives considered

- Taking over the `details` slot at a shadowing priority: rejected because the tool inspector's selection store is private to `ui-conversation` and cannot be re-hosted, so the shipped inspector would disappear while the panel is docked.
- Feeding the panel from MCP tool result text only: rejected because write results are tiny (comment/transition responses do not carry the ticket) and because MCP image/attachment blocks are discarded by the bridge; a REST re-fetch after every touch gives the panel the real current state.
- Executing MCP tools from the host for the panel's own fetches: rejected because that re-enters the tool pipeline (approval gates, listener observation) with an agent-less execution and depends on the exact tool set the mounts expose.
- One flat set of `mcp-client` rows in the user's `cordis.patch.yml` with no host package: rejected because tokens would sit in config, there would be no gate, no panel data, and no settings UI.
