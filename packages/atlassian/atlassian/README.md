# @cortex/atlassian


Jira, Confluence, and Bitbucket **Data Center** integration for the Cortex harness. One service plugin (`ctx.atlassian`) mounts the two MCP servers the agent uses, gates their writes, mirrors every touched entity into the session log for the browser panel, runs PR review mode, and serves the panel's Remote API. The browser half is [`@cortex/client-ui-atlassian`](../../client/ui-atlassian/README.md).

## What it composes

| Concern | Mechanism |
|---|---|
| Agent tools | Two `@cortex/mcp-client` children mounted from settings + credentials: [`sooperset/mcp-atlassian`](https://github.com/sooperset/mcp-atlassian) as `mcp__atlassian__jira_*` / `mcp__atlassian__confluence_*`, and [`n11techhub/mcp-bitbucket`](https://github.com/n11techhub/mcp-bitbucket) as `mcp__bitbucket__bitbucket_*`. Both `serverName`s are fixed because the browser keys its cards on them. |
| Write gate | `tools/pre-execute` decision for mutating Atlassian tools from the `writes` setting: `ask` (default, the approval chip), `allow`, or `deny`. Reads always delegate. |
| Live panel data | `tools/result` observer: every Atlassian call appends one `atlassian/activity` entry; search-style results append `atlassian/search` rows; each touched issue/page/PR is re-fetched over REST (bounded to three per call) and appended as `atlassian/snapshot`. The `atlassian` session projection folds these log-only events into the whole-value panel state the host pushes to the browser. |
| Commands | `/ticket <PROJ-123\|clear>` pins the session's ticket (`atlassian/pin` + snapshot); `/pr-review <PROJECT/repo#id\|URL> [instructions]` starts a review run: the host first reads the comments already on the pull request (REST activities), embeds them in the reviewer's instructions, and records them on the review so the panel shows them. |
| Review tools | `atlassian_review_finding` and `atlassian_review_complete`, registered globally, valid only while a review is running in the caller's session (they refuse otherwise). A finding repeating an already-recorded one (same file, line, category) is refused; a finding within three lines of an existing pull request comment is refused unless `acknowledgeExisting: true` marks it materially new, and then carries the overlapped comment ids. Each finding is an `atlassian/review` log event, so the panel updates as the agent works. |
| System prompt | `atlassian:guidance` section (fixed prose, only while a server has tools) and `atlassian:session` context (pinned ticket and running review). |
| Remote API | `ctx.remote.atlassian.{status, reconnect, probe, open, pin, listPullRequests, postFinding, dismissFinding, cancelReview, diffContext}` (Typert). |

## Settings (`atlassian` namespace)

| Field | Default | Meaning |
|---|---|---|
| `jiraUrl`, `confluenceUrl`, `bitbucketUrl` | `''` | Data Center base URLs. |
| `jiraTokenRef`, `confluenceTokenRef`, `bitbucketTokenRef` | `ATLASSIAN_JIRA_TOKEN`, `ATLASSIAN_CONFLUENCE_TOKEN`, `ATLASSIAN_BITBUCKET_TOKEN` | Credential references resolved through `ctx.credentials` per operation; the tokens themselves live in the credentials store, never in settings. |
| `jiraProjectsFilter`, `confluenceSpacesFilter` | `''` | Forwarded as `JIRA_PROJECTS_FILTER` / `CONFLUENCE_SPACES_FILTER`. |
| `bitbucketDefaultProject` | `''` | Project key used when the model or a PR reference omits one (`BITBUCKET_DEFAULT_PROJECT`). |
| `atlassianLaunch` | `uv run --frozen --project <repo>/third_party/mcp-atlassian mcp-atlassian` | Launch line of the Jira/Confluence server. The default runs the pinned copy embedded under [`third_party/`](../../../third_party/README.md); when that tree is absent it falls back to `uvx mcp-atlassian`. |
| `bitbucketLaunch` | `node <repo>/third_party/mcp-bitbucket/server.mjs` | Launch line of the Bitbucket server. The default runs the prebuilt self-contained bundle embedded under `third_party/` (no npm install, no Docker); when absent it falls back to the `ghcr.io/n11techhub/mcp-bitbucket` container image. The mount also sets `PROJECT_ROOT` to `$CORTEX_HOME/mcp-bitbucket` so the server's log directory stays out of the repository. |
| `writes` | `ask` | Write gate policy. |
| `toolsets`, `enabledTools` | `default`, `''` | Forwarded as `TOOLSETS` / `ENABLED_TOOLS` to bound the 98-tool catalog. |

A mount starts when its launch line parses and at least one URL + token pair resolves; a startup failure is reported through `status()` (`phase: 'error'`) and retried by `reconnect()`. Changing any of these settings or a referenced credential remounts the affected child.

## Session events

All five are log-only, non-surface, and never reach a model request: `atlassian/snapshot`, `atlassian/activity`, `atlassian/search`, `atlassian/pin`, `atlassian/review`. Their payloads and the `atlassian` projection value are documented in `src/types.ts`; the fold and every bound (30 entities, 5 searches, 40 activity rows, 200 findings per review, 12 kB bodies) live in `src/projection.ts` and `src/markup.ts`.

## Model Experience

### Guidance section while a server is mounted

#### What the model sees

The `atlassian:guidance` system-prompt section renders only while at least one of the two mounts has registered tools:

##### Verbatim guidance

```markdown
Atlassian tools: `mcp__atlassian__jira_*` and `mcp__atlassian__confluence_*` reach Jira and Confluence; `mcp__bitbucket__bitbucket_*` reaches Bitbucket Server (project key + repository slug + numeric prId). Prefer `jira_get_issue` before changing an issue, `jira_get_transitions` before `jira_transition_issue`, and quote issue keys exactly (PROJ-123). Every read and write is mirrored into the user's Atlassian panel automatically.
```

#### Token effect

Fixed while a mount is live; zero otherwise.

#### KV Cache effect

Prefix-stable; mounting or unmounting a server flips the section on or off, which invalidates the prefix once.

### Session context (pinned ticket, running review)

#### What the model sees

The `atlassian:session` context contributes, when set, `The active Jira ticket of this session is <KEY> ("<summary>", status <status>). "This ticket" refers to it.` and/or `A pull request review of <PROJECT/repo#id> is running: record each problem with atlassian_review_finding and finish with atlassian_review_complete.`

#### Token effect

Conditional and small; replaced whenever the pin or review state changes.

#### KV Cache effect

Travels after retained history as a tail snapshot, so a change does not rewrite the stable system-prompt prefix.

### MCP tool set and results

#### What the model sees

The mounted servers' tool schemas as `@cortex/mcp-client` registers them (see that package), bounded by `TOOLSETS` / `ENABLED_TOOLS`; results are the servers' JSON text. The write gate adds no model-visible text beyond an ordinary denial or approval outcome.

#### Token effect

Fixed schema prefix per mount (data-dependent size); results append through the ordinary tool-result pipeline.

#### KV Cache effect

Prefix-stable while the tool set is unchanged; a remount that changes the tool list invalidates once.

### Review tools and the review instruction turn

#### What the model sees

Two fixed tool schemas ([`atlassian_review_finding`](../../../docs/tool-catalog.md#atlassian_review_finding) and [`atlassian_review_complete`](../../../docs/tool-catalog.md#atlassian_review_complete) in the generated tool catalog) and, when `/pr-review` runs, one queued user-role turn wrapped in `<pr_review>…</pr_review>` (source `plugin: atlassian`, form `instructions`) that names the pull request, the review procedure, and the comments already on the pull request (up to 40, one line each) with the instruction not to repeat them; its stable text is `reviewInstructions()` in `src/review.ts`. A refused duplicate or overlap answers with the existing finding id or the overlapping comments quoted.

#### Token effect

Fixed schemas; one data-dependent instruction turn per review; each finding call appends its short JSON result.

#### KV Cache effect

Append-only.

## Known Limitations and Deferred Work

- **Data Center only** — REST calls use `/rest/api/2` (Jira), `/rest/api` (Confluence), and `/rest/api/1.0` (Bitbucket Server) with bearer personal access tokens; Atlassian Cloud is not addressed by this package.
- **Snapshots need REST credentials** — the panel is fed by REST re-fetches with the same URL + token the MCP mounts use; a streamable-HTTP MCP deployment without those settings gets activity rows but no entity snapshots.
- **No pull-request listing tool in the Bitbucket MCP server** — the picker lists PRs over REST (`dashboard/pull-requests`, per-repository listing); the model has no equivalent tool.
- **Findings anchor to the current diff** — `postFinding` resolves the anchor against the live diff; a line the diff no longer contains posts as a general comment prefixed with `file:line`.
- **Images are not surfaced** — attachment and page images are named, not fetched; the MCP bridge discards image blocks.
