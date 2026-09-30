# Local operations and workspace recovery

Testy's queue is a durable local store for one Windows user. Studio's **Automate** page, the `operations` CLI and the optional [background agent](AGENT.md) use the same store. A signed-in, unlocked interactive desktop is still required for UI execution, on this PC or inside a [Hyper-V VM](HYPERV.md). There is no Windows service, remote worker fleet, tenant isolation or enterprise RBAC.

Each job is either a lifecycle profile (AI preparation, then the UI run; this PC only) or a saved test run (`enqueue-test`: a frozen test, provider settings or `--replay`, and a program path). A job's target is `local` or `vm:<Hyper-V VM GUID>`. A pump only claims jobs for its own target (`--target`), after its target is ready: the desktop lease and an unlocked input desktop locally, or the VM lease and a PowerShell Direct readiness check for a VM. An unready target leaves its jobs queued. A store holding saved-test or VM jobs is written as format 2, so older builds stop with an explicit version message.

## Queue and schedules

A lifecycle profile identifies a test, provider/project configuration, executable and literal launch arguments. Enqueue loads and validates those files, then stores a cloned typed request and its SHA-256 in `operations/queue.json`. Changes to the original files do not change an already queued job. Executable and repository contents remain external inputs and are checked by the lifecycle/worker at execution time; a frozen request is not a frozen copy of a repository or installed application.

Examples below use PowerShell, an installed `Testy.Cli.exe`, and explicit workspace/profile paths:

```powershell
& .\Testy.Cli.exe operations --operation enqueue --workspace C:\Tests\Workspace --profile C:\Tests\profile.json
& .\Testy.Cli.exe operations --operation list --workspace C:\Tests\Workspace
& .\Testy.Cli.exe operations --operation pump --workspace C:\Tests\Workspace --once
& .\Testy.Cli.exe operations --operation pump --workspace C:\Tests\Workspace --seconds 3600
& .\Testy.Cli.exe operations --operation cancel --workspace C:\Tests\Workspace --id JOB_ID
& .\Testy.Cli.exe operations --operation rerun --workspace C:\Tests\Workspace --id TERMINAL_JOB_ID
```

`pump --once` attempts one eligible job, with a default outer deadline of 15,000 seconds so the maximum preparation and UI deadlines fit. Without `--once`, the default pump lifetime is 60 seconds. `--seconds` explicitly overrides either default and must be 1–86,400. Lifecycle phase deadlines still apply. Stopping a pump requests cancellation of active work and waits for its cleanup; pending jobs remain in the queue. A busy desktop returns without claiming the job. An idle queue returns without launching a target.

```powershell
& .\Testy.Cli.exe operations --operation schedule-add --workspace C:\Tests\Workspace --profile C:\Tests\profile.json --interval-seconds 3600
& .\Testy.Cli.exe operations --operation schedules --workspace C:\Tests\Workspace
& .\Testy.Cli.exe operations --operation schedule-pause --workspace C:\Tests\Workspace --id SCHEDULE_ID
& .\Testy.Cli.exe operations --operation schedule-resume --workspace C:\Tests\Workspace --id SCHEDULE_ID
```

A schedule runs while a pump or the background agent is active. Its interval is 60 seconds–30 days, measured from successful completion. Missed intervals coalesce into one due occurrence. A schedule cannot have overlapping occurrences. Failed, cancelled or interrupted occurrences pause it for explicit review. Resume does not erase the previous evidence or resume an interrupted step.

The store allows 100 pending jobs, 1,000 total job records and 50 schedules, within a 64 MiB state-file limit. `delete-job` removes a terminal record while retaining its evidence directory; `schedule-delete` removes a schedule. Export or archive evidence before independently deleting old artifact directories. Never delete lock files to bypass ownership.

## Claims, interruption and uncertain outcomes

Queue writes use an exclusive cross-process lock and flushed atomic file replacement. A claim records the owner's process ID, process start identity and a random claim token before execution. Terminal updates must match that claim. Each attempt receives its own directory and frozen request.

Workers share a same-user lease for the current Windows session, window station and desktop. A borrowed lease permits only one executing worker. The owning process records target/runner process identities before authorizing input. A surviving registered process or an identity that cannot be checked prevents reuse; Testy does not steal a live lease or terminate unrelated processes. Windows jobs handle cleanup of owned target/runner descendants. This coordination is a local correctness mechanism, not a security sandbox against a malicious process running as the same user.

`reconcile` recognizes a dead claim owner and marks the attempt **Interrupted** with an uncertain outcome. It never replays an input, command or partially completed test. An exception without a verified terminal cleanup result is also uncertain. The executor retains desktop ownership until it finishes cleanup; cancellation is not a guarantee that an already dispatched side effect did not happen.

Review evidence and reset any external state before `rerun`. A rerun creates a new job ID and a new attempt linked to the old job. Queue cancellation before dispatch sends no application input. Cancellation during execution remains subject to the action and cleanup evidence. See [Lifecycle](LIFECYCLE.md) and [Failure diagnostics](FAILURE-DIAGNOSTICS.md).

## Backup, format validation and restore

```powershell
& .\Testy.Cli.exe operations --operation migrate --workspace C:\Tests\Workspace
& .\Testy.Cli.exe operations --operation backup --workspace C:\Tests\Workspace --archive C:\Backups\workspace.zip
& .\Testy.Cli.exe operations --operation restore --workspace C:\Tests\Workspace --archive C:\Backups\workspace.zip --destination C:\Tests\RestoredWorkspace
```

Commit edits and stop active jobs before backup. The archive must be new and outside the workspace. Backup holds the queue lock to prevent new claims, rejects running jobs, holds source files against concurrent writes/deletes, and verifies the inventory before committing the ZIP. Temporary writes, inaccessible files, reparse points, unsupported paths, or a changed inventory cause failure. Runtime lock files are excluded; ordinary user files ending in `.lock` remain included.

The archive contains a manifest with each file's relative path, length and SHA-256. Limits are 20,000 files, 2 GiB in total and 256 MiB per file. Restore rejects duplicate/path-alias entries, traversal, reserved device names, symlink entries, unlisted files, invalid hashes and unsupported future formats. It validates an owned staging directory before moving it into a new or empty destination. It never overwrites a populated workspace.

Workspace format 1 is explicit in `workspace-format.json`. A legacy workspace can be validated and given this marker without rewriting historical test/run evidence. Future versions are rejected rather than silently downgraded. Restored running jobs become interrupted, queued jobs become cancelled and schedules are disabled. Explicit review and fresh reruns are required.

Historical report, JSON, screenshot and sidecar bytes remain unchanged. Restore records original workspace roots; Studio uses the confined root mapping when displaying restored evidence. Saved lifecycle profiles also resolve their copied test, provider-settings and launch-argument files through that mapping when loaded, without rewriting the saved profile. Executable paths, separate project configuration paths and repository paths inside provider settings remain explicit and are never silently rebound. External project files, artifacts outside the workspace and Windows credential-vault secrets are not silently bundled. Old external paths may remain unavailable on another machine. See [Studio workflows](PRODUCT-WORKFLOWS.md) and [Installation](INSTALLATION.md).
