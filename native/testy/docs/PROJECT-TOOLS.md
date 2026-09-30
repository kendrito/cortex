# Project-aware AI tools

Project tools give Testy's AI an explicitly selected local source tree and an optional, host-configured command catalog. File observations and command results are evidence for planning and diagnosis. They do not satisfy a saved UI assertion or turn an unexecuted draft into a passing test.

This feature is disabled by default. The shipped [disabled configuration](../examples/project-tools.disabled.json) is valid and grants no project access. No API key belongs in this file. Keep model credentials in the provider's configured environment variable.

## Select a project

Copy the disabled example into a separate configuration file. For read-only source access, set `enabled` to `true` and `rootDirectory` to the absolute path of one existing local project. Leave `commands` and `requiredBeforeUiCommands` empty. Do not select a whole drive or a user-profile directory. Network, device and reparse-point roots are rejected.

An example with two explicitly authorized commands is below. Replace **both absolute paths** with paths that exist on your machine before applying it. The executables and arguments are chosen by the human configuring the project, never by the model.

```json
{
  "enabled": true,
  "rootDirectory": "C:\\src\\MyApp",
  "commands": [
    {
      "id": "build",
      "description": "Build the existing solution without restoring dependencies.",
      "executable": "C:\\Program Files\\dotnet\\dotnet.exe",
      "arguments": ["build", "MyApp.sln", "--no-restore"],
      "workingDirectory": ".",
      "timeoutSeconds": 120
    },
    {
      "id": "unit-tests",
      "description": "Run the project's existing tests without building or restoring.",
      "executable": "C:\\Program Files\\dotnet\\dotnet.exe",
      "arguments": ["test", "MyApp.sln", "--no-build", "--no-restore"],
      "workingDirectory": ".",
      "timeoutSeconds": 120
    }
  ],
  "requiredBeforeUiCommands": ["build", "unit-tests"],
  "maximumCalls": 20,
  "maximumCommandInvocations": 3,
  "maximumPlanningTurns": 12,
  "maximumOutputCharacters": 16000,
  "maximumFiles": 2000,
  "maximumFileBytes": 512000
}
```

These example commands will fail if the solution, SDK, restored assets or test dependencies are unavailable. Testy does not install dependencies or invent a replacement command. Command arguments are literal Windows arguments; shell operators are not interpreted unless the human explicitly configures a shell executable and script in the catalog.

## Use the existing Studio interface

Close Studio, then use the bundled CLI to select the project for its workspace:

```powershell
& .\Testy.Cli.exe project-config --project C:\config\my-project.json `
  --workspace "$env:LOCALAPPDATA\Testy\Workspace"
& .\Testy.Studio.exe
```

For a custom workspace, supply its directory to `project-config` and launch Studio with `--workspace` pointing to the same directory. The selection is stored in that workspace's `project-tools.json`. Studio now also exposes the project directory and command catalog under **Automate → Source code access**. Saving there and leaving Automate reloads the selection without a restart. External CLI edits require reopening Automate or restarting Studio before use. **Create test**, **Improve…** and AI-guided **Run** use the selection; saving provider connection settings preserves it. Disable project access in the same form to turn it off.

Provider settings can also contain a `projectTools` object with the same schema. A workspace's separately selected `project-tools.json` takes precedence over that embedded selection.

## CLI authoring and execution

Supported AI commands accept `--project C:\config\my-project.json`, which overrides the project selection embedded in their provider settings for that invocation:

```powershell
& .\Testy.Cli.exe draft-test --pid 1234 --settings C:\config\provider.json `
  --project C:\config\my-project.json --instructions C:\tests\intent.txt `
  --acceptance C:\tests\assertions.json --output C:\tests\new-draft.json `
  --artifacts C:\results\authoring

& .\Testy.Cli.exe worker-run --exe C:\src\MyApp\MyApp.exe `
  --test C:\tests\saved-test.json --settings C:\config\provider.json `
  --project C:\config\my-project.json --timeout-seconds 300 `
  --artifacts C:\results\execution
```

`run-ai`, AI `run-suite`, and `draft-from-demo` also support `--project`. Explicit replay does not run project tools. Use a model turn budget large enough for the saved steps, required commands, source observations and terminal decision. The ordinary model and worker deadlines still apply; catalog timeouts do not extend the worker's deadline.

`draft-test` saves an unexecuted draft and provenance. When supplied, the independently authored acceptance file must contain assertions only, and its assertions must be preserved exactly. Review the draft and run it on a fresh test instance to establish acceptance.

**Commands are not a prelaunch build system.** `worker-run` validates configuration before starting its owned target, but the model dispatches project commands after the target has launched and attached. Commands that rebuild or replace that running target can fail because of file locks or invalidate its state. Arrange a required prelaunch build outside this worker, or configure commands that are appropriate while the target is running. Authoring begins with an attached-target observation; commands may change the project after that observation, so a draft still needs fresh execution verification.

For an explicit one-tool diagnostic without a model, save a request such as:

```json
{"tool":"project_read_file","arguments":{"path":"src/Feature.cs","startLine":1,"maxLines":80}}
```

Then run:

```powershell
& .\Testy.Cli.exe project-tool --project C:\config\my-project.json `
  --request C:\config\read-request.json --artifacts C:\results\project-check
```

Each explicit `project-tool` invocation is a new session. Its one-shot command and prerequisite state does not carry into a later AI run.

## Tools and bounds

| Tool | Arguments | Result |
| --- | --- | --- |
| `project_list_files` | `directory` (`.` for the root) | Bounded recursive file metadata. It does not read or hash every listed file. |
| `project_read_file` | `path`, `startLine`, `maxLines` | UTF-8 excerpt with line numbers, actual file identity and SHA-256 of the complete observed bytes. |
| `project_search_text` | `query`, `directory` | Literal, case-insensitive search of redacted UTF-8 content, with line and full-file hash citations. |
| `project_run_command` | `commandId` | One exact configured command invocation and its terminal process/cleanup result. |

Paths are relative to the selected project. Parent traversal, alternate data streams, reserved device names, ambiguous Windows path components, absolute paths, symbolic links, junctions and hard-linked files are refused. Directory handles prevent ancestor replacement during observations and command launch; final handle paths and file identities are checked. Paths compare using Windows case-insensitive semantics.

Defaults permit 20 tool calls, three command invocations, 2,000 visited entries, 512,000 bytes per file and 16,000 retained output characters. Hosts may configure bounded values: at most 200 calls, 20 commands, 10,000 visited entries, 2 MiB per file and 65,536 output characters. Reads accept at most 200 lines per request; searches accept at most 256 query characters and return at most 100 matches. Recursive traversal is limited to depth 32, with an eight-second observation budget checked between filesystem operations. It cannot forcibly interrupt a Windows filesystem call that itself stalls.

`Truncated`, omitted-entry counts, redaction flags and unavailable statuses remain explicit. A negative search or listing is not proof that something is absent from the entire repository. Files may change between distinct observations; hashes identify the observed version, not an atomic whole-project snapshot.

## Commands, failure and prerequisites

Only exact catalog IDs can run. The model cannot supply an executable, alter arguments, widen the working directory or introduce another command. Each command ID may dispatch only once per session. A failed, timed-out, cancelled or uncertain command blocks subsequent commands and UI acceptance; read-only project diagnostics remain available within the remaining call budget. There is no automatic retry after an uncertain outcome.

`requiredBeforeUiCommands` contains unique catalog IDs. The engine does not execute them automatically. It requires every listed command to have completed with exit code zero and verified cleanup before UI steps or a submitted draft can be accepted. An omitted prerequisite cannot produce a passing workflow. Command output is distinct from saved UI-step evidence, and execution obtains fresh UI context after a command before acting again.

Commands use an owned Windows job, atomic job association, a restricted inherited-handle list, closed stdin and bounded stdout/stderr drains. Cancellation, timeout and normal exit all stop and verify owned descendants. Each stream retains at most 65,536 characters; overflowing streams are withheld rather than returning potentially incomplete secret fragments. A session may impose a smaller output limit.

This is process isolation and lifecycle control, **not a security sandbox**. A configured command executes as the current Windows user and can access that user's files, network and registry, or invoke build targets that execute code. Configure only commands and projects you trust. Testy restricts the model's command selection, not everything trusted project code can do.

## Sensitive files and evidence

Default exclusions include `.env*`, `.git`, `.ssh`, cloud/credential directories, private-key/certificate containers, credential files, generated `bin`/`obj`, `node_modules`, and this session's evidence directory. The command environment forwards an explicit runtime/build-path allowlist and strips arbitrary inherited variables, including provider/cloud credentials. This does not remove credentials from the current user's registry or filesystem, which an authorized command could independently read.

Known provider-key values and recognizable credential assignments/tokens/private-key blocks are redacted before model output and persistence. Values supplied through recognized catalog credential switches or assignments (such as `--token VALUE` or `--password=VALUE`) also enter the exact-value redaction set, so a command echoing the bare value cannot bypass its label-based redaction. Redaction preserves source line breaks. It is a defense in depth, not a guarantee that every possible secret format can be recognized; keep sensitive material outside the selected tree. File hashes refer to original observed bytes, while excerpts and command output are redacted. A very large or binary file is unavailable rather than silently represented by a partial hash.

Each tool call persists a structured JSON record and a `.sha256` sidecar. The record contains request, status, times, citations, truncation/redaction state, and command result when applicable. Run and worker validation check those records against their durable copies and distinguish project observations from UI assertions. Records returned to callers are copies; changing a returned object cannot rewrite session history.

For current executed checks and remaining limitations, see the release verification report. Mock provider protocol checks establish request/feedback behavior; they are not live-model or TestComplete benchmark results.
