# AHK Manager command line

Use `App/backend/agent/ahk-manager.exe` inside the extracted portable package. The program identifies the installation by that path.

```powershell
$manager = 'C:\Apps\AHK Manager Portable\App\backend\agent\ahk-manager.exe'
& $manager commands
& $manager version
& $manager app.start
& $manager app.status
& $manager scripts.list
& $manager app.quit
```

`commands` returns the shared command catalog. Use `version` for the installed CLI version and `app.status` for the running host. The catalog, help, and CLI version work with Manager closed and from an ordinary shell.

Live commands require an existing elevated administrator token in the same Windows user and session as Manager. An unattended process must already have that authority. A regular shell receives `ElevationRequired`. The CLI does not open a UAC prompt.

`app.start` waits for the authenticated host and backend to become ready. If Manager is closed, it starts the root launcher without opening a window or running login startup. Other commands require a running host unless you explicitly pass `--start`. Use `app.show`, `app.hide`, and `app.preferences` for notification area actions. `app.quit` exits Manager while preserving independent scripts. `app.stopScriptsAndQuit` requests verified script shutdown before exit and may report partial success while Manager stays open.

## Input and output

Each command takes an optional JSON object. Pass it as one argument after `--json`, or pipe UTF-8 JSON to `--stdin`. Omitted arguments are an empty object. Use the catalog for fields and commands that require a preview or confirmation. Keep target identifiers and confirmation values from the same operation and host generation.

```powershell
@{ paths = @('C:\Scripts\Example.ahk') } |
  ConvertTo-Json -Compress |
  & $manager scripts.addFiles --stdin
```

Each invocation writes one JSON response with the protocol, request ID, version, host generation, outcome, code, and result. The generation is empty for offline responses. Use the process exit code and response together.

| Exit | Meaning                                         |
| ---: | ----------------------------------------------- |
|    0 | Confirmed                                       |
|    2 | Invalid command or input                        |
|    3 | Elevation required or peer refused              |
|    4 | Host unavailable, busy, or installation invalid |
|    5 | Accepted and still pending                      |
|    6 | Partial                                         |
|    7 | Failed                                          |
|    8 | Unknown                                         |

A confirmed quit means the exact authenticated Manager process exited with code zero. For accepted updates, rollback, restart, or relocation, use the receipt and the command catalog’s read-only status and recovery commands to check the result. A timeout, disconnect, or lost reply can leave a result unknown. Inspect the current state before issuing another mutation.

## Developing the CLI

The native catalog is generated from `Presentation.Electron/src/shared/agent-commands.json`; it is shared with the application rather than maintained as a second command list. Keep request/receipt validation and the parent/peer ownership proofs independent.

From the repository root, run the focused transport tests before changing the CLI:

```powershell
dotnet restore Tests.AgentCli/Tests.AgentCli.csproj --locked-mode --configfile NuGet.Config
dotnet run --project Tests.AgentCli/Tests.AgentCli.csproj -c Release --no-restore
```

Run the Node command-dispatch tests from `Presentation.Electron` with `npm.cmd test -- --out <fresh-absolute-directory> --test-name-pattern "agent|command"`. The architecture and default .NET formatting checks include both CLI projects. The packaged Windows startup fixture verifies the published executable, authenticated pipe and headless lifecycle separately.
