# Interior navmesh workflow

Available in SehtMCP **0.2.0**. Call `seht_status` to confirm the running version; an existing 0.1.0 process will keep its old tools until restarted. Save its open plugins first, then open them in a new server session.

## Choose the geometry source

| Tool | Input | Behavior |
| --- | --- | --- |
| `navmesh_generate_from_cell` | An editable new interior Cell, optional reference selection, explicit BSA paths | Collects supported placed NIF render geometry and bakes it with Recast |
| `navmesh_generate` | Scene vertices and triangles in cell coordinates | Bakes walkability, height clearance, radius, slopes and climb with Recast |
| `navmesh_create` | Already authored walkable vertices and triangles | Builds NAVM/NAVI and topology directly; does not calculate actor clearance |

Generation supports new interior cells owned by the editable plugin. Use a full ESP or ESM for interiors that other mods may override. ESL and ESP-FE output is supported within the conservative FormID range, with the existing warning about new light-plugin cells.

All coordinates use Skyrim units with **Z up**. Triangle indices start at zero. Recast treats upward-facing floor triangles as potentially walkable; include walls, ceilings and obstacles, not just the floor. Direct authoring normalizes triangle winding upward and welds identical vertex positions.

## Bake a cell

Create the cell and place its architecture using `record_create` and `cell_place`, or open an existing saved work-in-progress plugin. Call `plugin_checkpoint` before initial generation if you want to experiment without changing previously allocated NAVM identities.

Example `navmesh_generate_from_cell` arguments (substitute the session and cell FormKey):

```json
{
  "session": "YOUR_SESSION",
  "expectedRevision": 12,
  "cell": "000800:YourInterior.esp",
  "archives": [
    "D:\\Modlists\\SME\\Stock Game\\Data\\Skyrim - Meshes0.bsa",
    "D:\\Modlists\\SME\\Stock Game\\Data\\Skyrim - Meshes1.bsa"
  ],
  "settings": {
    "cellSize": 8,
    "cellHeight": 2,
    "agentHeight": 128,
    "agentRadius": 16,
    "maxClimb": 32,
    "maxSlope": 45
  },
  "walkableSeeds": [[0, 0, 0]],
  "dryRun": true
}
```

Choose seed points on the floors actors should reach, such as spawn points and door arrival markers. Only components nearest those seeds survive. Omit `walkableSeeds` to retain every generated component, including possible roofs or enclosed pockets. Each seed must be within `seedMaxDistance` (default 64) of a generated triangle surface. A seed selects a connected component; it does not test line of sight or collision between the point and that surface.

The result lists source references, resolved models/archives, exclusions, generated components and counts. Defaults collect enabled Static references only. Pass `references` with a list of REFR FormKeys to select architecture and static obstacles explicitly. Actor references, teleport doors, deleted/initially disabled forms and conditional enable states are not automatically baked. Missing, animated or unsupported selected NIF geometry fails the operation instead of silently generating an incomplete cell.

Loose files take priority. `archives` is an explicit list ordered low to high priority; SehtMCP does not infer active archive order from MO2. NIF transforms and clockwise REFR rotations are applied before baking. **The NIF collector uses render geometry, not Havok collision.** If those surfaces differ materially, supply simplified collision/proxy triangles to `navmesh_generate`, or finish the mesh in CK.

After a successful dry run, repeat with `dryRun: false` and the same revision. Save the returned revision. Use `navmesh_preview` (optionally `pitch: 90` for a top view), `navmesh_get`, and `plugin_validate` to review the result.

For hand-authored planar surfaces, [navmesh-room.json](../examples/navmesh-room.json) supplies a small `navmesh_create` example. This is a floor definition, not a complete furnished-cell collision plan.

## Door links

`navmesh_nearest` locates a triangle near a point and returns its NAVM FormKey, triangle index, snapped position and distance. For a local door's arrival marker, inspect the **other door's** `TeleportDestination.Position`: that position is expressed in the local destination cell.

Call `navmesh_link_door` with `session`, `expectedRevision`, `navmesh`, `door` (the local persistent door REFR), and `triangle`. It writes the NAVM door association/flag and matching NAVI information. The door must have a valid Door base and a teleport destination pointing to another placed Door. The result reports whether teleport references are reciprocal.

Both endpoints need their own valid navigation associations for NPC travel. This tool does not change teleport destinations, validate marker placement against collision, or finalize an exterior endpoint. Reusing an already-linked door is refused. Complete layout changes before linking doors, and test follower traversal through both directions in game.

## Regeneration and validation

All mutations are atomic and revision-checked. `dryRun` allocates no durable records. `plugin_save` remains the only plugin file write.

For an unlinked generated cell, `replaceExisting: true` rebuilds NAVM data while preserving FormKeys. Rebuilds that change the number of connected components, affect inherited NAVMs, or invalidate door/external-edge links are refused. Restore the checkpoint before first generation if you need to change the mesh's component structure. Never renumber published navmeshes to work around these checks.

Each connected component becomes one NAVM with reciprocal triangle adjacency, interior parent information, bounds and a complete spatial lookup. A local NAVI indexes the meshes and records isolated-region geometry or door links. The current lookup uses one exhaustive grid bucket; this is conservative and may be less efficient for very large interiors than CK's finer grid.

Output uses Recast's shared triangular polygon topology. It omits optional per-polygon detail triangulation, which produced overlapping edges on a real interior regression case. Heights are quantized to `cellHeight` and triangle interiors approximate the surface; inspect stairs, ramps and uneven floors against collision.

Validation checks finite geometry, upward nondegenerate triangles, indices, reciprocal adjacency, parent links, lookup bounds/grid, door flags and CRCs, NAVI parent/door consistency, and stale island geometry. Validation and binary re-import are structural checks; they do not establish game-engine behavior.

Limits include 250,000 input/output vertices or triangles, one million horizontal voxel columns, 50 million estimated raster column visits, 256 connected components, and 32,767 vertices/triangles per component. Defaults remove isolated regions smaller than `minRegionArea: 4096` square units; set zero to preserve tiny regions. `mergeRegionArea` defaults to 16,384 square units. Coarser voxels reduce time and memory at the cost of geometric precision.

Exterior stitching, inherited-navmesh surgery, cover generation, moving geometry, Havok collision extraction and automated CK finalization remain unsupported. No Creation Kit GUI or in-game pathfinding test was performed for this release.

## Format and algorithm references

Implementation was checked against [Mutagen's NAVM layout](https://github.com/Mutagen-Modding/Mutagen/blob/dev/Mutagen.Bethesda.Skyrim/Records/Major%20Records/NavigationMesh.xml), [NAVI layout](https://github.com/Mutagen-Modding/Mutagen/blob/dev/Mutagen.Bethesda.Skyrim/Records/Major%20Records/NavigationMeshInfoMap.xml), [xEdit's Skyrim definitions](https://github.com/TES5Edit/TES5Edit/blob/dev-4.1.6/Core/wbDefinitionsTES5.pas), and its [PathingCell/PathingDoor CRC constants](https://github.com/TES5Edit/TES5Edit/blob/dev-4.1.6/Core/wbDefinitionsCommon.pas). Baking uses [DotRecast 2026.3.1](https://github.com/ikpil/DotRecast/tree/2026.3.1). Placement rotation follows [CommonLibSSE-NG's NiMatrix3 convention](https://github.com/CharmedBaryon/CommonLibSSE-NG/blob/main/src/RE/N/NiMatrix3.cpp).
