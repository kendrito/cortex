# Window surfaces and standard WPF editors

This support matrix describes specific Windows and .NET WPF contracts. It does not qualify arbitrary third-party control libraries, vendor runtimes, remote desktops, or every application using the same framework. The AI chooses each saved action; observing a control never edits, realizes, selects, scrolls, or commits it.

## Captured surfaces and coordinates

The Windows driver composes visible, non-minimized, non-DWM-cloaked top-level windows belonging to the attached process, including titleless popup HWNDs. A surface must have a client area of at least 2×2 pixels; non-client-only helper HWNDs are excluded. No window-title heuristic determines capture eligibility. The image uses physical screen positions and target-window stacking order. Empty gaps have a neutral background; pixels from the desktop or other processes are never copied into the image.

`UiSnapshot.ScreenshotBounds` is the physical-screen union of those surfaces. An image point `(x,y)` maps to `(ScreenshotBounds.X+x, ScreenshotBounds.Y+y)`. At most 16 surfaces, 8192 pixels per side, and 24 million pixels are supported. A larger union fails explicitly. Each included surface is rendered with `PrintWindow` and checked for blank client content; a valid title bar alone cannot establish usable evidence. Custom renderers, transparent/layered effects and protected content are not generally guaranteed by `PrintWindow`.

Native actions pin the captured HWND identities, rectangles, stacking order and active target window. Opening, closing, moving, resizing or reordering a target surface invalidates the image for input. Coordinates in gaps are rejected. Immediately before pointer input, the hit HWND must belong to the selected process and match the topmost captured target surface at that screen point. Foreground, enabled-window, process-start identity, actual UIA hit identity and synchronized-input acknowledgement checks remain in effect. Foreign windows may occlude the application on the physical desktop even though its target-only image is readable; input is rejected when hit testing detects that occlusion.

Captures are a bounded sequential rendering of target windows, not an atomic compositor frame. Layout changes during rendering fail the capture, but rapidly changing application content can still differ between surfaces. The implementation does not capture foreign overlays, compose the entire desktop, unlock a desktop, bypass foreground restrictions, or promise input on an inactive session. Separate before/after assertions are still necessary.

Transient window-show animation surfaces can be unreadable. Capture retries only observation/rendering for up to 1.5 seconds before reporting a persistent failure. `IScreenshotEvidenceSource.LastScreenshot` records the successful image's exact path, PID, timestamp and union origin/size; evidence pairing verifies these against the accompanying tree. Failed or cancelled rendering never authorizes fresh coordinates, and an older concurrent render cannot replace a newer capture.

The opt-in probe also inspects visible same-process `HwndSource.RootVisual` popup roots on its dispatcher. A visual is visited once by object identity, and its actual HWND supplies the enabled/input guard. Foreign-dispatcher presentation sources are not assumed safe to inspect; such a snapshot is marked incomplete. UIA and probe tree shapes can differ even when their screenshots show the same surfaces.

The owned WPF ComboBox fixture exposes distinct logical-item and popup-visual automation peers at the same visible position. A real coordinate click selected the exact requested option, but its hit peer supplied no synchronized-input acknowledgement. Native semantic `Select` therefore remains unqualified for this case; equal names, IDs or rectangles do not bridge those peer identities. The raw `CoordinateClick` action and an independent exact outcome assertion are a separate supported contract. Local semantic `Select` and the opted-in grid combo editor use their explicit provider/editor contracts. WPF's public source shows [item wrappers routing events through data peers](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Automation/Peers/ItemsControlAutomationPeer.cs); that is a plausible explanation, not a verified causal trace of this particular acknowledgement failure.

## Standard DataGrid editor matrix

All grid workflow actions require the in-process opt-in WPF probe, `GridAutomation.Enable=true`, exact unique `IGridRowIdentity.TestyRowKey`, exact unique `GridAutomation.ColumnKey`, and an item implementing `IEditableObject`. UIA-only attachment explicitly rejects the transactional grid capabilities.

| Column/editor | `GridEditCell.text` | Supported scope |
| --- | --- | --- |
| `DataGridTextColumn` / `TextBox` | Exact text, up to 4096 characters | One enabled visible writable editor with a standard two-way Text binding |
| `DataGridCheckBoxColumn` / `CheckBox` | Exactly `true` or `false`; `null` only when three-state | Standard two-way IsChecked binding; no case folding or boolean guessing |
| `DataGridComboBoxColumn` / noneditable `ComboBox` | Exact unique current choice label | Standard two-way SelectedItem or SelectedValue binding; string items, or realized item containers with explicit accessible names/string content |
| `DataGridTemplateColumn` | Same value rules as its opted-in editor | Column explicitly sets `GridAutomation.EditorId`; exactly one matching bound `TextBox`, `CheckBox`, or noneditable `ComboBox` in the realized cell |
| Password, arbitrary custom, editable combo free text, unapproved/ambiguous template | Unsupported | No reflection, private setters, arbitrary model property writes, or simulated substitute operation |

Combo lookup inspects at most 2000 current choices. Unknown labels, duplicate labels, unobserved complex item labels and unsupported binding forms are rejected. Business-row lookup remains bounded to 20,000 current-view items. Sorting, column display reordering and virtualization do not change the key contract.

Example saved value: `{"rowKey":"ROW-1","columnKey":"Enabled","text":"true"}`. A template column must additionally call `GridAutomation.SetEditorId(column, "ApprovalEditor")` and give the actual supported editor that exact AutomationId.

Editing calls the real grid `BeginEdit` and changes the actual editor dependency property using `SetCurrentValue`, preserving its binding. This is semantic UI editing, not physical mouse/keyboard simulation. Checkbox state changes raise state-change events; the contract does not synthesize a `Click` event. Normal WPF bindings may update the pending model transaction immediately. `GridCommitRow` and `GridCancelRow` are separate AI-selected steps. Application persistence must be asserted independently; ending a UI edit is not proof of saving data. An unsupported editor discovered after `BeginEdit` can leave an inspectable pending transaction: the engine never silently commits, cancels or retries it.

## Reproducible qualification

- `Testy.WindowsChecks --composite-capture <report.json>` checks Z-order composition, negative monitor origins, neutral gaps, stale HWND/layout identity, and competing/cancelled final-file publication offline.
- `Testy.Cli verify-surfaces --exe <Testy.WpfLab.exe> --artifacts <directory>` uses actual owned main/auxiliary/titleless-popup windows. Normal button and keyboard cases require strict receipts; the popup case separately proves exact raw-coordinate outcome and rejection of unsupported semantic selection coverage. A blocked native-input case remains a failure, not a simulated pass.
- `Testy.Cli verify-grid-editors --exe <Testy.WpfLab.exe> --artifacts <directory>` exercises standard and opted-in template editors, commit/cancel, duplicate choices, wrong keys, unsupported templates, reordering and UIA capability rejection.

These dedicated fixtures do not establish vendor compatibility or comparative superiority over another test product.

Primary API references: [PrintWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow), [window relationships and Z-order](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindow), [DataGridComboBoxColumn](https://learn.microsoft.com/en-us/dotnet/api/system.windows.controls.datagridcomboboxcolumn), [WPF DataGrid styles and templates](https://learn.microsoft.com/en-us/dotnet/framework/wpf/controls/datagrid-styles-and-templates), and [SetCurrentValue](https://learn.microsoft.com/en-us/dotnet/api/system.windows.dependencyobject.setcurrentvalue).
