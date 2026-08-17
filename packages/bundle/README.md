# bundle/ — profile plugin bundles


Profile bundles: npm packages whose manifest declares `"cortex": { "bundle": { "patch": "./cordis.patch.yml" } }`, making them installable patch layers for `cortex --profile` compositions ([profile contract](../boot/app-boot/README.md#profiles)). A bundle's substance is its patch list; some also ship runtime glue plugins their patch mounts.

| Package | Role | ctx key |
|---|---|---|
| [`base/`](base/README.md) | The shared cortex core every profile applies first | — (patch only) |
| [`web-app/`](web-app/README.md) | Browser surface: web patch layer + runtime glue plugin | mounts rows |
| [`headless/`](headless/README.md) | Direct one-shot task mode over base, with no Host or Web layer | mounts `headless-runner` |

In-box bundles resolve from the cortex installation; out-of-tree bundles install into a profile through `cortex plugin --profile <name> add <package>`.
