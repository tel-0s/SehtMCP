using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SehtMcp;

public static class ToolResult
{
    public static CallToolResult Ok(object? value)
    {
        var json = JsonSerializer.SerializeToElement(value, SehtConfig.Json);
        if (json.ValueKind != JsonValueKind.Object) json = JsonSerializer.SerializeToElement(new { items = value }, SehtConfig.Json);
        return new() { IsError = false, Content = [new TextContentBlock { Text = json.GetRawText() }], StructuredContent = json };
    }
    public static CallToolResult Error(Exception ex)
    {
        var cause = ex.GetBaseException();
        var code = cause switch { ArgumentException => "invalid_argument", FileNotFoundException => "not_found", IOException => "io_error", InvalidOperationException => "invalid_state", OperationCanceledException => "cancelled", _ => "operation_failed" };
        return new() { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = new { code, message = cause.Message } }) }] };
    }
    public static CallToolResult Run(Func<object?> action) { try { return Ok(action()); } catch (Exception e) when (e is not OutOfMemoryException) { return Error(e); } }
}

[McpServerToolType]
public sealed class AuthoringTools(PluginWorkspace workspace, SehtConfig config, ExternalTools external)
{
    private CallToolResult Run(Func<object?> action) { lock (workspace.Gate) return ToolResult.Run(action); }

    [McpServerTool(Name = "seht_status", ReadOnly = true), Description("Get SehtMCP configuration, installed tool versions, supported capabilities, and open sessions. Start here.")]
    public CallToolResult Status() => Run(() => new { version = "0.3.0", workspace = config.Workspace, dataRoots = config.DataRoots, external = external.Status(), sessions = workspace.List(), runtime = Environment.Version.ToString(), transport = "stdio", capabilities = new[] { "typed-plugin-authoring", "esp-esm-esl-espfe", "transactional-batches", "checkpoints", "structural-validation", "bsa-read", "nif-inspect", "nif-png-preview", "papyrus-compile", "interior-navmesh-generation", "isolated-exterior-navmesh-generation", "worldspace-persistent-doors", "recast-cell-render-geometry", "navmesh-door-links", "navmesh-png-preview" } });

    [McpServerTool(Name = "plugin_create", Destructive = false), Description("Create an editable Skyrim SE plugin session: kind esp, esm, esl, or esp-fe (ESL-flagged ESP). Filename extension must match. Masters are filenames in configured dataRoots; dependencies load recursively. Nothing is written until plugin_save.")]
    public CallToolResult Create(string filename, string kind = "esp", string author = "", string description = "", string[]? masters = null) => Run(() => workspace.Create(filename, kind, author, description, masters ?? []).Summary());

    [McpServerTool(Name = "plugin_open", Destructive = false), Description("Read an existing ESP/ESM/ESL into an editable session, loading its masters. This call never writes the source. Saving is restricted to the configured workspace and requires an explicit overwrite flag for existing output. Localized plugins are currently inspection-only.")]
    public CallToolResult Open(string path) => Run(() => workspace.Open(path).Summary());

    [McpServerTool(Name = "plugin_list", ReadOnly = true), Description("List editable sessions, revisions, dirty state, flags, and masters.")]
    public CallToolResult List() => Run(workspace.List);

    [McpServerTool(Name = "plugin_close", Destructive = true), Description("Close a session and release loaded masters. Unsaved sessions require discard=true.")]
    public CallToolResult Close(string session, bool discard = false) => Run(() => { workspace.Close(session, discard); return new { closed = session }; });

    [McpServerTool(Name = "plugin_add_masters", Destructive = false), Description("Add master filenames in load order, resolving transitive dependencies. Supply the current expectedRevision. Added masters are preserved at save even if not yet referenced.")]
    public CallToolResult AddMasters(string session, long expectedRevision, string[] masters) => Run(() => workspace.AddMasters(session, expectedRevision, masters));

    [McpServerTool(Name = "plugin_metadata", Destructive = false), Description("Set author, description, or light flag transactionally. ESL capacity is validated without renumbering FormIDs. Null values leave fields unchanged.")]
    public CallToolResult Metadata(string session, long expectedRevision, string? author = null, string? description = null, bool? light = null) => Run(() => workspace.Mutate(session, expectedRevision, s =>
    {
        if (author is not null) s.Mod.ModHeader.Author = author;
        if (description is not null) s.Mod.ModHeader.Description = description;
        if (light is not null)
        {
            if (!light.Value && s.Mod.ModKey.Type == ModType.Light) throw new ArgumentException("An .esl file must remain light.");
            s.Mod.IsSmallMaster = light.Value;
            if (light.Value && s.Mod.EnumerateMajorRecords().Any(r => r.FormKey.ModKey == s.Mod.ModKey && r.FormKey.ID is < 0x800 or > 0xFFF)) throw new ArgumentException("Records do not fit the conservative light range. Compaction is not automatic.");
        }
        return s.Summary();
    }));

    [McpServerTool(Name = "record_types", ReadOnly = true), Description("Discover concrete Skyrim major record types. topLevel=true types support standalone creation. Others require parent and slot, except Cell which creates an interior cell. Overrides use source contexts, including nested records.")]
    public CallToolResult Types(string query = "") => Run(() =>
    {
        var probe = new SkyrimMod(ModKey.FromFileName("Schema.esp"), SkyrimRelease.SkyrimSE);
        return RecordCodec.Implementations(typeof(IMajorRecord)).Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(t => new { type = t.Name, topLevel = probe.TryGetTopLevelGroup(t) is not null, interiorCell = t == typeof(Cell) }).ToArray();
    });

    [McpServerTool(Name = "record_schema", ReadOnly = true), Description("Inspect a Mutagen Skyrim type's fields, enum names, collection element types, and polymorphic variants. Use $type for abstract fields. depth 0..3. Names such as Weapon, Npc, Quest, WeaponData, Effect, ScriptEntry.")]
    public CallToolResult Schema(string type, int depth = 1) => Run(() => RecordCodec.Schema(type, depth));

    [McpServerTool(Name = "record_search", ReadOnly = true), Description("Search editable records or winning records including loaded masters by EditorID substring, exact type name, or FormKey substring. Results are paginated; returns current revision. limit 1..500.")]
    public CallToolResult Search(string session, string query = "", string? type = null, bool includeMasters = false, int offset = 0, int limit = 100) => Run(() =>
    {
        AssetService.Page(offset, limit); var s = workspace.Get(session);
        IEnumerable<IMajorRecordGetter> records = s.Mod.EnumerateMajorRecords();
        if (includeMasters) records = s.Dependencies.SelectMany(d => d.EnumerateMajorRecords()).Concat(records).GroupBy(r => r.FormKey).Select(g => g.Last());
        var matches = records.Where(r => (type is null || PluginWorkspace.TypeName(r).Equals(type, StringComparison.OrdinalIgnoreCase)) && ((r.EditorID?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) || r.FormKey.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new { revision = s.Revision, total = matches.Length, offset, records = matches.Skip(offset).Take(limit).Select(PluginWorkspace.Brief).ToArray() };
    });

    [McpServerTool(Name = "record_get", ReadOnly = true), Description("Read typed record data, including loaded masters. Canonical key syntax: 000800:Plugin.esp. Optional field selects a top-level field to inspect large records. depth 1..16, maxItems 1..1000; truncation is marked explicitly.")]
    public CallToolResult Get(string session, string formKey, string? field = null, int depth = 8, int maxItems = 250) => Run(() =>
    {
        if (depth is < 1 or > 16 || maxItems is < 1 or > 1000) throw new ArgumentException("Invalid read limits.");
        var s = workspace.Get(session); var record = PluginWorkspace.Find(s, formKey, true);
        object? value = record;
        if (field is not null) value = (RecordCodec.Properties(record.GetType()).FirstOrDefault(p => p.Name.Equals(field, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Field missing.")).GetValue(record);
        return new { revision = s.Revision, record = RecordCodec.Encode(value, depth, maxItems) };
    });

    [McpServerTool(Name = "record_create", Destructive = false), Description("Create a concrete record from discovered schema. fields is an object; objects merge, arrays replace, null clears. FormLinks use canonical FormKeys. Cell without parent creates an interior cell. Nested records use parent FormKey and slot, e.g. PlacedObject under Cell.Temporary. Returns allocated FormKey and revision.")]
    public CallToolResult CreateRecord(string session, long expectedRevision, string type, string? editorId = null, JsonObject? fields = null, string? parent = null, string? slot = null) => Run(() => workspace.Mutate(session, expectedRevision, s => PluginWorkspace.Brief(PluginWorkspace.CreateRecord(s, type, editorId, fields ?? [], parent, slot))));

    [McpServerTool(Name = "record_update", Destructive = false), Description("Merge fields into an editable record atomically. Arrays replace in full, null clears optional values, and objects recursively merge. Read schema first. FormKey and nested major-record collections cannot be changed here. Create an override before editing a master record.")]
    public CallToolResult Update(string session, long expectedRevision, string formKey, JsonObject fields) => Run(() => workspace.Mutate(session, expectedRevision, s => { var r = PluginWorkspace.Find(s, formKey); RecordCodec.Apply(r, fields); return PluginWorkspace.Brief(r); }));

    [McpServerTool(Name = "record_override", Destructive = false), Description("Copy a master record into the editable plugin, preserving FormKey and source fields. Nested records use Mutagen contexts to preserve parent structure without copying siblings. Optional fields apply in the same transaction.")]
    public CallToolResult Override(string session, long expectedRevision, string formKey, JsonObject? fields = null) => Run(() => workspace.Mutate(session, expectedRevision, s => { var r = PluginWorkspace.CopyRecord(s, formKey, true, null); if (fields is not null) RecordCodec.Apply(r, fields); return PluginWorkspace.Brief(r); }));

    [McpServerTool(Name = "record_duplicate", Destructive = false), Description("Duplicate a leaf record with a new FormKey and unique EditorID, preserving source fields and parent placement. Links still point to original targets. Subtree duplication is refused because it needs an explicit child FormKey remapping plan. Optional fields apply atomically.")]
    public CallToolResult Duplicate(string session, long expectedRevision, string formKey, string editorId, JsonObject? fields = null) => Run(() => workspace.Mutate(session, expectedRevision, s => { var r = PluginWorkspace.CopyRecord(s, formKey, false, editorId); if (fields is not null) RecordCodec.Apply(r, fields); return PluginWorkspace.Brief(r); }));

    [McpServerTool(Name = "record_remove", Destructive = true), Description("Remove a record from the editable plugin (removing an override restores master behavior). Refuses if other editable records reference it or it owns children. Does not mark the master record deleted.")]
    public CallToolResult Remove(string session, long expectedRevision, string formKey) => Run(() => workspace.Mutate(session, expectedRevision, s => { PluginWorkspace.RemoveRecord(s, formKey); return new { removed = formKey }; }));

    [McpServerTool(Name = "record_references", ReadOnly = true), Description("List outgoing FormLinks or incoming references for a FormKey, optionally including masters. Paginated to bound response size.")]
    public CallToolResult References(string session, string formKey, bool incoming = false, bool includeMasters = false, int offset = 0, int limit = 100) => Run(() =>
    {
        AssetService.Page(offset, limit); var s = workspace.Get(session); var key = FormKey.Factory(formKey);
        if (!incoming)
        {
            var links = PluginWorkspace.Find(s, formKey, true).EnumerateFormLinks().Where(l => !l.IsNull).DistinctBy(l => (l.FormKey, l.Type)).ToArray();
            return new { total = links.Length, items = links.Skip(offset).Take(limit).Select(l => new { formKey = l.FormKey.ToString(), type = l.Type.Name }).ToArray() };
        }
        var records = includeMasters ? s.Dependencies.SelectMany(d => d.EnumerateMajorRecords()).Concat(s.Mod.EnumerateMajorRecords()) : s.Mod.EnumerateMajorRecords();
        var found = records.Where(r => r.EnumerateFormLinks().Any(l => l.FormKey == key)).Select(PluginWorkspace.Brief).ToArray();
        return (object)new { total = found.Length, items = found.Skip(offset).Take(limit).ToArray() };
    });

    [McpServerTool(Name = "plugin_batch", Destructive = false), Description("Apply up to 200 record operations atomically. Each object has op=create|update|override|duplicate|remove, plus type/editorId/fields/formKey/parent/slot as applicable. A create may set alias; later formKey, parent, or link strings may use $alias. All changes roll back on any failure. dryRun returns predicted results without committing.")]
    public CallToolResult Batch(string session, long expectedRevision, JsonArray operations, bool dryRun = false) => Run(() =>
    {
        if (operations.Count is < 1 or > 200) throw new ArgumentException("Batch must have 1..200 operations.");
        var s = workspace.Get(session); var before = s.Mod; var revision = s.Revision; var dirty = s.Dirty;
        try
        {
            return workspace.Mutate(session, expectedRevision, current =>
            {
                var aliases = new Dictionary<string, string>(); var results = new List<object>();
                JsonNode? Substitute(JsonNode? n) => n switch
                {
                    JsonValue v when v.TryGetValue<string>(out var str) && str.StartsWith('$') && !str.StartsWith("$type") => JsonValue.Create(aliases.GetValueOrDefault(str[1..]) ?? throw new ArgumentException($"Unknown alias {str}.")),
                    JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, p.Key == "$type" ? p.Value?.DeepClone() : Substitute(p.Value)))),
                    JsonArray a => new JsonArray(a.Select(Substitute).ToArray()), _ => n?.DeepClone()
                };
                foreach (var node in operations)
                {
                    var op = Substitute(node)?.AsObject() ?? throw new ArgumentException("Each operation must be an object.");
                    string Required(string key) => op[key]?.GetValue<string>() ?? throw new ArgumentException("Missing " + key);
                    var fields = op["fields"]?.AsObject() ?? []; IMajorRecordGetter? r;
                    switch (Required("op"))
                    {
                        case "create": r = PluginWorkspace.CreateRecord(current, Required("type"), op["editorId"]?.GetValue<string>(), fields, op["parent"]?.GetValue<string>(), op["slot"]?.GetValue<string>()); break;
                        case "update": r = PluginWorkspace.Find(current, Required("formKey")); RecordCodec.Apply(r, fields); break;
                        case "override": r = PluginWorkspace.CopyRecord(current, Required("formKey"), true, null); RecordCodec.Apply(r, fields); break;
                        case "duplicate": r = PluginWorkspace.CopyRecord(current, Required("formKey"), false, Required("editorId")); RecordCodec.Apply(r, fields); break;
                        case "remove": PluginWorkspace.RemoveRecord(current, Required("formKey")); results.Add(new { removed = Required("formKey") }); continue;
                        default: throw new ArgumentException("Unknown batch op.");
                    }
                    if (op["alias"] is { } alias && !aliases.TryAdd(alias.GetValue<string>(), r.FormKey.ToString())) throw new ArgumentException("Duplicate batch alias.");
                    results.Add(PluginWorkspace.Brief(r));
                }
                return new { dryRun, results, aliases };
            }, commit: !dryRun);
        }
        finally { if (dryRun) { s.Mod = before; s.Revision = revision; s.Dirty = dirty; } }
    });

    [McpServerTool(Name = "plugin_checkpoint", Destructive = false), Description("Store a named in-memory checkpoint (maximum 8 per session). Checkpoints are lost when the server exits. Use plugin_save for durable output.")]
    public CallToolResult Checkpoint(string session, string name) => Run(() => { var s = workspace.Get(session); if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || s.Checkpoints.Count >= 8 || s.Checkpoints.ContainsKey(name)) throw new ArgumentException("Checkpoint name must be new, nonempty, <=100 characters; maximum 8 checkpoints."); s.Checkpoints[name] = s.Mod.DeepCopy(); return new { checkpoint = name, revision = s.Revision }; });

    [McpServerTool(Name = "plugin_restore", Destructive = true), Description("Restore a named in-memory checkpoint; increments revision and marks the session dirty. Loaded masters remain available.")]
    public CallToolResult Restore(string session, long expectedRevision, string name) => Run(() => workspace.Mutate(session, expectedRevision, s => { s.Mod = (s.Checkpoints.GetValueOrDefault(name) ?? throw new ArgumentException("Unknown checkpoint.")).DeepCopy(); return new { restored = name }; }));

    [McpServerTool(Name = "plugin_diff", ReadOnly = true), Description("Compare current records with a named checkpoint. Reports added, removed, and changed FormKeys with bounded results. Header metadata is reported separately.")]
    public CallToolResult Diff(string session, string checkpoint, int offset = 0, int limit = 100) => Run(() =>
    {
        AssetService.Page(offset, limit); var s = workspace.Get(session); var before = s.Checkpoints.GetValueOrDefault(checkpoint) ?? throw new ArgumentException("Unknown checkpoint.");
        var a = before.EnumerateMajorRecords().ToDictionary(r => r.FormKey); var b = s.Mod.EnumerateMajorRecords().ToDictionary(r => r.FormKey);
        var changes = a.Keys.Union(b.Keys).Select(k => new { formKey = k.ToString(), change = !a.ContainsKey(k) ? "added" : !b.ContainsKey(k) ? "removed" : a[k].Equals(b[k]) ? "unchanged" : "changed" }).Where(x => x.change != "unchanged").ToArray();
        return new { revision = s.Revision, total = changes.Length, changes = changes.Skip(offset).Take(limit).ToArray(), beforeHeader = RecordCodec.Encode(before.ModHeader, 3), afterHeader = RecordCodec.Encode(s.Mod.ModHeader, 3) };
    });

    [McpServerTool(Name = "plugin_validate", ReadOnly = true), Description("Validate duplicate IDs, FormLinks and target types, deleted targets, light FormID range, and output support. Does not prove gameplay correctness or replace xEdit, Creation Kit, or in-game testing.")]
    public CallToolResult Validate(string session) => Run(() => workspace.Validate(workspace.Get(session)));

    [McpServerTool(Name = "plugin_save", Destructive = true), Description("Validate, write a staged binary, re-read it, then atomically save under the workspace. relativePath filename must match plugin ModKey. overwrite=true creates a backup. Refuses unresolved links and incompatible ESL ranges. Never edits load orders or deploys to game Data.")]
    public CallToolResult Save(string session, long expectedRevision, string relativePath, bool overwrite = false) => Run(() => workspace.Save(session, expectedRevision, relativePath, overwrite));
}
