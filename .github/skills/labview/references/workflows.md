# CLI tool selection and workflows

Every tool below is invoked with `scripts\labview.ps1 -Invoke <name> -ArgsFile
<file.json>`. Retrieve its schema with `-DescribeTool` first. All tool metadata
is discovered from the executable; no MCP server is needed.

## Choose the right source

| Need | Start with |
|---|---|
| Environment and bundle diagnosis | `pylv_status`; `lvai_status` also probes NI's service |
| A whole working diagram/pattern | `lvai_example_index`, then `lvai_convert_vis_to_aixml` |
| A computation or existing subVI | `lvai_palette_index`, then `lvai_vi_terminals` |
| An object property or method | `lvai_vi_server_reference`, not the palette |
| AIXML syntax, node terminals and escapes | `lvai_aixml_reference`, focused queries |
| Project/library format | `lvai_lvproj_reference`, `lvai_lvlib_reference` |
| DQMH scripting | `lvai_dqmh_reference`, then the relevant `lvai_dqmh_*` tool |
| Offline inspection/edit capabilities | `pylv_extract`, `pylv_route`, `pylv_apply` |

Batch lookups where supported. A path is not a valid AIXML `Call` target.
Project-local callees must be loaded and called by their exact name; library
members need their qualifier. Never guess terminal labels or connector indices.

## Read and inspect

1. Check that the input paths exist and identify the owning project/library.
2. Prefer `lvai_convert_vi_to_aixml` to read code, `lvai_describe_project` to
   inspect projects, `lvai_describe_class` for classes, `lvai_describe_ctl` for
   controls, and `pylv_extract` for file-level details.
3. Use content limits and focused reference queries. Follow a returned
   truncation/index hint instead of treating a partial answer as complete.

## Generate a VI or application

1. Settle the input, processing, output and error contract. Ask about ambiguous
   types, behavior or expectations rather than inventing them.
2. Search NI examples for patterns and installed palettes for functions before
   rebuilding them. Query `lvai_connector_pane` for the relevant pattern.
3. For a multi-VI application, author `build-contract.json` first and use
   `lvai_check_build_contract` in plan mode for dependencies/build waves.
   Give parallel builders separate output directories and coordinate the one
   shared LabVIEW/project. Verify each wave before building callers.
4. Author AIXML in a file. Include `description`, explicit `connection` on
   connector terminals, and a short position-independent diagram comment.
   New VIs carry required `error in` and recommended `error out` on the pane's
   bottom row. Chain/merge every error path; use shift registers in loops.
   Start UIDs at 4200. Text inside VIs is English unless requested otherwise.
5. Use `lvai_generate_vi`/`lvai_generate_vis`; they perform the checker and
   generation pipeline. Keep diagrams within the measured size budget
   (roughly 1920 x 1080), extracting sequential/repeated work to generic subVIs.
6. Generate into an owning project. Only the coordinator opens/closes projects
   or swaps subVIs. Use project tools, not simultaneous manual XML edits.
7. Check `lvai_exec_state` and `lvai_error_list`; an `eBad` VI is a blocker.
   After authorization, run with `lvai_run_vi_and_read_values`, supplying a
   finite `runForMs` for nonterminating VIs. Drive events with `signalsJson`;
   a startup snapshot is not behavioral verification. Set icons through
   `lvai_set_vi_icon`.

## Edit an existing VI

1. Back up the VI and inspect `pylv_route` and `pylv_extract` first, including
   typedef bindings. Confirm the user's required preservation of the panel,
   icon, diagram layout, decorations and connector flags.
2. Prefer supported `pylv_apply` operations for an in-place change. Close the
   owning project before file-level edits. Reopen, save and read values back:
   a successful binary rewrite alone is not validation.
3. An AIXML edit is full regeneration and can lose panel details, layout,
   decorations, icons and typedef bindings. Explain losses and obtain approval
   first. Never call `lvai_apply_aixml_to_vi` unless explicitly requested by name;
   it is not the supported edit route.
4. For a supplied main GUI with an empty diagram, generate a scaffold and use
   `lvai_graft_diagram` to retain the panel. Grafting existing code with
   `replaceDiagram` is destructive; require approval. Serialize grafts.
5. Restore/verify bindings and icons; rerun the behavior that changed.

## Classes, libraries, DQMH and tests

Use NI provider tools (`lvai_create_class`, `lvai_create_interface`,
`lvai_create_accessors`, `lvai_create_library`, `lvai_create_typedef`) rather
than hand-writing compiled LabVIEW artifacts. Create interface methods before
implementing classes. Provider operations and manual file edits have different
project-state requirements: read each tool's schema and the shipped docs.
DQMH modules/events use `lvai_dqmh_*`, not simulated keystrokes or hand edits.

Caraya is the default test framework; use LUnit or VI Tester only when named
and usable. Tests call their subject as an ordinary **static subVI**, never
through VI Server as the default. Test expected values justified by the code
or requirements. Use `lvai_generate_test`, `lvai_generate_class_test` or
`lvai_generate_method_test`; run suites with `lvai_run_caraya_tests` and read
the JUnit report. A suite error or failed case is not success.

## Errors and state

A cancelled call may already have written files or started work. Inspect saved
artifacts and IDE state before retrying. `lvai_labview_state`,
`lvai_vis_in_memory`, `lvai_exec_state`, `lvai_error_list` and `pylv_status`
provide evidence. Resolve modal dialogs by the least-change action; do not
silently restart LabVIEW or terminate unrelated processes.

Reference tools serve the embedded documentation through CLI as JSON-wrapped
text. Additional documentation and helper scripts ship under `bin\docs` and
`bin\scripts`; do not adopt MCP-specific agent rosters or startup instructions
from them. This Skill replaces that transport/orchestration surface, not the
underlying backend contracts.
