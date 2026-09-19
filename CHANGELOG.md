# Changelog

## 0.3.0 ? 2026-09-19

Adds navigation for new isolated exterior grid cells in new parentless worldspaces, including SmallWorld worlds. Existing generation, inspection, preview and door-link tools now write exterior NAVM/NAVI worldspace parents and signed Y/X coordinates. `cell_place` stores exterior persistent references in the worldspace persistent cell; door linking also relocates legacy grid-cell persistent doors while preserving their FormKeys. Adds coordinate/ownership checks, exterior door validation, binary format tests, and a complete exterior/interior teleport-pair protocol regression. Geometry must remain inside one 4096-unit grid cell. Cross-cell stitching, inherited worlds/navmeshes, LAND extraction and Havok collision extraction remain unsupported.

## 0.2.0 — 2026-09-19

Adds seven interior-navmesh tools: Recast generation from explicit geometry or placed NIF render meshes, direct triangle authoring, geometry inspection, nearest-surface queries, teleport-door links, and PNG previews. Writes NAVM and NAVI records with parent links, adjacency, spatial lookup, island data and explicit door associations. Includes seed-based component selection, dry runs, conservative rebuilds preserving FormKeys, navmesh validation, and new synthetic and installed-asset integration checks. Publishes can use a separate output directory while an older server remains running. Havok collision extraction and exterior/CK finalization remain unsupported.

## 0.1.0 — 2026-09-19

Initial developer release with a working stdio MCP server, typed Skyrim authoring, transactional batches, reference validation, four plugin output kinds, nested overrides, interior/exterior cell helpers, BSA/NIF inspection, PNG previews, Papyrus compilation, and mod packaging. Includes client examples, pinned builds, synthetic tests, and an installed-game protocol smoke test.
