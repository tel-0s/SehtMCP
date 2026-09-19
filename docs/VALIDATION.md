# Local verification — 2026-09-19

## 0.2.0 navmesh release

The Windows Release build passed **46 tests**, with zero compiler warnings/errors and a self-contained win-x64 publish. Seventeen new navmesh cases cover the four plugin formats, NAVM/NAVI binary round trips, independently decoded grid bytes, Recast radius/clearance/slope/climb behavior, obstacle rejection, disconnected regions and seed filtering, preserved identities, dry runs/rollback, malformed adjacency/grid data, light allocation failure, new-cell boundaries, transformed NIF geometry, missing assets, nearest-surface queries and door links.

The published executable passed `python scripts/verify_mcp.py --local --server artifacts/publish/0.2.0/win-x64/SehtMcp.exe`: **52 tools**, 133 record types, interior navmesh dry run/bake/inspection/nearest-surface query/PNG preview/validation/save/reopen, the previous plugin and asset checks, and real Papyrus compilation. It also baked a transformed vanilla Dwemer floor directly from an installed BSA. Latest complete protocol artifacts: `artifacts/smoke-1789849386109145400/`.

An additional private regression plugin supplied five new, furnished interiors. Architectural REFR selections were baked from a snapshot under `artifacts/ninth-navmesh-regression/`; generated records and the copied plugin passed structural validation and binary save/re-import. The source project was not edited. This case exposed overlapping edges in Recast's optional detail triangulation; output now uses its shared triangular polygon topology. It also demonstrated the need to exclude conditional objects and select reachable components rather than retain every raised/roof surface.

Codex and Claude Code now point to the separate 0.2.0 publish directory. Codex reports the enabled stdio entry with 30/180-second timeouts; Claude Code reports Connected. Old server processes were left running so their sessions can be saved before reconnecting.

These are structural, protocol, and geometry tests. No CK GUI finalization, xEdit GUI check, or in-game actor/follower traversal was performed. Render geometry is not a substitute for verifying Havok collision agreement.

## 0.1.0 build (historical baseline)

- Windows x64, .NET SDK 10.0.401.
- Pinned Mutagen.Bethesda.Skyrim 0.54.4 and official MCP SDK 2.2.0.
- `scripts/build.ps1`: locked restore, Release build, 29 passing tests, self-contained win-x64 publish.
- Build finished with **zero warnings and zero errors**.

## 0.1.0 tests

29 tests cover the four plugin output kinds; FormID allocation; typed weapons and crafting links; reference target types; ESL overflow; rollback and revision conflicts; dry runs; interior/exterior cell hierarchy; negative coordinates; placed transforms; context-aware nested overrides; NPC levels; script properties; quest stages/objectives/conditions; spell effects; duplication/removal; checkpoint/restore/diff; path constraints; saved-output backups and external-change detection; NIF parsing/rendering and malformed input; packaging; explicit asset references; and MO2 priority reading.

All fixtures are synthetic and do not require shipping Bethesda data.

## 0.1.0 published executable integration

Command:

```powershell
python scripts/verify_mcp.py --local --server artifacts/publish/win-x64/SehtMcp.exe
```

The independent Python stdio client successfully:

1. Negotiated MCP and discovered **45 tools**, guide resource, and authoring prompt.
2. Discovered **133 concrete Skyrim record types**, and requested nested schemas.
3. Created an ESL-flagged ESP using the installed Skyrim.esm as a master.
4. Authored linked keyword/weapon records through an atomic batch, read them back, validated, saved, and reopened the plugin.
5. Generated a manifest and mod ZIP.
6. Read `meshes/weapons/iron/longsword.nif` directly from an installed vanilla BSA, inspected its blocks/textures, and received a valid PNG preview through MCP image content.
7. Compiled a small Papyrus script using the installed Creation Kit compiler and configured flag file.

Artifacts from this run are under `artifacts/smoke-1789845094765238100/`: report.json, tools.json, record schemas, the authored plugin and ZIP, NIF inspection JSON, preview PNG, compiler output, and server stderr log. The preview was visually inspected. Test output is isolated from the active game/modlist.

## 0.1.0 client installation

SehtMCP was registered in the user's Codex and Claude Code configurations using the published executable and `seht.local.json`. Codex was configured with a 30-second startup timeout and 180-second tool timeout. `codex mcp get SehtMCP` confirmed the enabled stdio entry, and `claude mcp get SehtMCP` reported **Connected**.

An independent stdio probe read the installed Codex configuration, confirmed that Claude Code used the same command and arguments, negotiated MCP, discovered all 45 tools, and successfully called `seht_status`. Existing client settings were preserved. Fresh client sessions are required to discover newly registered tools.

## Not verified

The plugin was not loaded in the Creation Kit GUI, xEdit, or a running game. Client desktop UIs were not exercised. Native CK/NifSkope launching is implemented but was not invoked during verification. Linux was not tested in this local Windows verification; the CI workflow covers it separately. No claim of complete engine validation, universal record-field coverage, or production readiness follows from these checks.
