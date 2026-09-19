# Support matrix: 0.1.0

This file distinguishes implemented behavior from future integration work.

| Feature | Status / boundary |
| --- | --- |
| New ESP/ESM/ESL/ESP-FE | Implemented and binary round-trip tested |
| Record type discovery | 133 concrete types in Mutagen 0.54.4; runtime schema discovery |
| Generic typed field editing | Implemented for ordinary objects, collections, scalars, enums, links, link-or-index, translated strings, vectors, byte slices; unusual dictionary/union/value types may require additional adapters |
| NPCs and script properties | Tested with fixed level and integer Papyrus properties; no FaceGen generation |
| Quests and dialogue | Quest stages/objectives, conditions and script attachment data are editable; dialogue child records can use parent/slot. No voice generation, lip generation, scene authoring UI, or automatic fragment generation |
| Magic | Effects, spell entries, magnitude/duration, and condition FormLinks tested |
| Crafting and items | Weapon stats, keyword links, constructible records tested; exposed armor/container/leveled-list definitions require appropriate engine data |
| Interior cells | Implemented with interior block/subblock hierarchy |
| Exterior cells | Implemented with grid grouping; negative coordinates tested; no terrain/navmesh synthesis |
| Placed references | REFR/ACHR, transforms, scale, persistent/temporary collection; advanced worldspace persistent-reference semantics still require CK review |
| Nested overrides | Mutagen source contexts preserve parents; regression test ensures siblings are not copied |
| Duplication | Top-level and nested leaf records; copying records with child major records is refused |
| ESL range | Conservative 0x800..0xFFF, maximum 2048 new forms; no automatic compaction or expanded AE range |
| Existing localized plugins | Read/inspect; output deliberately blocked until STRINGS sidecar handling is implemented |
| Asset resolution | Explicit loose roots and BSA selection; no implicit active archive load-order inference |
| NIF inspection | Little-endian 20.2.0.7, user 12, Bethesda streams 83/100; block sizes, strings, texture sets |
| NIF preview | Embedded SSE BSTriShape-family positions/indices, supported parent nodes, diagnostic shading, PNG image content |
| NIF limitations | No LE geometry preview, external skin-partition geometry, dynamic vertex positions, UV/material rendering, alpha testing, animation, particle rendering, collision preview, NIF editing, or full scene-graph evaluator |
| OBJ export | Supported geometry positions/faces only, with transforms; no UV/material/skin export |
| Creation Kit | Detects configured executable and version; launch/status; no process injection or live editing bridge |
| Papyrus | Source file creation and installed compiler invocation; import paths and flags configured explicitly |
| Validation | Structural integrity and binary parsing; no automatic repair/cleaning, ITM analysis, deleted-navmesh repair, or gameplay verification |
| Mod packaging | New ZIP, saved plugin, explicit workspace files, manifest; no automatic game asset redistribution |
| Persistence | Explicit binary save; sessions and checkpoints are in memory |
| Clients | StdIO tested via independent Python JSON-RPC client; configuration examples provided for Codex/Claude/VS Code; their GUIs have not been exercised |
| Platforms | Windows development tested; Linux CI definition provided but not locally run; native editor integrations require Windows |

## Next integration work

1. Dedicated converters/tests for remaining uncommon generated field shapes; index-based list edits and richer field documentation.
2. Localized STRINGS output with atomic multi-file save and language selection.
3. Better NIF geometry coverage (skin partitions/dynamic geometry), UVs, materials, and model-facing camera controls.
4. An explicitly versioned Creation Kit bridge, dispatching mutations on the editor thread with capability probing. Pin supported CK builds instead of guessing memory offsets.
5. CK-produced assets: navmesh, FaceGen, dialogue fragments, lip files, terrain/LOD workflows. These require concrete implementations and separate integration tests before tools advertise support.

The MCP layer is designed to accept these services without changing canonical FormKeys, revision semantics, or the existing output boundary.
