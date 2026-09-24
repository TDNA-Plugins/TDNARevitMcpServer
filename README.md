# Revit MCP Server

A [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) server that connects Autodesk Revit 2025+ to AI assistants like Claude. Query elements, create views, manage sheets, and modify parameters — all through natural language.

## How It Works

The system has two halves that communicate over a named pipe:

```
Claude Desktop / VS Code
        │
        │ stdio (JSON-RPC)
        ▼
┌─────────────────┐          Named Pipe           ┌──────────────────┐
│  RevitMcp.Server │  ◄──── "revit-mcp-bridge" ──► │  RevitMcp.Addin  │
│  (Console App)   │     length-prefixed JSON      │  (Revit Add-in)  │
└─────────────────┘                                └──────────────────┘
                                                           │
                                                    Revit API calls
                                                    (main thread only)
```

**RevitMcp.Server** is a standalone console app that speaks MCP over stdio. Claude Desktop launches it automatically.

**RevitMcp.Addin** loads inside Revit, runs a named-pipe listener on a background thread, and marshals all Revit API calls to the main thread via `ExternalEvent`.

**RevitMcp.Core** contains the shared contracts, command names, and handler implementations used by both sides.

## Available Tools

### Elements
| Tool | Description |
|------|-------------|
| `get_elements` | Retrieve elements, optionally filtered by category (default limit: 100) |
| `get_element_by_id` | Get detailed info for a single element including bounding box and location |
| `get_element_parameters` | List all parameters and values for an element |
| `get_selected_elements` | Get currently selected elements in the active view |
| `set_parameter` | Set a parameter value on an element |
| `delete_elements` | Delete elements with preview mode (set `confirm=true` to execute) |

### Views
| Tool | Description |
|------|-------------|
| `open_view` | Open a view by ID or name |
| `create_plan_view` | Create a floor plan or ceiling plan for a level |
| `create_elevation_view` | Create an elevation at a location and direction |
| `create_section_view` | Create a section defined by origin, direction, and dimensions |
| `create_schedule_view` | Create a schedule for a category with optional field selection |

### Sheets
| Tool | Description |
|------|-------------|
| `create_sheet` | Create a new sheet with a title block |
| `add_view_to_sheet` | Place a view on a sheet as a viewport |

### Model & Project
| Tool | Description |
|------|-------------|
| `get_project_info` | Get project metadata (name, number, client, address, etc.) |
| `analyze_model_statistics` | Get element counts by category, family, type, and level |
| `get_current_view_info` | Get active view details (type, scale, detail level, etc.) |
| `export_room_data` | Extract rooms with area, volume, perimeter, and department data |

### QAQC
Ported from the TheatreDNA Launchpad QAQC scripts. All read-only unless noted.

| Tool | Description | Ported from |
|------|-------------|-------------|
| `audit_project_health` | Model composition, in-place families, views not on sheets, empty sheets, links/imports, unused groups, warnings, with review flags | `Project_AuditReport` |
| `audit_linked_files` | Revit and CAD links: status, path, broken/cloud, linked vs imported, view-specific, pinned | `Audit_LinkedFiles` |
| `check_element_visibility` | Why an element (host or linked) is or isn't visible in a view | `Check_ElementsVisibility` |
| `find_untagged_elements` | Instances of given families not tagged in the views on given sheets | `Check_TagsOnSheets` |
| `audit_fonts` | Fonts used by text, dimensions and tags; lists off-standard instances | `QAQC_FontAudit` |
| `audit_type_names` | Placed type names against a delimiter naming convention; missing Descriptions | `HOPA_NameDecoder` |
| `find_mirrored_instances` | Mirrored family instances, by category/family/view | `Element_MirrorInPlace` |
| `get_design_options` | Option sets, options, primary, element counts, empty options | `Export_DesignOptions` |
| `get_note_blocks` | Note block schedules, their family, row count, sheets | `Check_NoteBlockFamily` |
| `audit_sheet_list_visibility` | "Appears In Sheet List" vs the `x`-prefix convention. **`fix=true` modifies the model** | `AppearsInSheetList-QAQC` |
| `find_unpinned_elements` | Unpinned grids, levels, scope boxes, links, title blocks, viewports. **`pin=true` modifies the model** | `PinAll` |
| `save_model_snapshot` | Save a JSON snapshot of sheets, views, schedules, families (+ parameters), annotation, design options | `Project_SnapshotV2` |
| `list_model_snapshots` | List saved snapshots for the active model | — |
| `compare_model_snapshot` | What changed since a snapshot: sheets, views, families added/deleted/moved/re-typed/changed | `Project_CompareV2` |
| `compare_family_to_file` | Compare the open family against another .rfa: types, parameters, values | `QAQC_Compare Revit Families` |

Snapshots are stored under `%APPDATA%\TheatreDNA\RevitMcp\Snapshots\<model>`, keyed on the central model name so every
user's local copy shares one history. The JSON matches the Launchpad `Project_SnapshotV2` format.

### Prompts
Reusable QAQC workflows, shown by MCP clients as slash commands (e.g. `/mcp__revit-mcp__qaqc_model_review`).

| Prompt | What it does |
|--------|--------------|
| `qaqc_model_review` | Runs the model-health tools in order and writes a severity-ranked issues table |
| `qaqc_pre_issue_check` | Checks a sheet set before issue: changes since last snapshot, untagged elements, fonts, sheet list, mirrored |

## Prerequisites

- **Autodesk Revit 2025** (or newer)
- **.NET 8 SDK** — [download here](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Claude Desktop** or another MCP-compatible client

## Building

Build the entire solution:

```bash
dotnet build RevitMcpServer.sln -c "Debug R25"
```

Build the server only:

```bash
dotnet build src/RevitMcp.Server/RevitMcp.Server.csproj
```

Publish the server for deployment:

```bash
dotnet publish src/RevitMcp.Server -c Release -o ./publish
```

The add-in's post-build step automatically copies the DLL and `.addin` manifest to Revit's add-in folder.

## Setup

### 1. Install the Revit Add-in

Build the `RevitMcp.Addin` project with a Revit 2025 configuration (`Debug R25` or `Release R25`). The post-build event copies the output to:

```
%APPDATA%\Autodesk\REVIT\Addins\2025\RevitMcp.Addin\
```

### 2. Configure Claude Desktop

Add the server to your Claude Desktop config (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "revit-mcp": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\src\\RevitMcp.Server\\RevitMcp.Server.csproj"]
    }
  }
}
```

Or if you've published the server:

```json
{
  "mcpServers": {
    "revit-mcp": {
      "command": "C:\\path\\to\\publish\\RevitMcp.Server.exe"
    }
  }
}
```

### 3. Launch

1. Open Revit — the add-in loads automatically and starts the pipe server
2. Open Claude Desktop — it spawns the MCP server process on first tool call
3. The MCP Status button on the ribbon turns green when connected

## Ribbon Buttons

The add-in creates a **Revit MCP** ribbon panel with four buttons:

| Button | Action |
|--------|--------|
| **MCP Status** | Shows connection state, uptime, tool call stats, and last error |
| **Restart Connection** | Restarts the pipe server (fixes dropped connections or stuck requests) |
| **Kill Server** | Terminates the MCP server process (Claude Desktop respawns it automatically) |
| **Open Claude** | Launches Claude Desktop or brings it to the foreground |

The MCP Status icon changes color based on connection state: green (connected), yellow (waiting), red (error).

## Solution Structure

```
RevitMcpServer.sln
├── RevitMcp.Core/           Shared library (no MCP SDK or Revit UI dependency)
│   ├── Commands/            Command name constants
│   ├── Handlers/            ICommandHandler implementations (one per tool)
│   └── Messages/            BridgeRequest / BridgeResponse records
│
├── RevitMcp.Server/         MCP server (stdio transport, console app)
│   ├── Bridge/              Named pipe client (RevitBridgeClient)
│   └── Tools/               [McpServerToolType] tool definitions
│
└── RevitMcp.Addin/          Revit add-in (named pipe server, ribbon UI)
    ├── Bridge/              PipeServer, RequestChannel, ExternalEventExecutor
    ├── Status/              McpStatusTracker (connection state singleton)
    └── UI/                  Ribbon button commands
```

## Adding a New Tool

1. Add the command name to `RevitMcp.Core/Commands/CommandNames.cs`
2. Create an `ICommandHandler` in `RevitMcp.Core/Handlers/`
3. Create or update a `[McpServerToolType]` class in `RevitMcp.Server/Tools/`
4. Register the handler in `RevitMcp.Addin/App.cs`

## Key Design Decisions

- **Named pipe bridge** — The MCP server and Revit run in separate processes. A named pipe (`revit-mcp-bridge`) with 4-byte length-prefixed JSON framing connects them.
- **ExternalEvent threading** — All Revit API calls must run on Revit's main thread. Requests queue through a `Channel<PendingRequest>` and execute via `ExternalEvent`.
- **System.Text.Json** — Used everywhere for serialization. No Newtonsoft.Json dependency.
- **Preview-mode deletes** — `delete_elements` defaults to dry-run. The AI must explicitly set `confirm=true` to mutate the model.

## License

All rights reserved.
