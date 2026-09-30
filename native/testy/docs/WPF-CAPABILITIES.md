# WPF control operations and AI context

Testy remains AI directed. During a saved AI run, the model sees the test, a screenshot, the current control tree, advertised capabilities, and completed-step evidence. Local-tool runs request one canonical saved step at a time; native OpenAI runs combine physical computer actions with explicit saved assertion and advanced-control functions. The model receives each result before its next decision. The engine owns input dispatch and independent assertions. It cannot accept a model's success statement in place of observed evidence. Replay is a separate, explicitly selected mode.

Common control-pattern operations work through external UI Automation or an opt-in in-process probe when the selected provider advertises support. Version 0.4 additionally provides probe-only standard DataGrid editing and bounded WPF diagnostic context. The assistant can author these operations as saved steps and receive their results during execution. The Studio layout is unchanged.

## Action contracts

Selectors retain the existing exact `id:`, `name:`, `path:`, and `query:` syntax. The values below are JSON strings inside a saved step's `value` field.

| Action | Selector | Value example | Meaning |
|---|---|---|---|
| `Expand` / `Collapse` | One observed tree node or expandable control | Empty string | Change only that control's expansion state. |
| `RealizeItem` | One item container | `{"item":{"label":"Order 1999"}}` | Find a unique logical item, then request its realization. |
| `ScrollIntoView` | One realized item | Empty string | Request visibility without selecting the item. |
| `ScrollPercent` | One scroll container | `{"vertical":100}` | Set supplied axes to 0–100 percent; omitted axes stay unchanged. |
| `AssertProperty` | One observed control | `{"property":"uia.isReadOnly","equals":true}` | Compare a known typed property without changing the UI. |
| `AssertItemExists` | One item container | `{"item":{"label":"Order 1999"}}` | Prove a unique logical item exists without realizing it. |
| `AssertItemAbsent` | One item container | `{"item":{"label":"Order 9999"}}` | Prove the supported logical lookup returns no item. |
| `GridEditCell` | One opted-in DataGrid | `{"rowKey":"INV-0001","columnKey":"Quantity","text":"7"}` | Begin a standard text-cell edit and set its generated editor text. Probe only. |
| `GridCommitRow` | The same DataGrid | `{"rowKey":"INV-0001"}` | Request cell and row commit for the pending business row. Probe only. |
| `GridCancelRow` | The same DataGrid | `{"rowKey":"INV-0001"}` | Request cell and row cancellation for the pending business row. Probe only. |

Item lookup accepts exactly one nonempty `id` or `label`, not a row index. Unsupported lookup keys are failures, not evidence of absence. Duplicate matches fail before realization. A placeholder is reacquired after the uniqueness check because a subsequent lookup can invalidate a previous placeholder. Microsoft documents both this lifetime rule and the requirement that placeholder lookup leave the UI unchanged. [ItemContainer contract](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementingitemcontainer)

Logical lookup refers to the collection exposed by the control's current view. Filters apply: an item removed from the filtered view can be absent there while still existing in the application's backing data. These assertions do not inspect a database or silently remove the filter.

Uniqueness is limited to **provider-exposed logical peers**. Standard WPF can suppress peers for equal backing items, such as repeated identical strings or the same object instance. Unequal backing items with duplicate accessible labels are detected and rejected when exposed as separate peers; hidden equal entries cannot be independently counted through this interface. Use distinct data items with stable unique keys when their identity matters. [WPF item-peer implementation](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Automation/Peers/ItemsControlAutomationPeer.cs)

Realization and visibility are separate operations. A provider may scroll as a side effect of realization, but it need not do so. Use `ScrollIntoView` when the workflow requires visibility. [Working with virtualized items](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-workingwithvirtualizeditems)

Every value is validated before execution. Unknown and duplicate JSON fields, incorrect types, nonfinite numbers, empty item keys, and invalid percentages are rejected. Assertions do not expand trees, realize items, scroll, select, or focus controls implicitly. A failed or timed-out mutation is not automatically repeated.

## Opt-in DataGrid edit transactions

Grid editing requires the integration in [the probe setup guide](PROBE.md#enable-standard-datagrid-editing): an enabled standard WPF `DataGrid`, explicit column keys, and rows implementing `IGridRowIdentity` and `IEditableObject`. The driver advertises `GridEdit`, `GridCommit`, and `GridCancel` on an enabled, writable grid. The external UIA driver does not advertise these operations and refuses them; it does not substitute model assignments or physical clicks.

The grid selector must be unique. Row and column keys match exact, case-sensitive strings of 1–256 UTF-16 code units; blank keys are invalid. Editor text permits 0–4,096 code units, including an empty string. Row lookup examines at most 20,000 items in the **current filtered view**, using the explicit business-key contract rather than accessibility item peers. All current-view items must supply valid keys, and two matches for the requested key are rejected. The chosen column key must also be unique. Sorting and visual column reordering do not alter these keys; filters can remove a row from the view.

`GridEditCell` explicitly realizes and scrolls the row, selects it, focuses the cell, and calls `DataGrid.BeginEdit`. It supports standard text, checkbox and noneditable combo columns, plus a template column that explicitly opts into one uniquely identified supported editor. Each editor must have the appropriate writable standard binding; exact boolean and unique choice-label rules apply. The driver rechecks row reference and column identity before changing the actual editor. Password and arbitrary third-party editors remain unsupported. See the [standard editor matrix and limits](WINDOW-SURFACES-AND-EDITORS.md#standard-datagrid-editor-matrix). A different pending cell must be committed or cancelled before editing another cell. Normal binding update triggers remain authoritative and may update provisional application data during editing; the bridge does not assign backing fields or arbitrary model properties.

`GridCommitRow` requires that exact business row to be the pending edit, requests cell commit followed by row commit, and verifies that editing ended. Validation or an application veto fails the operation. `GridCancelRow` requests both cancellation stages and verifies that editing ended; the application's `IEditableObject` implementation owns rollback, so assert the restored value separately. Each command is a saved step with its own evidence.

**A successful UI commit is not proof of persistence.** An asynchronous save may still be pending, fail, or store an incorrect value. Follow commit with exact assertions against application-visible saved state, using the normal assertion deadline for delayed results. Cancellation cannot undo an already dispatched commit or an application's asynchronous side effects. The bridge does not retry these operations automatically.

## Properties and collection coverage

Properties have an observation status: `Known`, `Unsupported`, `Unavailable`, `Redacted`, or `Truncated`. Only a known scalar can satisfy a property assertion. Boolean `false`, the string `"false"`, null, unavailable, and unsupported are distinct. An omitted property means unsupported; it must not be read as zero or false.

Registered UIA properties are `uia.value` (including a known empty string), expansion state, selected state, read-only state, horizontal/vertical scroll percentage, grid row/column, and grid row/column counts. Their names begin with `uia.`. Probe-only observations include `wpf.validationHasError`, `wpf.hasItems`, `wpf.isVirtualizing`, `wpf.enableRowVirtualization`, and `wpf.enableColumnVirtualization`. Version 0.4 adds `wpf.gridIsEditing`, `wpf.gridEditingRowKey` (empty when no row is editing), `wpf.bindingStatus`, and `wpf.commandCanExecute`. Binding status is observed only from standard TextBox/TextBlock text bindings. Command availability queries `RoutedCommand.CanExecute` on buttons; custom `ICommand` implementations are reported unavailable. Availability depends on the actual control and provider. These are bounded observations, not arbitrary CLR expressions or DataContext evaluation.

Legacy display text in `Value` is bounded to 8,192 UTF-16 code units and carries `IsValueTruncated`. Text assertions refuse a truncated value; a matching prefix cannot prove exact full text. Native coverage also refuses truncated text/selection state. UIA TextPattern uses an extra code unit to detect overflow before trimming terminal paragraph line breaks, so values at that raw-provider boundary can be conservatively unavailable. Typed `uia.value` has a separate 2,048-unit limit: a complete 3,000-unit legacy value can satisfy `AssertText` while its typed property correctly remains `Truncated` and cannot satisfy `AssertProperty`.

`ChildCoverage` describes whether a container exposes complete children, realized children only, or unknown coverage. A complete traversal (`IsTruncated=false`) is not a statement that every virtualized item exists in that tree. A zero-match `AssertNotExists` is refused when noncomplete collection coverage leaves logical absence unproven. Use `AssertItemAbsent` for a supported collection lookup.

An element that disappears, is virtualized or has its container recycled while the UIA driver reads it marks the snapshot incomplete (`IsTruncated=true`) instead of failing it. This covers both forms the UIA client uses for that condition: `ElementNotAvailableException` and a property read that returns `UIA_E_ELEMENTNOTAVAILABLE`. The runner then observes again within the step's readiness deadline or the shared eight-second post-step evidence deadline. A completed action is never repeated.

Grid row/column positions are observations at a point in time. Sorting, filtering, column reordering, and recycling can change them. Resolve a fresh stable item key after changes. A text entry operation does not prove a DataGrid edit committed; cell and row commit/validation need separately verified workflow steps. [WPF DataGrid commit behavior](https://learn.microsoft.com/en-us/dotnet/api/system.windows.controls.datagrid.commitedit?view=windowsdesktop-9.0)

For the standard WPF DataGrid peer, `uia.row` is the index in the current items view and `uia.column` is the index in the Columns collection. Merely changing a column's visual `DisplayIndex` does not change that collection index. Do not equate it to a screen coordinate. [WPF cell-peer implementation](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Automation/Peers/DataGridCellItemAutomationPeer.cs)

## WPF diagnostic context

Probe snapshots include up to the latest 64 `ApplicationDiagnostics`: timestamped validation-state changes, binding-status changes, observed button/routed-command events, and explicit grid-operation outcomes. Each record carries a source, kind, observation status and bounded classification message. `DiagnosticsTruncated` signals dropped history independently of tree truncation. This is diagnostic context for the model and reviewer, not a complete application log or proof of a failure's cause. An observed Click or Executed event does not prove that a handler or save completed.

The probe withholds validation error contents, binding paths, DataContext values, exception contents and command parameters. Password-subtree events and identities are redacted. `CanExecute` invokes the application's normal command query; a failed query is unavailable, not false. Snapshots do not form an atomic transaction with application event history or screenshots.

Each historical record preserves its event-time `ObservedRuntimeId` and bounded `ObservedAutomationId`. A current `Selector` is attached only when that runtime identity is unique and its automation ID, bounded ancestor context, source object and nearest standard item's reference still match. Recycled visual containers therefore cannot attach an old item's event to a new item solely because their visual identity survived. Unavailable, changed or incomplete identity leaves the selector empty and marks `IdentityStatus` accordingly. This continuity check does not establish business causality or durable item identity across process restarts.

## Runtime and input boundaries

The external UIA driver uses the target's accessibility provider. The WPF probe requires explicit integration in an application you own, runs on its dispatcher, and uses standard automation peers plus a small property allowlist. The supplied probe targets .NET 9; there is no arbitrary injection into an unmodified EXE and no shipped .NET Framework 4.x probe.

Probe snapshots include opaque visual-object identities in `RuntimeId`, prefixed `wpf:` and scoped to the process and probe session. They stay stable for the same live object, including when a virtualized container is reused for different data; use the fresh key and properties to identify the current item. Restarting the process or probe creates a new identity scope. These are not native UIA runtime IDs and cannot satisfy a physical-input receipt.

Semantic actions verify the provider's advertised operation. They are not proof that a physical mouse or keyboard path works. Bound native OpenAI runs advertise an explicit `perform_saved_control_step` tool for advanced WPF operations alongside the native computer tool. Ordinary physical interactions retain independent input-receipt checks; the semantic tool cannot execute them as a fallback. Assertions use `verify_saved_assertion`. Tool names and saved IDs record which route actually executed each step. Desktop input and image capture require a usable interactive Windows session.

## Comparison with TestComplete

SmartBear documents object-specific WPF operations, grid-cell access, table checkpoints, template-generated elements, native methods/properties, and support for standard and third-party controls. Those are a broader compatibility target than basic accessibility clicks. [TestComplete WPF control support](https://support.smartbear.com/testcomplete/docs/app-testing/desktop/wpf/control-support.html)

TestComplete also has automated execution, self-healing object recognition, OCR operations, and AI-generated test data. It should not be characterized as a purely manual tool or as lacking AI. Testy's specific focus is model-directed authoring and execution with fresh application context and independently verified saved acceptance conditions. That focus can suit a custom workflow, but any reduction in authoring or maintenance effort remains unproven without a comparison on the same real application. A model call at every step also adds latency, cost, and model failure modes. [Self-healing](https://support.smartbear.com/testcomplete/docs/testing-with/running/self-healing-tests.html), [AI operations](https://support.smartbear.com/testcomplete/docs/keyword-testing/reference/ai/index.html), [AI data generation](https://support.smartbear.com/testcomplete/docs/testing-with/data-driven/generators/ai-wizard.html)

The current work strengthens Testy's explicit AI feedback, immutable saved acceptance conditions, typed evidence, collection lookup, common control operations, and an explicit standard-grid edit workflow. It does **not** establish overall TestComplete parity or superiority. No head-to-head TestComplete benchmark was run. Arbitrary native methods, third-party grids, legacy-runtime probes, other editor types, large enterprise applications, and long-running worker stability still need focused compatibility work.

The included `Testy.WpfLab.exe` is a repeatable stress fixture with 2,000 virtualized orders, duplicate names, sorting/filtering, reordered columns, nested trees, validation, a password control, a templated button, and a modal. Use `verify-wpf` for driver coverage and `run-ai` with an appropriate provider for actual model-directed execution. Check the release verification report for what passed, what failed as expected, and what remains unverified.

Launching `Testy.WpfLab.exe --grid-workflows` selects a separate editable fixture with 1,000 business-keyed invoices. It has validation, duplicate-key modes, commit vetoes, delayed/failed/incorrect save modes, binding repair and routed-command diagnostics. Its independent persisted-state display uses an in-memory fixture store; it is not a database durability test. Fixture coverage and protocol simulations must not be described as live model-provider coverage; consult the [current verification report](../verification/FUNCTIONALITY-RESULTS.md) for the actual evidence.
