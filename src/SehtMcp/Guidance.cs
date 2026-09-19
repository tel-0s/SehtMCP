using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SehtMcp;

[McpServerResourceType, McpServerPromptType, McpServerToolType]
public static class Guidance
{
    public const string GuideText = """
        SehtMCP 0.1 authoring workflow

        1. seht_status: inspect dataRoots, workspace, and configured integrations.
        2. plugin_create(filename, kind, masters) or plugin_open(path). Keep session and revision.
        3. record_types and record_schema expose the installed Mutagen Skyrim definitions.
        4. record_search(includeMasters=true) finds source forms; record_get inspects them.
        5. record_create builds a new record. record_override copies a master form with context;
           record_duplicate allocates a new identity. Preserve generated FormKeys.
        6. record_update merges typed JSON fields. Arrays REPLACE, objects MERGE, null CLEARS.
           FormLinks are strings such as 012EB7:Skyrim.esm. Never use runtime FE load-order IDs.
           Enums use discovered names; flags use comma-separated names. Abstract subobjects
           specify an allowlisted $type, e.g. {"$type":"NpcLevel","Level":10}.
           Display text can be a string or language map {"English":"Example"}.
           Asset paths are relative to Data (Mutagen may normalize a type-specific prefix).
        7. Every edit supplies expectedRevision and returns the next revision. A conflict
           requires re-reading the session. plugin_batch is all-or-nothing and supports
           aliases ($myRecord) for links to records created earlier in the same batch.
        8. plugin_checkpoint and plugin_restore support experimentation in this process.
           Sessions and checkpoints are in memory; plugin_save is the durable operation.
        9. plugin_validate checks identity, references, target types, and conservative ESL
           limits. plugin_save also writes and re-reads the binary before atomic publication.
           All outputs stay in workspace; replacement needs overwrite=true and makes a backup.
        10. Inspect the result in xEdit/CK and playtest before distributing. Validation does
            not check quest logic, collision agreement, balance, assets, or runtime behavior.

        New interior navmeshes: navmesh_generate_from_cell bakes placed static NIF render
        geometry with Recast. Supply explicit BSA paths in archives when assets are packed.
        It fails on missing/unsupported selected geometry. Render surfaces can differ from
        Havok collision. navmesh_generate instead accepts explicit scene/proxy triangles;
        navmesh_create accepts already-authored walkable triangles without clearance baking.
        Coordinates are Skyrim units, Z-up; baking needs upward floor winding. Optional
        walkableSeeds retain only the components nearest those points, excluding roof islands.
        Use dryRun first, then navmesh_preview and navmesh_get to review the generated mesh.
        navmesh_nearest finds a triangle for a door arrival point. navmesh_link_door writes
        NAVM/NAVI door links; both teleport endpoints need their own links and runtime checks.
        Regeneration preserves FormKeys and refuses linked meshes or changed component counts.
        plugin_validate checks NAVM topology, lookup bounds/grid, and NAVI consistency.

        ESP, ESM, standalone ESL, and ESL-flagged ESP are supported. Light files use the
        conservative 0x800..0xFFF range (2048 new records); compaction is never automatic.
        New light-plugin cells have engine override limitations; prefer a full plugin.
        Existing localized plugins can be inspected but cannot yet be saved.

        Nested creation: Cell without a parent creates an interior cell. PlacedObject or
        PlacedNpc uses parent=<cell FormKey>, slot=Temporary or Persistent. DialogResponses
        uses the appropriate parent DialogTopic child collection discovered in the schema.
        cell_create_exterior creates a grid cell under an editable worldspace. cell_place
        adds a positioned REFR/ACHR. Nested overrides and leaf duplication use source contexts;
        whole-subtree duplication is refused until a complete remapping plan exists.
        Other complex records expose their typed fields; creating a record does not establish
        that its default values constitute a playable object.

        Assets: dataRoots are ordered low-to-high priority for loose assets. BSA access is
        explicit (archive_list/archive_search); this server does not infer active archives
        or an MO2 virtual filesystem. NIF previews return PNG content to the client, using
        supported embedded SSE triangle geometry. They do not reproduce materials, skins,
        particles, collision, or animation. nif_inspect reports unsupported geometry.
        Use editor_launch(application="nifskope", file=<absolute path>) for a full viewer.

        CK integration in this release is launch/status plus compatible plugin output.
        There is no injected CK bridge, live form mutation, exterior navmesh finalization, FaceGen,
        landscape sculpting, or automated CK save. Papyrus uses the installed compiler,
        with configured flags and import paths. No arbitrary shell/code execution tool exists.
        """;

    [McpServerTool(Name = "seht_guide", ReadOnly = true), Description("Read the authoring workflow, JSON conventions, safety properties, and precise limitations. Recommended before creating the first plugin.")]
    public static string Guide() => GuideText;

    [McpServerResource(UriTemplate = "seht://guide", Name = "SehtMCP authoring guide", MimeType = "text/plain")]
    public static string Resource() => GuideText;

    [McpServerPrompt(Name = "build_skyrim_plugin"), Description("Guide an agent through authoring and validating a Skyrim plugin with SehtMCP.")]
    public static string BuildPlugin([Description("The mod's intended behavior, filename, and dependencies.")] string requirements) => $"Build a Skyrim SE plugin for these requirements:\n{requirements}\n\nFollow this workflow and report any unsupported requirements explicitly:\n{GuideText}";
}
