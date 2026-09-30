# Studio workflows (Automate)

Version 0.6 adds **Automate** to Studio's navigation pane. It is a page in the main window with seven tabs, in this order: **Background runs**, **Test sets & data**, **Virtual machines**, **Record**, **Build, then test**, **Source code access** and **Data & backup**. The page is built fresh from disk each time you open it. When you go to another page, settings, project access and new recorded drafts reload immediately. While Automate is running an operation, it stops it safely instead of letting you leave.

## Connect the model

Open **Settings**, choose the service, server URL and model, and keep **Let the AI guide each run** on. A corporate compatible endpoint uses the same local tools as other compatible providers. OpenAI Responses can select native computer use when the configured model supports it. The local engine checks action identity, assertions and evidence regardless of which model is selected.

Enter a key under **API key** and select **Save key**. The credential belongs to the current Windows user and exact configured endpoint and key-variable slot. Changing an endpoint requires a separately stored credential. A configured process/user environment variable remains a fallback. Workspaces and backup ZIPs contain connection settings, not the vault secret. Removing a stored key does not remove an environment variable.

**Check connection** sends one harmless structured planning request, without application input, screenshots or project content. It qualifies that narrow contract only. It does not certify native computer input, long workflow reliability, model quality, or production endpoint behavior.

## Project preparation and application launch

1. In **Source code access**, choose the project folder and let the AI read it.
2. Add exact command executables, working directories, literal argument lists and timeouts. Mark mandatory build/reset commands **Must pass first**. The model can choose these catalog entries; it cannot invent a shell command.
3. In **Build, then test**, select the test to run and the app to open (the output EXE path). This EXE may be absent until the build runs. Provide launch arguments one per line and turn on the WPF helper only for an instrumented WPF application.
4. Select **Save as new setup**, then **Run saved setup**. Setups (lifecycle profiles) snapshot the test, connection and project catalog into workspace input files. Direct runs and queue entries both use those saved inputs. Editing the form, library test or current connection does not alter an existing setup; save a new one to apply the changes.

Preparation must inspect source, successfully complete every required command, and explicitly complete its AI phase before launch. The UI phase receives source inspection tools with an empty command catalog so it cannot rebuild the loaded app or rerun preparation. Every saved interaction still needs recorded UI execution, and every saved assertion still needs a passing engine check. Build success cannot substitute for acceptance success.

Preparation and UI phases have separate bounded deadlines. UI decisions allow up to 240 turns with local tools, or 80 native computer decisions; the default remains 30. An interrupted side effect produces an uncertain outcome that needs review. **Check a run that was cut off…** reconciles the saved journal and never resumes actions. Use a fresh run after checking/resetting the target environment. See [Lifecycle](LIFECYCLE.md).

## Suites, data and step options

**Test sets & data** saves multiple library tests (a suite) with explicit repetitions. A test set runs sequentially against the connected app and stops on failure. Author reset steps when repetitions share state. There is no implicit reset or hidden rerun.

CSV data contains literal values, including quoted commas, line breaks and doubled quotes. Headers name parameters. Map a header to a whole TypeText, Select or AssertText value of the selected library test, then choose **Make a set from the rows**. Every CSV row must supply exactly those parameters. Actions, selectors, comparison modes and acceptance structure stay fixed. A matrix supports up to 200 rows; formulas are plain text. The resulting tests are ordinary editable saved definitions, with provenance files beside the suite.

Select a step on the **Tests** page and choose **More options…** in the step editor under the steps to edit its wait time, WPF row/column keys and explicitly approved selector alternatives. Grid edits need a later commit/cancel step. An alternative requires an exact expected control type and automation ID or accessible name. The AI can select a saved alternative only when the original selector is absent; it cannot silently change assertions. Native computer mode rejects alternatives rather than weakening its input checks.

## Record (demonstrations)

Connect an app first. **Record** records supported UIA Invoke and Value events in the initially selected window. Wait for the explicit **Recording is active** message before demonstrating. The recorder does not capture every gesture or all secondary windows. It preserves its scope and warnings with the evidence.

Select a library test containing your explicit acceptance assertions. The AI can turn the demonstration into a draft while retaining those assertions. Successful authoring adds an **unexecuted** draft to the library; review it and run it on clean test data. Authoring is never a passed test.

## Background runs and schedules

Select a saved setup on **Build, then test**, then add it to the waiting list (**Add setup to waiting list**) or schedule it (**Schedule setup**) on **Background runs**. The schedule interval is in seconds. The queue freezes the setup's test, settings and launch arguments. **Run the next waiting test** performs one dispatch. **Keep running waiting tests** pumps the durable queue for the chosen lifetime; Stop cancels cooperatively and waits for cleanup.

Schedules run while a worker is active in the same signed-in, unlocked Windows session. **Enable at sign-in…** installs the [background agent](AGENT.md), which keeps them running with Studio closed; its panel shows what it is doing and waiting for, and lets you pause it or choose when tests may run on this PC. It is not a Windows service. Successful completion sets the next interval; failed, cancelled or interrupted attempts pause their schedule. Missed ticks coalesce. A living worker retains ownership even when it takes longer than expected. After owner death, registered child processes must be confirmed stopped before another worker can acquire the shared desktop lease. No input or command is retried merely because a worker crashed.

Cancel, explicitly start a fresh attempt (**Run again**), pause and resume schedules (two separate buttons), and open results from this view. Removing a finished queue record retains its artifacts. This release is a local same-user scheduler. It does not provide a corporate server, remote worker fleet, SSO, tenant isolation, or team access control.

## Virtual machines (Hyper-V)

**Virtual machines** lists this PC's Hyper-V VMs through the background agent (Hyper-V needs administrator rights). **Set up for Testy…** stores an administrator sign-in for the VM in Windows Credential Manager, verifies it over PowerShell Direct and copies Testy's worker into the VM. Keep that account signed in and unlocked (VM Connect, basic session). Choose a saved test and the program path inside the VM, then **Queue run on selected VM** or **Schedule on selected VM**. The agent runs it when the VM is ready, the AI works inside the VM, and screenshots and results come back to the job's evidence. See [Hyper-V testing](HYPERV.md).

## Data & backup (workspace recovery)

**Data & backup** creates a hash-validated ZIP outside the active workspace. Commit edits and stop active jobs first. Backup rejects an active queue job or concurrent write, rather than accepting a mixed snapshot. It includes files inside the workspace only; external project sources and external evidence paths are not copied. The current backup limits are 20,000 files, 2 GiB total and 256 MiB per file.

Restore requires a new or empty directory. It validates entry paths, hashes, counts and byte limits before moving staged files into place. Copied queued work is suspended/cancelled for review; schedules do not restart automatically. Historical JSON and sidecar bytes stay unchanged. Studio resolves old screenshot/report paths through restore metadata for display. **Open another data folder…** starts a separate Studio instance; it does not rewrite the workspace you currently have open.

Copied lifecycle profiles resolve their saved test, provider-settings and launch-argument files inside the restored workspace, even if the original workspace no longer exists. The target executable, project configuration and repository locations remain explicit external paths; review those before running on another machine.

See [Installation and release verification](INSTALLATION.md) for the application bundle and update process. See [Windows surfaces and WPF editors](WINDOW-SURFACES-AND-EDITORS.md) for the supported control matrix.
