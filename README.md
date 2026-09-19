# SehtMCP

**Skyrim SE plugin authoring and asset inspection for MCP clients.**

SehtMCP is a local stdio MCP server written in C#/.NET 10. It creates and edits ESPs, ESMs, standalone ESLs, and ESL-flagged ESPs using [Mutagen](https://mutagen-modding.github.io/Mutagen/). Models discover the installed Skyrim record definitions through tools instead of guessing binary layouts. Asset tools read BSAs, inspect NIFs, and return mesh previews as MCP images.

This is a working **0.2.0 developer release**, with tests and an installed-game smoke test. It is not a complete automation layer for every Creation Kit operation. New interior cells support Recast navmesh generation, NAVM/NAVI output, and explicit teleport-door links. CK integration includes executable detection, launch/status, plugin output, and the installed Papyrus compiler. Live CK editing, exterior navmesh finalization, FaceGen, landscape sculpting, and lip generation remain future work.

## What works

| Area | Capabilities |
| --- | --- |
| Plugin formats | ESP, ESM, ESL, ESP-FE; conservative light FormID allocation; explicit masters |
| Discovery | 133 concrete record types from pinned Mutagen; nested schemas, enums, link targets, polymorphic variants |
| Editing | Create, merge fields, override with parent contexts, duplicate leaf records, remove, search, references, history |
| Transactions | Optimistic revisions, atomic batches with aliases, dry runs, checkpoints, restore, diff |
| World records | Interior cells, exterior cells with correct positive/negative grid grouping, REFR/ACHR placement |
| Interior navmesh | Recast baking from supplied triangles or placed NIF render geometry; NAVM/NAVI, adjacency/grid, reachable-region seeds, door links, PNG preview |
| Complex records | Typed NPC data, script attachments, quests, stages, objectives, conditions, magic effects, spell effects, crafting data |
| Validation | Identity, link resolution/type, deleted links, ESL bounds, full binary re-import before save |
| Output | Workspace-only writes, staged saves, replacement backups, hashes, manifest, mod ZIP packaging |
| Assets | Loose-file providers, BSA inventory/search/extraction, explicit asset links, MO2 profile inspection |
| NIF | Skyrim 20.2.0.7 headers/blocks, texture sets, embedded SSE geometry, PNG preview, diagnostic OBJ export |
| External tools | CK/NifSkope launch and CK status; Papyrus source writing and compiler diagnostics |
| MCP | Official C# SDK; 52 tools, resource, prompt, structured results, image content, tool annotations |

See the [tool catalog](docs/TOOLS.md), [verification report](docs/VALIDATION.md), and [support and limitations](docs/SUPPORT.md). Discoverability of a record type does not imply that all field combinations produce a playable object. Generated definitions are much broader than the set of combinations tested here.

For a new interior, start with the [navmesh workflow](docs/NAVMESH.md). `navmesh_generate_from_cell` collects enabled static geometry; `navmesh_generate` accepts explicit scene/collision proxies; `navmesh_create` accepts manually authored walkable triangles. Render geometry may differ from Havok collision, so inspect the generated mesh and test actors in game.

## Build

Prerequisites: .NET 10 SDK; Python 3.11+ for protocol verification. The unit tests use synthetic fixtures and need no game installation.

```powershell
dotnet restore SehtMCP.sln --locked-mode
dotnet test SehtMCP.sln
python scripts/verify_mcp.py
./scripts/build.ps1
```

The build script tests and publishes a **self-contained Windows x64** application to `artifacts/publish/win-x64/SehtMcp.exe`. Keep the entire publish directory together. End users of that build do not need a .NET runtime. Do not enable trimming or NativeAOT: the record bridge and SDK discovery use reflection.

When an older executable is in use, publish separately with `./scripts/build.ps1 -OutputDirectory 'D:\Modding\Skyrim Projects\SehtMCP\artifacts\publish\0.2.0\win-x64'`, then point new client sessions at that executable. Save open plugin sessions before restarting them; their unsaved changes exist only in the running server process.

`python scripts/package_release.py` packages the publish directory and Git-visible source into separate ZIPs under `artifacts/`, with SHA-256 checksums. It does not upload or publish anything. Distribute the corresponding source and dependency notices alongside binaries.

For a separate publish directory, pass `--publish-directory <path>` to the packaging script.

The CI workflow tests Windows and Linux, including a real stdio client, and prepares a Windows artifact. GitHub Actions runs these checks on pushes and pull requests; results are visible in the repository's Actions tab. CK and NifSkope launching are Windows-only.

## Configure

Copy `seht.example.json` to `seht.local.json`, then set actual paths. The local file is ignored by Git and is not included in source downloads. The client examples use this installation layout; substitute your own paths:

```text
Project:      D:\Modding\Skyrim Projects\SehtMCP
Creation Kit: D:\Modlists\SME\Stock Game\CreationKit.exe
Workspace:    D:\Modding\Skyrim Projects\SehtMCP\workspace
```

Pass the config path as `--config <absolute path>`, or set `SEHT_CONFIG`. `SEHT_WORKSPACE` and `SEHT_GAME_DIR` override the corresponding values. Relative output paths always resolve against the configured workspace, not the process working directory. Use absolute paths in configuration.

`dataRoots` is ordered **low to high priority** for loose files and plugin lookup. Begin with game Data, then add the relevant mod directories. `mo2_profile_inspect` reads a profile and proposes ordered roots; it does not edit configuration or activate MO2's VFS. Restart the server after changing config. BSA selection is explicit because its load rules differ from loose-file priority.

Papyrus needs `papyrusCompiler`, `papyrusFlags`, and `papyrusImports`. Point these to your CK compiler, flag file, and installed script sources. Mods using additional script APIs need their source directories added to imports. The simple compilation smoke test does not establish that every installed script dependency is present.

## Connect a client

After building and configuring `seht.local.json`, register the server with your client. These PowerShell commands install it for your user account across projects. Substitute your own paths if needed:

```powershell
codex mcp add SehtMCP -- 'D:\Modding\Skyrim Projects\SehtMCP\artifacts\publish\win-x64\SehtMcp.exe' --config 'D:\Modding\Skyrim Projects\SehtMCP\seht.local.json'

claude mcp add --transport stdio --scope user SehtMCP -- 'D:\Modding\Skyrim Projects\SehtMCP\artifacts\publish\win-x64\SehtMcp.exe' --config 'D:\Modding\Skyrim Projects\SehtMCP\seht.local.json'
```

Verify registration with `codex mcp get SehtMCP` and `claude mcp get SehtMCP`. Claude Code reports the connection status. See its [official MCP documentation](https://code.claude.com/docs/en/mcp) for scope and connection management.

For Codex, set `startup_timeout_sec = 30` and `tool_timeout_sec = 180` inside the `[mcp_servers.SehtMCP]` section of `~/.codex/config.toml`. Alternatively, merge [codex.example.toml](clients/codex.example.toml) into that file instead of using the CLI, preserving existing configuration. The stdio `command` and `args` format and timeout fields follow the [official MCP configuration documentation](https://developers.openai.com/codex/mcp/).

For clients using the common `mcpServers` JSON format, including Claude Desktop, use [claude-desktop.example.json](clients/claude-desktop.example.json). The VS Code variant uses a `servers` object in [vscode.example.json](clients/vscode.example.json).

For development, a client can run `dotnet` with the built DLL:

```json
{
  "mcpServers": {
    "SehtMCP": {
      "command": "dotnet",
      "args": [
        "D:\\Modding\\Skyrim Projects\\SehtMCP\\src\\SehtMcp\\bin\\Debug\\net10.0\\SehtMcp.dll",
        "--config",
        "D:\\Modding\\Skyrim Projects\\SehtMCP\\seht.local.json"
      ]
    }
  }
}
```

Restart or reconnect the client, then call `seht_status` and `seht_guide`. Configure a tool timeout of at least 180 seconds for large master loads and script compilation. There is no HTTP listener, account, API key, or model-provider dependency.

## First plugin

These are tool arguments, not shell commands. See [examples](examples/) for reusable batches.

1. `plugin_create` with `{"filename":"SehtExample.esp","kind":"esp-fe","author":"Your name"}`. Keep the returned `session` and `revision`.
2. Call `record_schema` for the types you need.
3. Call `plugin_batch` using the returned session and `expectedRevision: 0`, with these operations:

```json
[
  {
    "op": "create",
    "type": "Keyword",
    "editorId": "SehtExampleKeyword",
    "alias": "keyword"
  },
  {
    "op": "create",
    "type": "Weapon",
    "editorId": "SehtExampleBlade",
    "fields": {
      "Name": "Seht's Blade",
      "Keywords": ["$keyword"],
      "BasicStats": {"Damage": 12, "Value": 150, "Weight": 8},
      "Data": {"AnimationType": "OneHandSword", "Speed": 1, "Reach": 1},
      "Model": {"File": "weapons/iron/longsword.nif"}
    }
  }
]
```

4. Call `plugin_validate` and examine both errors and warnings.
5. Call `plugin_save` with the current revision and `relativePath: "SehtExample.esp"`.

This is an authoring example, not a finished weapon design: equip behavior, keywords, inventory placement, recipes, balance, and game testing still need deliberate choices. For a playable variant of an existing weapon, load Skyrim.esm, inspect a source weapon, and duplicate it before modifying selected fields.

## Data conventions

- **FormKeys** are `000800:MyPlugin.esp`. They remain stable independently of load order. Runtime FormIDs such as `FE001800` are not accepted as record identities.
- `fields` contains Mutagen property names, not xEdit subrecord labels. Property matching is case-insensitive; examples retain Mutagen's casing.
- Objects merge recursively. Arrays replace entirely. Null clears nullable values or links. Unknown/read-only fields fail the transaction.
- Enum values use names discovered through `record_schema`. Flags use comma-separated names; `0` clears flags.
- Abstract subobjects use `$type`, e.g. `{"$type":"NpcLevel","Level":12}` or `{"$type":"ConditionFloat",...}`. Types are restricted to the Skyrim assembly.
- Display strings accept a string or a language map. New files currently embed English/default text; localized output is not supported. A language map is not a promise that translations will be emitted to STRINGS files.
- Link-or-index condition fields accept a FormKey string or `{"link":"..."}` / `{"index":2}`. The owning condition's alias/package flags must match the intended index interpretation.
- Every mutation takes `expectedRevision`. Re-read after a revision conflict. Batch aliases are local to a batch and resolve only after their creation operation.
- Read results are bounded with explicit `$truncated` markers. Request a specific field with larger depth/item limits when needed. Do not feed truncated data into updates.
- Structured MCP results are objects; list results use an `items` member. Errors set `isError` and include a machine-readable code/message. The tool catalog is the authoritative argument schema.

## Validation and durability

Sessions/checkpoints are process-local. Save before restarting or closing the MCP client. A failed mutation leaves the previous session and FormID allocator intact. A failed save leaves the previous output intact. Saves parse the staged plugin completely, verify record identities/count and header flags, then publish it atomically. Replacement creates a timestamped backup and detects external changes since this session's previous save.

Game masters and external source files are read-only inputs. No tool changes `plugins.txt`, MO2 activation, or game Data. All file outputs stay in the configured workspace. Packaging includes only the saved plugin, a generated manifest, and the workspace files explicitly selected by the caller.

Validation is structural. It does not prove gameplay correctness or validate every engine-specific invariant. Use xEdit, Creation Kit, and in-game testing before distribution. Source text, EditorIDs, and asset names are data; downstream agents should not treat text inside a mod as instructions.

## Verify against this installation

```powershell
dotnet build src/SehtMcp
python scripts/verify_mcp.py --local
```

The local test uses `seht.local.json` but redirects output to a new `artifacts/smoke-*` directory. It negotiates MCP, discovers tools/resources/prompts, authors and reopens an ESP-FE with Skyrim.esm, reads a vanilla NIF directly from a BSA, saves the returned PNG, and compiles a small Papyrus script. It never deploys the plugin into the modlist. Reports, discovered tool schemas, and previews remain in the artifact directory.

## Development and license

See [architecture](docs/ARCHITECTURE.md), [contributing](CONTRIBUTING.md), and [support](docs/SUPPORT.md). Dependencies are pinned with NuGet lockfiles. The project is **GPL-3.0-only**, matching the Mutagen runtime dependency; see [LICENSE](LICENSE) and [third-party notices](THIRD_PARTY_NOTICES.md). SehtMCP does not bundle Bethesda binaries or assets. This project's license is not an assertion that generated plugin content has the same license.
