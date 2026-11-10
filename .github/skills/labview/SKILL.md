---
name: labview
description: >-
  Use when inspecting, generating, modifying, validating, running, testing or
  documenting LabVIEW VIs, controls, classes, libraries, projects or DQMH modules
  through a local CLI without registering or connecting to an MCP server.
---

# LabVIEW through a local CLI

Use [scripts/labview.ps1](./scripts/labview.ps1), a guarded launcher for the C#
tool router. No MCP client, connection or server is involved. Keep terminal
approval enabled. The executable and its backends still need organizational
approval; changing the interface does not waive security policy.

## Find the executable and tools

Resolve the launcher relative to **this SKILL.md**, not the current directory.
A portable package carries `bin\LabVIEWMCP.exe`. In a source checkout, build with
`build.ps1` first. Alternatively pass `-Executable <absolute path>` or set
`LABVIEW_SKILL_EXE` to an approved build that supports the router.

```powershell
# Set $launcher to the absolute path of this skill's scripts\labview.ps1.
powershell -NoProfile -File $launcher -DescribeTool lvai_generate_vi
```

In an exported package, search `references\tool-index.md` for tool names and
titles. `references\tools.json` holds the generated full descriptions, input
schemas and safety annotations. Both come from the packaged executable.
Do not load the full catalogue into context unnecessarily. In a checkout, or
to refresh discovery, save the catalogue and print a short index:

```powershell
powershell -NoProfile -File $launcher -ListTools -OutFile "$env:TEMP\labview-tools.json" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Tool discovery failed' }
(Get-Content "$env:TEMP\labview-tools.json" -Raw -Encoding UTF8 | ConvertFrom-Json).tools |
    Select-Object name, title
```

Always use `-DescribeTool <exact name>` before preparing unfamiliar arguments.
The discovered catalogue, not a hard-coded count, defines the available tools.
The LLM chooses the tools and their arguments; the launcher implements no
per-tool workflow.

## Invoke and interpret

Write a UTF-8 JSON **object** to an argument file, then invoke explicitly:

```powershell
$request = Join-Path $env:TEMP 'labview-pane-request.json'
[IO.File]::WriteAllText($request, '{"pattern":4833}', [Text.UTF8Encoding]::new($false))
powershell -NoProfile -File $launcher -Invoke lvai_connector_pane -ArgsFile $request
$code = $LASTEXITCODE
```

Use `{}` for a tool with no arguments. Paths inside JSON need doubled
backslashes. Use files for AIXML too: shell quoting can damage its escapes.
Omitted parameters retain their C# defaults. Cancellation tokens are injected,
never supplied in JSON.

Stdout is one JSON value; diagnostics go to stderr. JSON tool answers are
preserved. Prose answers (including references and pane maps) become
`{"text":"..."}`. Exit codes: `0` returned without a top-level failure marker,
`1` tool/execution/output failure, `2` invalid request, `130` cancellation.
Also inspect nested step results, `errorCode`, `executable`, diagram size and
the tool's documented verdict; exit zero alone does not prove a VI works.
Never report success from empty output.

`-TimeoutSeconds` (default 300) is a cooperative whole-call cancellation budget,
separate from a tool's `timeoutSeconds`. It is not a hard kill or rollback;
some backend work may finish after cancellation. A new invocation is a new
process: do not assume subscriptions or in-process state survive between calls.

## Workflow and safety

Read [references/workflows.md](./references/workflows.md) for task-specific
tool selection and verification. Start environment diagnosis with `pylv_status`
using `{}`. Explicitly approve any LabVIEW start/restart separately.

- Never start `LabVIEWMCP.exe` without an explicit CLI mode: that starts MCP.
  Never register MCP, use a stdio protocol bridge, install the MCP plugin or
  its hooks, or use agents whose tool rosters require MCP.
- Safety annotations are hints, not authorization. Get approval for writes,
  overwrites, deletes, project changes and process lifecycle operations.
  Work on backups/copies. Do not retry a mutating call blindly.
- Never run hardware-connected VIs without explicit authorization. Tool
  execution has the subject VI's real side effects.
- Inspect an existing VI's round-trip support and typedefs before editing.
  Preserve supplied front panels; do not silently regenerate them.
- Verify saved artifacts and behavior with the relevant tools before claiming
  success. The CLI transport does not change backend limitations.
