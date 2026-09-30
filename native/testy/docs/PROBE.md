# Windows automation and the opt-in WPF bridge

Testy has two concrete local drivers. Both attach to a primary visible top-level window in an explicitly selected process ID and inspect the process's other visible top-level windows, including modal dialogs. Windows UI Automation works without modifying a target that provides accessible controls. The WPF probe exposes the application's visual tree through a cooperating library, including visuals omitted by accessibility peers.

Each top-level HWND is inspected as its own tree root. Some UIA providers also expose an owned dialog beneath its owner; that repeated root is skipped there and inspected separately, so the same physical control is not mistaken for two ambiguous controls. Distinct controls with duplicate IDs or names remain ambiguous. A new top-level window appearing during traversal makes the snapshot incomplete until refreshed. Controls in a disabled or modal-blocked owner window are reported disabled, and their mutations are refused even if the accessibility provider reports the individual control enabled. Active capture handles are normalized to the top-level parent, preserving separately owned modals and preventing child-button-only captures.

## Add the probe to your own WPF application

Reference `Testy.WpfProbe.csproj` (and its `Testy.Core` dependency). After your `Application` is created, call this on its Dispatcher and retain the returned disposable:

```csharp
probe = Testy.WpfProbe.ProbeServer.Start();
```

Dispose it on application exit. TestLab already does this in its App class. In Testy, open **Change app**, turn on **Use the WPF helper**, and connect to that application. The bridge is optional, local, and opt-in: Testy does not inject a DLL or run arbitrary code inside third-party processes. The supplied build targets .NET 9 WPF; no .NET Framework 4.x probe or arbitrary EXE injection is shipped.

The pipe is `testy-wpf-{PID}`, restricted by Windows to the same OS user; the client also verifies the named-pipe server's PID. Requests are bounded and time-limited. Only snapshot, read-only item lookup and predefined control-action requests are accepted. Actions execute on the application's Dispatcher. There is no reflection evaluator, file operation, shell execution, or arbitrary method invocation endpoint. Enable this bridge only in development/test builds: another process under the same Windows user could issue its supported UI actions while it is running.

### Enable standard DataGrid editing

Grid editing requires a second, explicit opt-in on each supported grid:

```csharp
Testy.WpfProbe.GridAutomation.SetEnable(ordersGrid, true);
Testy.WpfProbe.GridAutomation.SetColumnKey(quantityColumn, "Quantity");
```

Each item in the current grid view must implement `Testy.WpfProbe.IGridRowIdentity`, whose read-only `TestyRowKey` identifies the business row. Edited rows must also implement `System.ComponentModel.IEditableObject` for the normal WPF edit/rollback transaction. Keys must be stable, nonblank strings of at most 256 UTF-16 code units. Column keys are explicit attached properties on the actual `DataGridColumn`; display order and header text are not identifiers. The bridge rejects duplicate requested row or column keys and views exceeding 20,000 items.

Use the unique grid selector, for example `id:EditableOrders`, with these separate saved steps:

```text
GridEditCell   {"rowKey":"INV-0001","columnKey":"Quantity","text":"7"}
GridCommitRow  {"rowKey":"INV-0001"}
```

Use `GridCancelRow` with the same row payload to request rollback instead. `GridEditCell` supports standard text, checkbox and noneditable combo columns, plus a template column explicitly opted in with `GridAutomation.EditorId` matching one bound standard editor. Checkbox text is exactly `true`/`false` (`null` only for three-state); combo text is an exact unique supported choice label. It explicitly realizes, selects and focuses the cell, calls `BeginEdit`, rechecks identity and changes the actual editor property while preserving its binding. It does not directly set model properties. Commit and cancel use the DataGrid's cell and row transaction APIs; application validation and vetoes remain active. Password/custom/ambiguous editors and arbitrary third-party grids are unsupported, and UIA attachment refuses these probe-only actions. The precise [editor support matrix](WINDOW-SURFACES-AND-EDITORS.md#standard-datagrid-editor-matrix) defines the opt-in and binding limits.

An exited UI edit is not a verified save. Assert application-visible persisted state after commit, and restored cell data after cancel. Binding update triggers can change provisional data before commit; asynchronous handlers can still fail afterward. The full payload limits, properties and workflow boundaries are in [WPF capabilities](WPF-CAPABILITIES.md#opt-in-datagrid-edit-transactions).

## Controls, selectors, and input

Prefer unique `AutomationProperties.AutomationId` values. `id:CustomerName` and `name:Customer name` perform exact matching and reject ambiguity across all inspected windows. Structural selectors (`path:0/1/2`) begin with a window index (`0` is the attached main window), followed by zero-based child indices. They are sensitive to template/layout changes, opening/closing windows, and differ between the UIA tree and WPF visual tree. Never reuse a path across driver modes. KeyPress uses the UIA focus mechanism in both modes; use an ID/name selector for KeyPress in WPF probe mode.

For repeated controls, use an explicit composite query, for example:

```text
query:{"id":"ApplyAction","type":"Button","label":"Apply","ancestor":{"label":"Shipping"}}
```

`id` matches `AutomationId`, `label` matches the accessible `Name`, and `type` matches the snapshot's `ControlType`. Supplied properties are combined with AND using exact, case-sensitive strings. `ancestor` must match a strict ancestor, at any distance, within the same inspected tree. It can itself have an `ancestor` constraint: `query:{"label":"Apply","type":"Button","ancestor":{"label":"Shipping","ancestor":{"id":"OrderWindow"}}}`. Each object needs at least one of `id`, `type`, or `label`; a query allows at most eight objects and 2,048 characters. Unknown or duplicate keys, empty values, malformed JSON, and unsupported types are rejected before execution. JSON escaping preserves literal quotes, slashes and Unicode in labels. Queries do not match by displayed text value, guessed proximity, or fuzzy similarity.

Scope remains valid when sibling sections are reordered, provided the specified properties and ancestry remain unchanged. A repeated ancestor name can still leave multiple matches: actions require exactly one matching control, and no-match waits remain bounded by the step timeout. Truncated snapshots cannot prove scoped uniqueness or absence. Queries use the observed preorder tree and depth, so scope never crosses siblings or top-level windows. UIA and the WPF visual tree can have different ancestors; build a query from the selected driver's snapshot. Probe-mode KeyPress rejects paths and queries because its focus mechanism uses the different UIA tree; use a unique ID/name or UIA attachment for scoped keyboard input. Existing selectors retain their behavior, and saved selectors are never silently rewritten or healed.

Saved steps can separately contain explicitly authored selector alternatives. The AI must select one approved alternative through the bound recovery tool; the original selector must be provably absent, and the alternative must be unique with the saved role and identity. The engine records the substitution separately from the canonical test and rechecks it before use. Probe recovery validates and acts on the same Dispatcher tree. UIA rechecks live runtime identity, role, ID and name immediately before input, but separate cross-process reads and input cannot be atomic. There is no fuzzy repair or automatic retry after uncertain input. Probe recovery does not support KeyPress/CoordinateClick through its separate UIA focus mechanism.

UIA supports Invoke, Value, Toggle, SelectionItem, and select-by-item-name. The WPF probe supports Invoke/SelectionItem via automation peers, editable TextBox, ToggleButton/CheckBox, and ListBox/ComboBox selection. Password values are redacted and password typing is refused. Snapshots record enabled/offscreen flags and screen-space physical-pixel bounds. The WPF probe's offscreen flag means `!IsVisible`; it does not detect clipping or desktop occlusion. Custom controls need appropriate accessibility patterns or an explicit coordinate action. No coordinate fallback happens silently.

Probe `Toggle` sets the boolean `IsChecked` state and raises normal state-change notifications; it does not simulate Click, call custom `OnToggle` overrides, or provide three-state cycling. Standard WPF's UIA Toggle peer also invokes its toggle operation without the separate Click path. An application that depends on mouse/keyboard Click behavior needs an explicit input test and outcome assertion. State-dependent application logic should be tested against the actual event or binding it uses. [WPF toggle peer](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Automation/Peers/ToggleButtonAutomationPeer.cs), [WPF ToggleButton implementation](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/ToggleButton.cs)

Snapshots also expose `ScreenshotBounds` (the physical-screen union of eligible visible same-PID top-level surfaces) and `FocusedSelector` (the observed focused target control's stable selector, or empty). The AI workflow verifier uses pre-action metadata to map native screenshot coordinates and typing back to required saved controls. These fields are computed from current state. Both drivers additionally expose `IScreenshotEvidenceSource.LastScreenshot`, binding actual image path, PID, timestamp and complete origin/size to the successful capture; the runner checks this metadata against its tree and refreshes mismatched pairs within the existing evidence deadline.

For standard expandable, virtualized, grid and scrolling controls, see [WPF capabilities](WPF-CAPABILITIES.md). Snapshot properties distinguish known values from unavailable, redacted and truncated observations; omitted properties are unsupported. Collection coverage can be realized-only even when traversal is complete. Assertions never implicitly realize, expand or scroll an item. Probe `RuntimeId` values are opaque `wpf:` visual-object identities scoped to the process and probe session; recycled containers can keep that identity while displaying different data. They are diagnostic identities and cannot satisfy native UIA input receipts.

Probe `ApplicationDiagnostics` retain at most 64 timestamped classified validation, binding, command and grid-operation observations. Raw error contents, binding paths, command parameters and application data are withheld; password-subtree records are redacted. Event-time identities are preserved, and historical records receive a current selector only when the source, exact bounded IDs/ancestor context and nearest standard item reference still match uniquely. Container recycling or uncertain identity leaves that selector empty. `IdentityStatus` and `DiagnosticsTruncated` distinguish uncertain attribution and dropped history. These records can guide investigation but do not establish a root cause or successful persistence. See [diagnostic context](WPF-CAPABILITIES.md#wpf-diagnostic-context).

KeyPress accepts letters, digits and CTRL/SHIFT/ALT chords with ENTER, TAB, ESCAPE, SPACE, BACKSPACE, DELETE, HOME, END and arrow keys. System navigation/close chords are not supported. Key input and explicit CoordinateClick require a target-process window to obtain foreground focus. Selector-based keys focus the window containing that element. Coordinates are physical pixels relative to the composed image's top-left (`ScreenshotBounds.X/Y`); they are not absolute desktop coordinates or WPF device-independent units. With one window this remains its outer top-left. Gaps between captured windows reject input, and a point must hit the topmost captured same-PID surface at that position. Surface/active-window changes require fresh evidence. A user can still move focus between the checks and Windows processing input; do not interact with the desktop during input-based runs.

## Evidence and operational limits

Screenshots compose PrintWindow renders of eligible visible same-PID top-level HWNDs in screen position and Z-order, including owned dialogs and titleless popups. Minimized, DWM-cloaked and non-client-only helper surfaces are excluded by explicit eligibility rules, not titles. Neutral gaps contain no desktop pixels. Limits are 16 surfaces, 8192 pixels per side and 24 megapixels. Testy never falls back to a full desktop screenshot. Transient unreadable/changed layouts get bounded read-only retries; persistent failed rendering returns an error. Capture validation examines each **client area separately**, using GetClientRect/ClientToScreen, so a painted title bar cannot disguise a black or nearly uniform application body. Failed captures clear cached coordinate authorization. Near-uniform clients are rejected conservatively, including intentionally blank application surfaces. GPU-rendered/protected/transparent content may still produce incomplete nonuniform screenshots; this check cannot recognize every rendering artifact. Inspect evidence when using a new framework. See [surface eligibility, mapping and operational limits](WINDOW-SURFACES-AND-EDITORS.md#captured-surfaces-and-coordinates).

Blank-client and activation failures include read-only input-desktop diagnostics. During an earlier verification attempt, foreground HWND was zero, input-desktop access failed with Win32 error 5, the running process was in session 1 and the active console was session 2. These observed signals establish that the process could not access the active input desktop; they do not establish a specific lock/disconnect cause. A painted title bar plus black client is failed evidence, regardless of passing UIA value assertions. After the interactive desktop became available again, fresh physical checks produced readable client and modal screenshots; the earlier failed evidence remains preserved.

The UIA tree is limited to 2,000 elements, 30 levels and approximately 10 seconds by default; virtualized/offscreen items may not exist until the target realizes them. Incomplete traversals set `IsTruncated`; selector assertions and mutations reject truncated trees so partial results cannot falsely prove absence or uniqueness. UIA operations run off the Studio UI thread with a 20-second managed deadline. A stuck COM/native provider cannot safely be forcibly interrupted; a timed-out native invocation can still complete later. Cancellation and driver disposal stop waiting/scheduling further steps, not already-dispatched Windows actions. Run untrusted or persistently hanging targets in a disposable test VM. Probe requests have an 8-second client deadline and a 10-second server deadline; already-dispatched application actions cannot be undone.

Attach chooses the process main window, otherwise its largest visible titled window, and pins its process start time to guard against PID reuse. Same-process top-level windows are inspected together; controls in different browser renderer processes require attaching the relevant process and are not automatically traversed across process boundaries. Elevated targets generally require a matching integrity level. Testy does not elevate itself.

## Fixture contract

TestLab provides `CustomerName`, `CustomerEmail`, `AddCustomer`, `StatusMessage`, `CustomerList`, `SearchBox`, `ResultCount`, `ResetButton`, `DelayedButton`, and `DefectToggle` IDs. Reset clears all state and reports `Ready for a new customer.`. Missing name reports `Enter a customer name.`; invalid email reports `Enter a valid email address.`; success reports `Customer added: {name}`. The directory filter changes `ResultCount` to `0 customers`, `1 customer`, or `{N} customers`. Background check disables its button, waits 700 ms, then reports `Background check complete.`. The defect toggle intentionally reports `Customer added: [incorrect name]` to demonstrate an assertion failure.

`OpenReview` opens a real modal owned window (`ReviewDialog`) with `ReviewSummary` and `CloseReview`. The summary is `{N} customer records in this session.`. Its capture includes the modal above the disabled owner in their actual screen positions. Input still targets the active enabled modal; composing the disabled owner's pixels does not authorize actions on it.

## Native computer-use dispatcher

`new Testy.Windows.NativeComputerActionExecutor(driver)` implements `IComputerActionExecutor`. Its `ExecuteAsync(JsonElement, CancellationToken)` accepts the native OpenAI computer action types `click`, `double_click`, `move`, `scroll`, `drag`, `type`, `keypress`, `screenshot`, and `wait`. A caller must first obtain a screenshot with the same driver's `CaptureAsync`; the dispatcher pins the image's HWND membership, full rectangles, stacking order and active input window. Moved/resized/opened/closed/reordered surfaces require a fresh screenshot. The agent protocol runner captures new evidence after each action, including screenshot/wait actions.

Native typing uses Unicode SendInput packets without touching the clipboard and rejects a focused password control. Clicks support left, right, middle and wheel-as-middle; unsupported buttons and action types throw. Key chords reject OS app-switch/start/task-manager combinations. Scroll amounts are approximately translated into wheel notches (100 protocol pixels per notch), because ordinary Windows wheel input cannot specify exact pixels. Drag paths are bounded and checked along interpolated points; cancellation releases an already-pressed mouse button. Native actions have a 20-second cooperative deadline. Focus races with a physically interacting user remain possible.

UIA snapshots include process-lifetime `RuntimeId` identities. Native mouse receipts inspect the actual accessibility element at the pointer immediately before input, retain passive descendant identities, and stop at the nearest independently actionable control. Keyboard receipts record the actual focused control. When supported by the target, the driver listens for UIA synchronized-input delivery events before sending input and records `InputDelivered` only after `InputReachedTarget`. Other-element/discarded events, missing patterns, or a one-second acknowledgement timeout leave delivery unverified. Subscriptions are cancelled and removed after each action. Receipt preparation and cleanup have bounded waits; an already-running COM provider call may still finish later.

Saved semantic native click/type/key/toggle/select coverage requires a fresh acknowledged receipt matching the expected control identity, plus the saved outcome assertions. Raw coordinate actions can still execute when a provider lacks synchronized-input support, but missing delivery evidence cannot prove a saved semantic control action. Native semantic mapping currently requires **Windows UI Automation attachment**; the opt-in WPF visual-tree driver does not expose matching UIA runtime identities, so use its local selector tools. A receipt identifies the target of a delivered input event; it does not by itself prove the application's handler or business operation succeeded. Exact result assertions remain necessary.

Windows can deny activation to background processes. The driver tries normal activation and a bounded UIA focus request for the selected window, then requires that exact HWND to be foreground. An occluded point or denied focus stops input with an explicit error; it does not simulate global keys or bypass ownership checks. Bring the target forward before retrying. Do not run two desktop-control sessions concurrently or let background inspectors change attachments during a run.

## Repeat the physical checks

Build `tests/Testy.WindowsChecks/Testy.WindowsChecks.csproj` and TestLab, then run:

```powershell
dotnet run --project tests/Testy.WindowsChecks
# Or pass a built fixture and an evidence folder:
dotnet run --project tests/Testy.WindowsChecks -- C:\path\Testy.TestLab.exe C:\path\checks
```

This harness owns and closes its TestLab process. Run on an interactive desktop without simultaneous user/agent input. Nineteen checks cover both drivers: actual customer creation using key/coordinate/native Unicode input, text replacement with a native chord, modal inspection/capture/close, ambiguous selector rejection, out-of-bounds rejection, truncated-tree safeguards, cancellation, and unsupported/global-chord rejection. `move`, `double_click`, `scroll`, and `drag` are verified to dispatch inside the target without error; these checks do **not** assert semantic scrolling or a drag-and-drop result. PNG evidence and `supplemental-results.json` are saved under `verification/supplemental` by default.

The current harness additionally requires synchronized delivery receipts and matching runtime identities for UIA native clicks, typing and keypresses. **Historical baseline:** when the interactive desktop became available during an earlier release check, all 19 physical checks passed (`verification/supplemental-restored/supplemental-results.json`). That baseline's queued native-protocol integration passed all five saved steps and its exact assertion across eight model turns (`verification/native-protocol-restored-recheck/native-protocol-report.json`). Its model responses were mocked; this was protocol dispatch, feedback, input delivery and saved-workflow coverage, not a live OpenAI API request. The first queued attempt safely rejected a pointer-position change before clicking and remains in `verification/native-protocol-restored`.

Those historical passes do not override newer results. The [latest focused Windows qualification](../verification/windows-priorities/RESULTS.md) reran all 19 checks successfully after composite capture changes, and separately records the unqualified native semantic popup-selection case. The [broader functionality report](../verification/FUNCTIONALITY-RESULTS.md) preserves prior release results and environmental focus/input blocks. Each harness run replaces the status file at startup and records failure details, so a failed rerun cannot leave an old success report authoritative.

Capture-quality regressions can run without any interactive desktop:

```powershell
dotnet run --project tests/Testy.WindowsChecks -- --capture-quality
```

This loads two preserved real PNG fixtures without launching a window: the misleading painted-frame/black-client capture must be rejected, and the earlier readable live-Codex capture must be accepted. Results are saved to `verification/capture-quality.json`. The fixtures preserve the observed rendering regression rather than synthesizing its appearance.
