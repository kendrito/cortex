# @cortex/client-ui-atlassian


Browser half of the Atlassian Data Center integration: the live **work panel**, the **keyed tool cards** for the Jira / Confluence / Bitbucket MCP tools, and the **Settings → Plugins → Atlassian** tab. The host half — MCP mounts, write gate, entity snapshots, the `atlassian` projection, `/ticket`, `/pr-review`, review tools, and the Remote API — is [`@cortex/atlassian`](../../atlassian/atlassian/README.md).

## What it registers

| Seat | Entry | What it does |
|---|---|---|
| `conversation.session.header.actions` (`id: atlassian`, order 30) | `AtlassianAction` | A compact trigger (glyph, tracked-entity count, live-review dot) and the right-hand drawer it opens through a portal. Live data arrives through `useProjection('atlassian')`; panel state (open, tab, selected entity, auto-open bookkeeping, review filter) lives in the per-session `createPanelStore()` store shared with the cards; every host verb rides the injected `PanelFace` (`ctx.remote.atlassian.*`, `/pr-review` through `ctx.remote.commands.execute`, prompts through the session binding). |
| `tool.call.toolview` (one entry per `CARD_TOOLS` key, plus `atlassian_review_finding` / `atlassian_review_complete`) | `AtlassianCard`, `FindingRow`, `ReviewCompleteRow` | Replay-stable accent rows over each logged call: the tracked entity from the projection, captured search rows as a table, a Bitbucket diff as the native diff block, file content as code, otherwise the raw JSON tree. "Open in panel" focuses the entity through the shared store. |
| `settings.plugins.tab` (`id: atlassian`) | `AtlassianSettingsTab` | Three service cards (URL, personal access token → credentials store, scope field, live mount status, connection test), the MCP launch commands, the write policy, and the toolset bounds. Writes go one field at a time through `ctx.settingsScope.bind({ namespace: 'atlassian' })`; tokens through `connection.api.credentials.set`; both are loopback-only, so a remote browser sees the read-only notice. |

## The drawer

**Work** shows the entity in focus — the projection's focus unless the user picked one from the recent list — as a full issue view (status, people, sprint/epic/points, transitions menu, comment composer, description, links/subtasks, comments with fresh ones highlighted, attachments), page view (breadcrumb, version, labels, converted body), or pull request view (state, branches, author, reviewers with approval state, description, **Review this PR**, approve/merge/comment prompts). **Review** is the pull request picker (review inbox or one repository) and the live run: status, verdict + summary, severity histogram, all/pending/posted filters, bulk post, and one card per finding with the proposed comment (editable before posting), the evidence with lazily loaded diff context that highlights the anchored line, the rationale, the suggested fix, and **Post comment** / **Dismiss**. **Activity** is the session's Atlassian activity timeline.

Auto-open: the first projection frame after mount only acknowledges; a later frame whose focus changed or whose review just started opens the drawer (Work or Review tab) while "Open automatically" is on. Escape closes it. On viewports under 460px the drawer is a full-width sheet.

Every prompt-shaped action ("Move to", "Comment…", "Approve", …) queues text into the session so the agent performs the write through the MCP tools and the ordinary write gate; the panel updates from the resulting snapshot. Only review-comment posting talks to Bitbucket directly (host REST, explicit user click).

## Model Experience

None, as this package renders the `atlassian` projection and logged tool calls for a human and touches no prompt, message, schema, stream, or tool result; every model-facing effect belongs to [`@cortex/atlassian`](../../atlassian/atlassian/README.md).

#### KV Cache effect

None; the package never assembles or sends provider requests.

## Known Limitations and Deferred Work

- **The drawer floats over the columns** — it is a `shell`-level portal, not a fourth layout column, so it covers the details column while open instead of pushing the chat.
- **Cards depend on the projection for rich content** — a call whose entity the host could not re-fetch (REST not configured, or a tool the host does not know) renders the generic disclosure; the raw result stays available in the tool details panel.
- **Settings and tokens write only from the host machine** — the settings scope and the credentials API are loopback-restricted by the harness; a browser reached through a proxy reads them.
- **One locale (`en`)** — copy lives in `src/client/locales.ts`; the harness ships no other language yet.
