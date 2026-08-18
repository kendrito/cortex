# atlassian


Atlassian Data Center integration group: Jira, Confluence, and Bitbucket reach the agent through two mounted MCP servers, and reach the user through the live entity panel, PR review mode, and chat cards of the browser client.

| Package | ctx key | Role |
|---|---|---|
| [`atlassian`](atlassian/README.md) | `atlassian` | Host seam: MCP mounts from settings + credentials, write gate, tool-result observation into `atlassian/*` log events, the `atlassian` projection, `/ticket` and `/pr-review` commands, review tools, and the panel Remote API |

The browser half lives at [`packages/client/ui-atlassian`](../client/ui-atlassian/README.md).
