# Authoring examples

The `.batch.json` files are the `operations` argument of `plugin_batch`. Create a session, pass its current revision, apply the batch, validate, and save. These examples use self-contained synthetic record graphs so they are reproducible without shipping game data.

- `weapon-and-recipe.batch.json` exercises typed weapon fields, an asset path, aliases, keyword lists, and crafting links. Its synthetic workbench keyword is for demonstrating links; replace it with a discovered real workbench keyword and add crafting ingredients for a usable recipe.
- `quest-and-spell.batch.json` exercises stages, objectives, spell effects, and a polymorphic GetStage condition. Configure a suitable magic-effect archetype, casting/delivery data, quest behavior, and distribution before playtesting it.

For a practical item variant, create a session with Skyrim.esm, find a source record with `record_search(includeMasters=true)`, inspect it, then call `record_duplicate` with a new EditorID. That preserves the base object's sound, equipment, model, and other settings while you adjust selected fields.

For cells, create a Cell for an interior or a Worldspace plus `cell_create_exterior` for an exterior. Use `cell_place` for transforms. Geometry and navmesh are separate requirements; creating cell records does not generate a navigable world.
