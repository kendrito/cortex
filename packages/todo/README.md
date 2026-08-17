# todo/ — todo / planning capability family


The model-facing todo capability. It is a single **product** package because one agent session owns the list; there is no replaceable provider contract.

| Package | Role | ctx key |
|---|---|---|
| [`tool-todo/`](tool-todo/README.md) | Stores and exposes the session's todo list. | (registers on `ctx.tools`) |

The child README owns the tool, persistence, and rendering contract.

The event payload is documented on [docs/subsystems/session.md](../../docs/subsystems/session.md).
