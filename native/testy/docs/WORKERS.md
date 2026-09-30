# Interactive Windows workers and measured results

`worker-run` launches a fresh target EXE and a child Testy CLI for one saved test. Choose an AI provider configuration or explicitly request replay:

```powershell
.\Testy.Cli.exe worker-run --exe .\Testy.TestLab.exe --test .\examples\create-customer.json --settings .\examples\provider-openrouter.json --artifacts .\workers --timeout-seconds 300
.\Testy.Cli.exe worker-run --exe .\Testy.TestLab.exe --test .\examples\create-customer.json --replay --probe --artifacts .\workers
```

The user session must be active, unlocked and attached to the Default desktop on WinSta0. Active RDP sessions can qualify; disconnected or locked desktops and Session0 services cannot. The [background agent](AGENT.md) runs in your session for this reason, and runs VM jobs through [Hyper-V](HYPERV.md) inside each guest's signed-in desktop. The worker reports its observed desktop preflight. It does not unlock the machine or bypass Windows foreground restrictions. Use one UI worker per interactive desktop; the local worker does not schedule or serialize separate concurrent invocations.

The test, provider configuration and executable are validated before launch. Provider files contain environment-variable names, never embedded keys. The worker freezes the requested test and configuration in a unique artifact directory. Arguments are supplied as a JSON string array using `--target-arguments FILE`, not as model-generated shell code. `--startup-timeout-seconds` defaults to20 (1–120), `--timeout-seconds` to300 (1–3600), and `--shutdown-grace-seconds` to3 (1–30).

The Windows job owns the fresh target, child runner and contained descendants. Target job membership is assigned at process creation. The child cannot attach and act until containment is established. Cancellation signals the child; after bounded grace, the worker terminates its owned job and checks that no job processes remain. It never retries an operation whose input outcome is uncertain. Existing personal applications are not worker targets. Tests still run with the Windows account's privileges; job containment is process lifecycle management, not an operating-system security sandbox.

`worker-result.json` records the terminal outcome, process identities, input uncertainty, cleanup status and elapsed phases. The nested run retains screenshots, snapshots, JSON, HTML and JUnit evidence. A pass requires finalized matching results, the frozen canonical workflow, exact evidence records, readable images and successful cleanup. AI runs additionally bind flat step records to the agent's actual tool observations. Report copies of images and recovery guard snapshots must remain inside the worker directory and match their source bytes before their paths are normalized for comparison. Timed-out or cancelled runs preserve available child artifacts without claiming completed coverage. Output is flushed before completion. Exit 0 means passed, 1 failed/cancelled/timed out, and 2 invalid configuration or unavailable desktop/target; consult the result's status for the distinction.

## Benchmark export

Every finalized worker writes `benchmark-entry.json`. Export a folder containing those entries:

```powershell
.\Testy.Cli.exe benchmark-export --source .\workers --output .\results.json
.\Testy.Cli.exe benchmark-export --source .\workers --output .\results.csv --format csv
```

The exporter cross-checks each entry against its adjacent terminal worker result. It records driver, mode, model, test/build hashes, startup/execution/cleanup/wall time, evidence and recovery counts. The workload hash normalizes only `UpdatedAt` so metadata timestamps cannot split an otherwise identical test; a separate hash retains the exact frozen JSON. Build fingerprints cover deterministically ordered top-level EXE, DLL, deps and runtime configuration files, bounded to 1,000 files and 1 GiB per directory. Nested plugins and dynamically selected external dependencies are outside that fingerprint. Exact EXE/CLI hashes remain recorded too.

Groups retain workload/build identity rather than pooling unrelated tests. Local AI tools, native hybrid AI and replay are distinct modes. CSV escapes fields and protects formula-like text; a metadata JSON accompanies it. Cost is not measured. These are observed Testy runs, not model throughput measurements. TestComplete remains `notMeasured` unless a separate real comparison is performed; the exporter never invents competitor timing or pass rates.

The application AI loop has no general repository inspection or shell command tool. A configured worker executable is launched by the worker host; this does not grant the model arbitrary command execution. See [authoring workflows](AUTHORING-WORKFLOWS.md), [recovery](RECOVERY.md), and the [current verification report](../verification/FUNCTIONALITY-RESULTS.md).
