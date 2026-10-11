# Cortex Progress Board

A live progress card pinned above the composer. It shows:

- an estimated progress bar, the current step and the elapsed time;
- the agent's `todo_write` list, each item ticked, active, paused or pending;
- a row per subagent (one level of nesting) with its own step count and what it is doing now.

The card takes the built-in todo card's seat in `conversation.input.dock` (same id, lower priority), since it shows the same list.

## How it works

- **Host** (`src/host.ts`): registers a `progressBoard` session projection. It folds each session's `turn/start`, `tool/call`, `todo/write` and `turn/end` events into board state. It also adds one prompt section asking agents to keep `todo_write` current and to post a final update before they end their reply.
- **Client** (`src/client.ts`): reads `progressBoard` and `subagentCatalog` for the open session. It joins each child session's own `progressBoard` from the session list, and redraws every second while work runs.
- **Model** (`src/board.ts`): the fold, the estimate and the rows the card draws, shared by both halves.

The estimate rises with tool calls and slowly with time, scaled by how many calls this session's turns usually take. Once a todo list exists (or, failing that, subagents), the remaining bar is split across its items. The bar never slides back while working. If a turn ends with items still open, the bar stops at the finished share and turns amber rather than claiming done.

## Build and install

```sh
npm ci --ignore-scripts
npm run build
npm test
cortex plugin --profile web add --ignore-scripts --offline "link:<absolute path to this directory>"
```

Restart Cortex web after installing; the server reloads clients when `lib/client.js` changes.
