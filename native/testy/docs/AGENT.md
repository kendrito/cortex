# Background agent

`Testy.Agent.exe` keeps Testy running in the background. It starts at Windows sign-in with your highest privileges and shows a tray icon. With Studio closed, it runs queued jobs and schedules on this PC and in [Hyper-V VMs](HYPERV.md).

It is a startup app in your own signed-in session, not a Windows service. A Session 0 service cannot see or click a desktop, so it could not run UI tests. Running elevated gives the agent Hyper-V and PowerShell Direct access.

## Enable, update, disable

- **Enable at sign-in:** in Studio, open **Automate → Background runs** and choose **Enable at sign-in…**. From a terminal in a published bundle, run:

  ```powershell
  .\Testy.Agent.exe install --workspace "$env:LOCALAPPDATA\Testy\Workspace"
  ```

  Windows asks for administrator approval once. The agent then:
  - copies the sealed bundle to `C:\Program Files\Testy\Agent\<version>-<manifest hash>\` and verifies every file against `release-manifest.json`;
  - registers the scheduled task `\Testy\Testy Agent <your SID>` (logon trigger for you, interactive token, highest available run level, 30-second delay, no time limit, normal priority, restart on failure, runs on battery);
  - starts it.
- **Update:** run **Enable at sign-in…** again from the newer Studio or bundle. It stops the old agent, installs the new version beside it and keeps one previous version.
- **Disable:** choose **Disable at sign-in…** or run `Testy.Agent.exe uninstall`. This stops the agent, deletes the task and removes unused program folders. Workspaces, results and stored sign-ins stay.
- **Status:** `Testy.Agent.exe status` prints the heartbeat, and exits 0 when the agent is running.
- **Where results go:** the last install or uninstall result is written to `%LOCALAPPDATA%\Testy\Agent\install-result.json`.

The per-user Studio installer (`install-testy.ps1`) is separate. It never runs elevated and does not touch the agent.

## What it does

- **The loop:** every 5 seconds, and immediately when `queue.json` changes or you unlock Windows, the agent reads the queue in one locked snapshot.
- **Launching work:** it starts a short-lived `Testy.Cli.exe operations --operation pump --once --target <target>` for each target that has work and is ready. At most one pump runs per target, and at most 4 VM runs at once (configurable).
- **It never claims a job itself.** Each pump owns its claim, so the existing crash reconciliation applies unchanged. If the agent exits, running pumps finish their jobs.
- **Checks before a claim:** every pump re-checks readiness before claiming:
  - **This PC:** the desktop lease plus an unlocked, connected input desktop.
  - **A VM:** the VM lease plus a PowerShell Direct readiness check.
  - A target that isn't ready leaves its jobs queued. Before this release, a locked desktop turned a claimed job into Failed and paused its schedule.
- **Reconciling:** it calls reconcile when a schedule is due or a pump died. Nothing is ever retried.
- **Machines:** it refreshes the Hyper-V inventory every 60 seconds and checks guest readiness for set-up VMs every 5 minutes.
- **Notifications:** passed and failed jobs show a tray notification.
- **Signing out:** it cancels running pumps cooperatively.

## Tests on this PC take over the mouse and keyboard

Local jobs launch the app and click and type on your desktop. By default the agent starts them only after you have been idle for 2 minutes.

- **Tests on this PC** (Automate → Background runs) switches between *when I'm idle*, *any time* and *never (VMs only)*.
- **Run queued jobs now**, in the tray or Studio, skips the idle wait once.
- VM jobs never touch this desktop.
- Studio's own **Run**, test sets and recordings take the same desktop lease, so they never collide with an agent job.

## Files

Agent state lives outside the workspace, so heartbeats never disturb verified workspace backups. The folder is `%LOCALAPPDATA%\Testy\Agent\<16-hex workspace key>\`:

| File | Purpose |
| --- | --- |
| `status.json` | Heartbeat every 5 s: state, message, elevation, version, running pumps, waiting reasons, recent results |
| `settings.json` | Paused, local-run policy, idle minutes, VM run limit |
| `machines.json` | Hyper-V inventory and guest readiness |
| `control\<id>.request.json` / `.result.json` | Studio → agent requests: `process-now`, `refresh-machines`, `setup-vm`, `shutdown` |
| `pumps\` | Output of recent pumps |
| `logs\agent-YYYYMMDD.log` | Seven days of logs; never contains secrets |

Studio considers the agent running when the heartbeat is under 20 seconds old and its process is alive.

## Security trade-offs

- **Elevated execution:** the agent and its local jobs run elevated, so apps under test on this PC run as administrator. Anything running as your user can put a job in your queue, and the agent will run it elevated. That is acceptable for a dedicated test PC. For a shared or corporate host, use a dedicated test account.
- **Protected program files:** the elevated program files live under Program Files, which only administrators can write.
- **Queue safety:** requests are validated and frozen when enqueued, and pumps refuse malformed stores.
- **Unsupported elevation:** elevating with another administrator's credentials (a different user profile) is not supported. The task, vault and workspace all belong to the signed-in user.
