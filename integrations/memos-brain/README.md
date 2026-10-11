# MemOS Brain for Cortex

This private package adds a native **Brain** sidebar panel to Cortex using the adjacent [MemOS integration](../memos/README.md)'s locally built 3D viewer. Installing Brain does not enable the memory pipeline, capture conversations, download a model, or change AI providers.

## Prepare the package

Use Node 22.19 or newer and a built Cortex checkout with its dependencies installed. Prepare and build [MemOS](../memos/README.md#prepare-the-local-package) first, including `npm run build:package`. Brain requires its SQLite runtime and viewer assets but does not require an embedding model or language-model provider to inspect existing memory.

From this directory:

```sh
npm ci --ignore-scripts --legacy-peer-deps
node scripts/cortex-setup.mjs
npm run typecheck
npm run build
npm test
```

Setup defaults to the repository containing `integrations/memos-brain`. To use another built Cortex checkout, pass `--cortex-root "C:/work/cortex"` and set `CORTEX_CHECKOUT` to that directory when running tests. Equivalent absolute paths work on macOS and Linux. Setup discovers declared Cortex peers by package name, validates their built runtime and declarations, and links them into this package's `node_modules/@cortex`. It refuses existing peer destinations and removes only links created by a failed invocation. It does not access the network or edit a profile, memory configuration, or credentials.

`npm ci` may access package distribution servers; use a preprovisioned package cache when provisioning must also remain offline. Dependencies use a relative `file:../memos` link, so keep these two source directories adjacent. Build artifacts and dependency directories are excluded from Git.

## Install into a profile

After building, use the supported Cortex CLI with the actual absolute package directory:

```sh
cortex plugin --profile web add --ignore-scripts --offline "link:C:/work/cortex/integrations/memos-brain"
```

Select **Brain** in the sidebar of that profile. Cortex serves its frame and assets through the existing authenticated connection. The standalone MemOS viewer's authentication and framing policy remain separate.

## Stored memory and privacy

Home resolution matches MemOS: `MEMOS_HOME`, then the parent of `MEMOS_CONFIG_FILE`, then the plugin's optional absolute `home`, then `$CORTEX_HOME/memos-plugin` or `~/.cortex/memos-plugin`. Use the same home as the memory adapter. Brain reads `data/memos.db` without migrations or retained SQLite handles; missing stores remain absent and display an empty collection. The panel displays only stored records and never substitutes an example dataset.

All graphics and API reads remain local to Cortex. Brain's graph routes are read-only, with Cortex authentication and same-origin admission. It does not load the standalone settings or memory-editing UI, telemetry, embeddings, capture, or model providers. No prompt or tool is added, so activation has no model-token or KV-cache effect.

## Memory controls

Memory ships with Cortex, so the Brain tab is where a person pauses it or starts over:

- **Memory on/off** switches recall, capture and the memory tools off and back on. It stops MemOS cleanly, keeps what was learned, and survives restarts (`cortex-memory.json` in the memory home).
- **Reset memory…** asks for confirmation, then stops MemOS, deletes its store (`data/memos.db` and its WAL files) and starts it fresh if memory is on.

Both go through `/api/memos-brain/api/v1/memory` (`GET` for status, `POST …/enabled` with `{ "enabled": boolean }`, `POST …/reset` with `{ "confirm": "reset" }`). The routes need the Cortex session and a JSON body, and refuse cross-site requests. Brain never touches the store itself: it hands each request to MemOS as the `memos-local-memory/control` Cordis event, so a reset never races MemOS's own writer.

## Validation and limitations

`npm test` exercises the actual built Cortex Cordis, Connection, SlotRegistry, and UI renderer. Host checks cover admission, truthful empty stores, fixture-backed memory/evidence/pagination, unchanged database bytes, asset paths, and route disposal. The client check covers native navigation, frame creation, unmount, and repeated enable/disable without duplicate registrations. Viewer checks mount the actual Brain component for native and standalone legacy URLs, including empty stores, refresh, stored-record details, and read errors. Tests use isolated fixtures and make no model calls.

Brain requires the adjacent MemOS build and a compatible Cortex build; it is not a standalone application. Older or unprepared database schemas report an error rather than being migrated. Its read-only requests show live storage and do not promise a transactionally frozen graph across separate requests. No runtime invariant entry is published because the panel and its request-scoped reader own no independently maintained state that can diverge.
