# Local MemOS viewer

The Cortex memory adapter serves this Preact application on its loopback HTTP listener. It browses stored memories, sessions, tasks, skills, policies, environment knowledge, events, and logs. Explicit backup import/export and embedding maintenance operate on the selected local memory home. Cortex owns the model selection and credentials; this viewer cannot configure another provider, download a model, enable sharing, check for updates, or send telemetry.

Browser API requests use same-origin `/api/v1/` paths and reject redirects. Assets, styles, and Three.js are bundled locally. The standalone server retains its authentication and anti-framing policy. See the [memory package](../README.md) for preparation, installation, model requirements, and local storage.

## Brain

`#/brain` shows a 3D graph, list, and timeline of saved memories, tasks, experience, environment knowledge, and skills. Connections come from recorded evidence references; they do not certify that a learned claim is true. Counts distinguish loaded records from collection totals. Select a node to inspect its details and evidence; use search, filters, and pagination to explore the collection.

Drag to orbit, Shift-drag to pan, and scroll or pinch to zoom. Camera reset and keyboard controls are available. The locally bundled renderer draws on demand and releases GPU resources when unmounted. A browser without WebGL can use the complete list. Browsing and camera movement perform no model or embedding calls.

Brain displays only records read from the selected local database. An empty database stays empty, including when opened with a legacy `demo=1` bookmark. No example dataset or sample controls are included in the viewer.

`brain.html` is the Brain-only entry for the [native Cortex panel](../../memos-brain/README.md). It excludes standalone navigation, authentication screens, settings, and background pollers. Under `/api/memos-brain/view/brain.html`, requests use `/api/memos-brain/api/v1/brain/*` and Cortex's same-origin authentication; the standalone viewer's API key is neither read nor attached.

## Build and verify

Run `npm run build:viewer` from the memory package directory to build both HTML entries. Run `npm run test:cortex` for the supported viewer, Brain, API-prefix, and privacy checks. The server must enforce its local-only configuration and Content Security Policy; browser restrictions do not replace server-side controls.
