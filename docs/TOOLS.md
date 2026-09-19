# SehtMCP tool catalog

Generated from the running server: **52 tools**. See [machine-readable schemas](tools.json) for complete JSON Schema definitions. Tool results use structured JSON plus text; NIF and navmesh previews also contain PNG image blocks.

## `archive_extract`

Extract a single BSA entry to a new workspace-relative output file. Refuses overwrite and path traversal. Asset size is bounded.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `archive` | yes | string |
| `path` | yes | string |
| `output` | yes | string |

## `archive_list`

List BSA files directly under configured dataRoots. Does not assume they are active in the game load order.

| Argument | Required | Schema / default |
| --- | --- | --- |

## `archive_search`

Search an explicitly selected BSA's file table by substring. No extraction is necessary. limit 1..500.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `archive` | yes | string |
| `query` | no | string; default `""` |
| `offset` | no | integer; default `0` |
| `limit` | no | integer; default `100` |

## `asset_resolve`

Find all loose-file providers for a relative Data path. dataRoots are low-to-high priority; the last provider wins. Archive lookup is explicit because archive load order differs from loose-file priority.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `path` | yes | string |

## `asset_search`

Search loose assets across configured dataRoots by path substring. Paginates results and reports when the scan limit is reached. Skips symlink directories.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `query` | yes | string |
| `offset` | no | integer; default `0` |
| `limit` | no | integer; default `100` |

## `cell_create_exterior`

Create an exterior Cell at grid x,y in an editable Worldspace, arranging correct exterior block/subblock groups (including negative coordinates). The worldspace must be created or overridden first. No terrain or navmesh is generated.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `worldspace` | yes | string |
| `editorId` | yes | string |
| `x` | yes | integer |
| `y` | yes | integer |
| `fields` | no | object/null; default `null` |

## `cell_place`

Place a base object or NPC into an editable Cell. Automatically selects REFR/ACHR and persistent/temporary group. Exterior persistent references are stored in the owning worldspace's persistent cell, created if needed. Exterior positions use world-space Skyrim units; rotation uses radians. Base FormKey must resolve. Does not generate navmesh or collision.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `cell` | yes | string |
| `baseFormKey` | yes | string |
| `editorId` | yes | string |
| `x` | no | number; default `0` |
| `y` | no | number; default `0` |
| `z` | no | number; default `0` |
| `rx` | no | number; default `0` |
| `ry` | no | number; default `0` |
| `rz` | no | number; default `0` |
| `scale` | no | number; default `1` |
| `persistent` | no | boolean; default `false` |

## `editor_launch`

Open the configured Windows application: creation-kit or nifskope. Optional file is an existing NIF for NifSkope. CK opens normally; this tool does not load plugins, edit UI state, or guarantee MO2 VFS access.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `application` | yes | string |
| `file` | no | string/null; default `null` |

## `editor_status`

Inspect configured Creation Kit, NifSkope, and Papyrus executables plus running CK process status. Does not access editor memory.

| Argument | Required | Schema / default |
| --- | --- | --- |

## `mo2_profile_inspect`

Read an MO2 profile's modlist.txt and plugins.txt without changing them. Returns enabled mod roots in low-to-high asset priority for config.dataRoots, plus active plugin order. It does not activate a virtual filesystem or infer archive priority.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `profileDirectory` | yes | string |
| `modsDirectory` | yes | string |

## `navmesh_create`

Create NAVM/NAVI records for a new interior or isolated exterior cell from explicitly authored walkable triangles. vertices are [x,y,z] in Skyrim units (world-space for exteriors); triangles are zero-based [a,b,c]. Welds identical positions, orients upward, builds reciprocal adjacency and spatial lookup, and separates disconnected components. Does not infer obstacles or actor clearance: use navmesh_generate for that. Atomic; plugin_save persists the result. Exteriors require a new parentless worldspace and geometry contained in the target 4096-unit grid cell; no cross-cell stitching. Rebuilding linked meshes or changing component count is refused.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `cell` | yes | string |
| `vertices` | yes | array |
| `triangles` | yes | array |
| `replaceExisting` | no | boolean; default `false` |
| `dryRun` | no | boolean; default `false` |

## `navmesh_generate`

Bake a new interior or isolated exterior cell's scene triangle geometry with Recast into Skyrim NAVM/NAVI records. vertices [x,y,z], triangles zero-based [a,b,c], Z-up Skyrim units. Floor winding must face upward; include walls, ceilings, stairs and obstacles to enforce clearance. Settings control actor height/radius, climb, slope and voxel resolution. Returns one NAVM per connected component; all records commit atomically. Geometry is supplied explicitly; this tool does not read Havok collision or the running CK. Exteriors require a new parentless worldspace and output contained in the target 4096-unit grid cell; no cross-cell edge stitching. dryRun leaves the session unchanged.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `cell` | yes | string |
| `vertices` | yes | array |
| `triangles` | yes | array |
| `settings` | no | object/null; default `null` |
| `walkableSeeds` | no | array/null; default `null` |
| `replaceExisting` | no | boolean; default `false` |
| `dryRun` | no | boolean; default `false` |

## `navmesh_generate_from_cell`

Collect placed NIF render geometry in a new interior or isolated exterior cell and bake NAVM/NAVI with Recast. Defaults to enabled Static references; references can explicitly select architecture/obstacles. Applies NIF node and REFR transforms. Loose files win; archives are explicit BSA paths in low-to-high priority order. Missing/unsupported/animated selected geometry fails the whole operation. Render geometry can differ from Havok collision: review the result or supply collision/proxy triangles to navmesh_generate. Does not process dynamic doors, actors, or conditional enable states. Exteriors require a new parentless worldspace and output contained in one 4096-unit grid cell. Includes worldspace persistent references anchored in that cell; LAND terrain needs explicit proxy triangles. dryRun leaves the session unchanged and reports geometry sources/exclusions.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `cell` | yes | string |
| `archives` | no | array/null; default `null` |
| `references` | no | array/null; default `null` |
| `settings` | no | object/null; default `null` |
| `walkableSeeds` | no | array/null; default `null` |
| `replaceExisting` | no | boolean; default `false` |
| `dryRun` | no | boolean; default `false` |

## `navmesh_get`

Inspect a NAVM's parent, bounds, vertex and triangle counts, paginated geometry, adjacency, edge links and door links. offset/limit page both vertices and triangles independently (limit 1..500); indices are absolute zero-based indices. Use this to choose an explicit door triangle or review generated topology.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `navmesh` | yes | string |
| `offset` | no | integer; default `0` |
| `limit` | no | integer; default `100` |

## `navmesh_link_door`

Associate a persistent teleport-door REFR in a new interior or isolated exterior cell with an explicit NAVM triangle. Writes PathingDoor CRC, NAVM Door flag/link, and synchronized NAVI links. Use navmesh_nearest at the local arrival marker (stored on the other endpoint). Exterior doors are resolved by worldspace and world-space position; legacy persistent doors under a grid cell move to its worldspace persistent cell with their FormKey preserved. Both destination endpoints need valid navmeshes and links; this does not edit teleport destinations, stitch exterior edges, or verify runtime pathing. Refuses moving an existing door link silently.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `navmesh` | yes | string |
| `door` | yes | string |
| `triangle` | yes | integer |

## `navmesh_nearest`

Find the closest triangle surface to a Skyrim-space point in a cell's navmeshes. Returns the NAVM FormKey, triangle index, snapped point and 3D distance, or fails if no surface is within maxDistance. Useful for matching a teleport arrival marker to navmesh_link_door. Does not establish walkability or line of sight between the supplied point and the mesh.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `cell` | yes | string |
| `x` | yes | number |
| `y` | yes | number |
| `z` | yes | number |
| `maxDistance` | no | number; default `64` |

## `navmesh_preview`

Return a PNG of the cell's navmesh triangles with visible triangle edges and a different shade per NAVM. Angles are degrees; pitch=90 gives a top view. Displays navigation surfaces only, without scene collision or runtime actors. Use after generation to inspect disconnected islands and floor coverage.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `cell` | yes | string |
| `width` | no | integer; default `640` |
| `height` | no | integer; default `640` |
| `yaw` | no | number; default `35` |
| `pitch` | no | number; default `45` |

## `nif_block_bytes`

Read a bounded hex slice of a NIF block for diagnostics. count 1..4096. Blocks are addressed by nif_blocks index.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `path` | yes | string |
| `block` | yes | integer |
| `archive` | no | string/null; default `null` |
| `offset` | no | integer; default `0` |
| `count` | no | integer; default `256` |

## `nif_blocks`

Page through NIF blocks with type, index, byte offset, and size. Does not modify the file.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `path` | yes | string |
| `archive` | no | string/null; default `null` |
| `offset` | no | integer; default `0` |
| `limit` | no | integer; default `100` |

## `nif_export_obj`

Export supported embedded SSE geometry to a new workspace-relative OBJ for external viewers. Applies supported parent transforms. No materials, UVs, bones, or animation are exported. Source NIF is unchanged.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `path` | yes | string |
| `output` | yes | string |
| `archive` | no | string/null; default `null` |

## `nif_inspect`

Inspect a Skyrim LE/SE NIF header, block types, textures, and supported geometry. path may be absolute, Data-relative, or a BSA entry with archive specified. Read-only; rejects malformed block tables.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `path` | yes | string |
| `archive` | no | string/null; default `null` |

## `nif_preview`

Return a PNG image of embedded Skyrim SE BSTriShape geometry directly to the model. Untextured diagnostic shading, orthographic view, angles in degrees. Applies supported parent transforms. Does not animate, deform skins, or reproduce Bethesda materials. Unsupported geometry is reported; NifSkope provides full viewing.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `path` | yes | string |
| `archive` | no | string/null; default `null` |
| `width` | no | integer; default `640` |
| `height` | no | integer; default `640` |
| `yaw` | no | number; default `35` |
| `pitch` | no | number; default `20` |

## `plugin_add_masters`

Add master filenames in load order, resolving transitive dependencies. Supply the current expectedRevision. Added masters are preserved at save even if not yet referenced.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `masters` | yes | array |

## `plugin_batch`

Apply up to 200 record operations atomically. Each object has op=create|update|override|duplicate|remove, plus type/editorId/fields/formKey/parent/slot as applicable. A create may set alias; later formKey, parent, or link strings may use $alias. All changes roll back on any failure. dryRun returns predicted results without committing.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `operations` | yes | array |
| `dryRun` | no | boolean; default `false` |

## `plugin_checkpoint`

Store a named in-memory checkpoint (maximum 8 per session). Checkpoints are lost when the server exits. Use plugin_save for durable output.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `name` | yes | string |

## `plugin_close`

Close a session and release loaded masters. Unsaved sessions require discard=true.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `discard` | no | boolean; default `false` |

## `plugin_create`

Create an editable Skyrim SE plugin session: kind esp, esm, esl, or esp-fe (ESL-flagged ESP). Filename extension must match. Masters are filenames in configured dataRoots; dependencies load recursively. Nothing is written until plugin_save.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `filename` | yes | string |
| `kind` | no | string; default `"esp"` |
| `author` | no | string; default `""` |
| `description` | no | string; default `""` |
| `masters` | no | array/null; default `null` |

## `plugin_diff`

Compare current records with a named checkpoint. Reports added, removed, and changed FormKeys with bounded results. Header metadata is reported separately.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `checkpoint` | yes | string |
| `offset` | no | integer; default `0` |
| `limit` | no | integer; default `100` |

## `plugin_list`

List editable sessions, revisions, dirty state, flags, and masters.

| Argument | Required | Schema / default |
| --- | --- | --- |

## `plugin_manifest`

Summarize an editable plugin's dependencies, record counts by type, explicit asset paths, saved hash, and validation. Suitable for reviewing a build before packaging.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |

## `plugin_metadata`

Set author, description, or light flag transactionally. ESL capacity is validated without renumbering FormIDs. Null values leave fields unchanged.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `author` | no | string/null; default `null` |
| `description` | no | string/null; default `null` |
| `light` | no | boolean/null; default `null` |

## `plugin_open`

Read an existing ESP/ESM/ESL into an editable session, loading its masters. This call never writes the source. Saving is restricted to the configured workspace and requires an explicit overwrite flag for existing output. Localized plugins are currently inspection-only.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `path` | yes | string |

## `plugin_package`

Create a new mod ZIP containing the last saved plugin, a Seht manifest, and explicitly selected workspace files. files maps ZIP Data-relative entry names to workspace-relative source paths. Never auto-bundles masters or game assets. Requires a clean saved session. Existing ZIPs are not overwritten.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `output` | yes | string |
| `files` | no | object/null; default `null` |

## `plugin_restore`

Restore a named in-memory checkpoint; increments revision and marks the session dirty. Loaded masters remain available.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `name` | yes | string |

## `plugin_save`

Validate, write a staged binary, re-read it, then atomically save under the workspace. relativePath filename must match plugin ModKey. overwrite=true creates a backup. Refuses unresolved links and incompatible ESL ranges. Never edits load orders or deploys to game Data.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `relativePath` | yes | string |
| `overwrite` | no | boolean; default `false` |

## `plugin_validate`

Validate duplicate IDs, FormLinks and target types, deleted targets, light FormID range, and output support. Does not prove gameplay correctness or replace xEdit, Creation Kit, or in-game testing.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |

## `record_assets`

List explicit asset links attached to a record, with loose-file resolution. Does not infer all runtime assets or assume absent loose files are missing; they may be packed in BSAs.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `formKey` | yes | string |

## `record_create`

Create a concrete record from discovered schema. fields is an object; objects merge, arrays replace, null clears. FormLinks use canonical FormKeys. Cell without parent creates an interior cell. Nested records use parent FormKey and slot, e.g. PlacedObject under Cell.Temporary. Returns allocated FormKey and revision.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `type` | yes | string |
| `editorId` | no | string/null; default `null` |
| `fields` | no | object/null; default `null` |
| `parent` | no | string/null; default `null` |
| `slot` | no | string/null; default `null` |

## `record_duplicate`

Duplicate a leaf record with a new FormKey and unique EditorID, preserving source fields and parent placement. Links still point to original targets. Subtree duplication is refused because it needs an explicit child FormKey remapping plan. Optional fields apply atomically.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `formKey` | yes | string |
| `editorId` | yes | string |
| `fields` | no | object/null; default `null` |

## `record_get`

Read typed record data, including loaded masters. Canonical key syntax: 000800:Plugin.esp. Optional field selects a top-level field to inspect large records. depth 1..16, maxItems 1..1000; truncation is marked explicitly.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `formKey` | yes | string |
| `field` | no | string/null; default `null` |
| `depth` | no | integer; default `8` |
| `maxItems` | no | integer; default `250` |

## `record_history`

Show every loaded version of a record in master load order, followed by the editable override. Useful for conflict review; only loaded masters are included.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `formKey` | yes | string |

## `record_override`

Copy a master record into the editable plugin, preserving FormKey and source fields. Nested records use Mutagen contexts to preserve parent structure without copying siblings. Optional fields apply in the same transaction.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `formKey` | yes | string |
| `fields` | no | object/null; default `null` |

## `record_references`

List outgoing FormLinks or incoming references for a FormKey, optionally including masters. Paginated to bound response size.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `formKey` | yes | string |
| `incoming` | no | boolean; default `false` |
| `includeMasters` | no | boolean; default `false` |
| `offset` | no | integer; default `0` |
| `limit` | no | integer; default `100` |

## `record_remove`

Remove a record from the editable plugin (removing an override restores master behavior). Refuses if other editable records reference it or it owns children. Does not mark the master record deleted.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `formKey` | yes | string |

## `record_schema`

Inspect a Mutagen Skyrim type's fields, enum names, collection element types, and polymorphic variants. Use $type for abstract fields. depth 0..3. Names such as Weapon, Npc, Quest, WeaponData, Effect, ScriptEntry.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `type` | yes | string |
| `depth` | no | integer; default `1` |

## `record_search`

Search editable records or winning records including loaded masters by EditorID substring, exact type name, or FormKey substring. Results are paginated; returns current revision. limit 1..500.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `query` | no | string; default `""` |
| `type` | no | string/null; default `null` |
| `includeMasters` | no | boolean; default `false` |
| `offset` | no | integer; default `0` |
| `limit` | no | integer; default `100` |

## `record_types`

Discover concrete Skyrim major record types. topLevel=true types support standalone creation. Others require parent and slot, except Cell which creates an interior cell. Overrides use source contexts, including nested records.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `query` | no | string; default `""` |

## `record_update`

Merge fields into an editable record atomically. Arrays replace in full, null clears optional values, and objects recursively merge. Read schema first. FormKey and nested major-record collections cannot be changed here. Create an override before editing a master record.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `session` | yes | string |
| `expectedRevision` | yes | integer |
| `formKey` | yes | string |
| `fields` | yes | object |

## `script_compile`

Run the configured Papyrus compiler on a workspace script. Requires papyrusFlags and papyrusImports configuration. Captures diagnostics, supports cancellation, and times out after 120 seconds. Writes compiled PEX output in workspace/scripts/compiled.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `name` | yes | string |

## `script_write`

Write a Papyrus source script under workspace/scripts/source. Scriptname must match name. Existing sources require overwrite=true and receive a backup.

| Argument | Required | Schema / default |
| --- | --- | --- |
| `name` | yes | string |
| `source` | yes | string |
| `overwrite` | no | boolean; default `false` |

## `seht_guide`

Read the authoring workflow, JSON conventions, safety properties, and precise limitations. Recommended before creating the first plugin.

| Argument | Required | Schema / default |
| --- | --- | --- |

## `seht_status`

Get SehtMCP configuration, installed tool versions, supported capabilities, and open sessions. Start here.

| Argument | Required | Schema / default |
| --- | --- | --- |
