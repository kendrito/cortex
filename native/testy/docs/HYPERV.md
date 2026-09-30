# Testing inside Hyper-V VMs

Testy can run a saved test on the signed-in desktop of a Hyper-V VM on this PC and bring the evidence back. The AI computer-use loop runs inside the VM: the model sees that VM's screenshots and the runner performs each action there. The [background agent](AGENT.md) does this work because Hyper-V requires administrator rights.

## One-time setup per VM

The VM needs:
- Windows 10/11 or Windows Server 2016+ with the Desktop Experience (not Server Core), and Hyper-V integration services (the defaults).
- An administrator account whose password you give Testy once. **Automate → Virtual machines → Set up for Testy…** saves it in Windows Credential Manager for your Windows user; it is never written to a file, an argument or a log.
- That same account signed in with the desktop unlocked. Use VM Connect in basic session. Closing an enhanced (RDP) session disconnects the console, and the VM then reports "disconnected".
- Internet access for AI runs (the model is called from inside the VM). Replay runs do not need it.
- The program you want to test, either installed in the VM or copied in per run from a folder on this PC.

You install nothing in the VM yourself. Setup verifies the sign-in over PowerShell Direct and copies Testy's worker into `C:\ProgramData\TestyAgent\Worker\<manifest hash>\`. It re-copies automatically when the bundle changes.

## What happens during a run

1. **Preflight on this PC:**
   - The saved test and request are validated.
   - A VM run needs an OpenAI or OpenAI-compatible endpoint that is not loopback. The Codex bridge and `localhost` models don't exist inside the VM.
   - The provider key is resolved from Credential Manager or your environment.
2. **Readiness over PowerShell Direct:**
   - The guest's `HKLM\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters\VirtualMachineId` must equal the VM ID.
   - The console session must be active and unlocked for the stored account, with Explorer running.
   - No other Testy run may be active in the VM.
   - Anything else leaves the job queued.
3. **Worker:** if the guest copy is missing or stale, one zip of exactly the sealed files is copied in, expanded and verified file by file against the manifest.
4. **Stage:**
   - Testy creates `C:\ProgramData\TestyAgent\Runs\<id>\`, readable only by SYSTEM, Administrators and the stored account.
   - It writes the frozen test, settings and arguments, and optionally unpacks your app folder.
   - For AI runs, the key is saved there as a DPAPI-protected file only that guest account can decrypt.
   - It registers a one-shot interactive task `\Testy\Run-<id>` (the signed-in account, limited run level) and starts it.
5. **Run:** `guest-run.ps1` deletes the key file as it moves the key into its own process environment, then runs `Testy.Cli.exe worker-run` against the program. It writes `exit.json` and clears the key. The worker performs its usual full acceptance checks inside the VM.
6. **Collect:** the run folder is zipped with a per-file SHA-256 inventory, copied back, and verified on this PC. Evidence lands in the job's attempt folder under `vm-…\guest\`.
7. **Cleanup:** the task is removed, the key file is confirmed absent, and no run process may remain. The guest run folder is deleted after a verified copy, or kept when evidence could not be collected.

## Host acceptance

A VM run passes only when all of these hold:
- the guest worker passed;
- every returned file matches the guest inventory;
- the worker ran exactly the requested test, mode, driver, model and executable, on this bundle's build;
- its stdout matches its persisted `worker-result.json`;
- the provider key and guest password are absent from all returned text;
- the key was imported and cleared in the guest;
- guest cleanup was confirmed.

The job then records **Passed**; any other outcome records Failed, Cancelled, TimedOut or Interrupted with the stage that stopped it. Host clocks are used for job times. Nothing is retried.

## Command line

Run these elevated:

```powershell
.\Testy.Cli.exe vms --operation list [--deep]
.\Testy.Cli.exe vms --operation set-credential --vm VM_GUID --username Administrator   # password: one UTF-8 line on stdin
.\Testy.Cli.exe vms --operation setup --vm VM_GUID
.\Testy.Cli.exe vms --operation check --vm VM_GUID
.\Testy.Cli.exe vm-run --vm VM_GUID --exe "{worker}\Testy.TestLab.exe" --test .\examples\create-customer.json --replay --artifacts .\artifacts
.\Testy.Cli.exe operations --operation enqueue-test --test T.json --settings S.json --exe "C:\Apps\App.exe" --target vm:VM_GUID
.\Testy.Cli.exe vms --operation forget --vm VM_GUID
```

The program path can take three forms:
- `{worker}\...` refers to the staged Testy folder, which includes the sample apps.
- An absolute path refers to a program already installed in the VM.
- With `--stage-directory DIR`, the path is relative to that folder copied in for the run (at most 20,000 files and 2 GiB).

## Limits

- **Hosts:** only VMs on this PC are reached; PowerShell Direct has no remote hosts.
- **Account:** the guest account must be an administrator for PowerShell Direct. The run task uses the signed-in account at its limited level, except for the built-in Administrator, which always has a full token.
- **Leaked environment:** the program under test inherits the worker's environment in the VM, including the per-run key variable, just as local runs inherit the configured key variable.
- **Lifecycle profiles:** project-aware lifecycle profiles (AI preparation plus build commands) still run only on this PC.
- **Evidence paths:** paths inside the copied evidence are guest paths. The HTML reports use relative links, so they open from the copy.
