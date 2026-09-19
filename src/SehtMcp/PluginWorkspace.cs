using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SehtMcp;

public sealed class PluginSession
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public required SkyrimMod Mod { get; set; }
    public long Revision { get; set; }
    public bool Dirty { get; set; }
    public string? SavedPath { get; set; }
    public string? SavedHash { get; set; }
    public List<ModKey> Masters { get; } = [];
    public List<ISkyrimModDisposableGetter> Dependencies { get; } = [];
    public Dictionary<string, SkyrimMod> Checkpoints { get; } = [];
    public object Summary() => new { session = Id, plugin = Mod.ModKey.ToString(), revision = Revision, dirty = Dirty, savedPath = SavedPath, isMaster = Mod.IsMaster, isLight = Mod.IsSmallMaster, records = Mod.EnumerateMajorRecords().Count(), masters = Masters.Select(x => x.ToString()).ToArray() };
}

public sealed record ValidationIssue(string Severity, string Code, string Message, string? FormKey = null);
public sealed record ValidationReport(bool Valid, int Records, int NewRecords, bool IsLight, IReadOnlyList<ValidationIssue> Issues);

public sealed class PluginWorkspace(SehtConfig config) : IDisposable
{
    // MCP may run tools concurrently. All session operations, including reads, share this gate.
    public object Gate { get; } = new();
    private readonly Dictionary<string, PluginSession> sessions = [];
    public PluginSession Get(string id) => sessions.GetValueOrDefault(id) ?? throw new ArgumentException("Unknown session. Use plugin_list or plugin_open.");
    public object[] List() => sessions.Values.Select(s => s.Summary()).ToArray();
    public static string TypeName(IMajorRecordGetter r) => r.GetType().Name.Replace("BinaryOverlay", "");
    public static object Brief(IMajorRecordGetter r) => new { formKey = r.FormKey.ToString(), editorId = r.EditorID, type = TypeName(r), deleted = r.IsDeleted };

    public PluginSession Create(string filename, string kind, string author, string description, string[] masters)
    {
        if (sessions.Count >= config.MaxSessions) throw new InvalidOperationException("Session limit reached. Close a session first.");
        CheckFilename(filename);
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (kind is not ("esp" or "esm" or "esl" or "esp-fe")) throw new ArgumentException("kind: esp, esm, esl, or esp-fe.");
        if (extension != (kind == "esp-fe" ? ".esp" : "." + kind)) throw new ArgumentException("Filename extension must match kind.");
        var s = new PluginSession { Mod = new SkyrimMod(ModKey.FromFileName(filename), SkyrimRelease.SkyrimSE), Dirty = true };
        s.Mod.IsMaster = kind is "esm" or "esl";
        s.Mod.IsSmallMaster = kind is "esl" or "esp-fe";
        s.Mod.ModHeader.Author = author;
        s.Mod.ModHeader.Description = description;
        // Conservative SE-compatible range. Do not silently renumber published forms.
        s.Mod.ModHeader.Stats.NextFormID = 0x800;
        try { LoadDependencies(s, masters); }
        catch { DisposeDependencies(s); throw; }
        sessions.Add(s.Id, s);
        return s;
    }

    public PluginSession Open(string path)
    {
        if (sessions.Count >= config.MaxSessions) throw new InvalidOperationException("Session limit reached.");
        path = Path.GetFullPath(path);
        CheckFilename(Path.GetFileName(path));
        var mod = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE);
        var s = new PluginSession { Mod = mod };
        try { LoadDependencies(s, mod.ModHeader.MasterReferences.Select(m => m.Master.ToString())); }
        catch { DisposeDependencies(s); throw; }
        // Sources are never written in place. Saving always targets the workspace.
        sessions.Add(s.Id, s);
        return s;
    }

    public void Close(string id, bool discard)
    {
        var s = Get(id);
        if (s.Dirty && !discard) throw new InvalidOperationException("Unsaved changes. Save first or set discard=true.");
        DisposeDependencies(s);
        sessions.Remove(id);
    }

    public string FindPlugin(string filename)
    {
        CheckFilename(filename);
        foreach (var root in config.DataRoots.Reverse())
        {
            var path = Path.Combine(root, filename);
            if (File.Exists(path)) return path;
        }
        var local = Path.Combine(config.Workspace, filename);
        if (File.Exists(local)) return local;
        throw new FileNotFoundException($"Master '{filename}' not found in configured dataRoots or workspace root.");
    }

    private void LoadDependencies(PluginSession s, IEnumerable<string> names)
    {
        var visiting = new HashSet<ModKey>();
        void Visit(string name)
        {
            CheckFilename(name);
            var key = ModKey.FromFileName(name);
            if (key == s.Mod.ModKey) throw new ArgumentException("Plugin cannot depend on itself.");
            if (s.Masters.Contains(key)) return;
            if (!visiting.Add(key)) throw new InvalidDataException("Circular master dependency.");
            if (visiting.Count + s.Masters.Count > 253) throw new InvalidDataException("Master limit exceeded.");
            var overlay = SkyrimMod.CreateFromBinaryOverlay(FindPlugin(name), SkyrimRelease.SkyrimSE);
            try
            {
                foreach (var master in overlay.ModHeader.MasterReferences) Visit(master.Master.ToString());
                s.Dependencies.Add(overlay);
                s.Masters.Add(key);
            }
            catch { overlay.Dispose(); throw; }
            visiting.Remove(key);
        }
        foreach (var name in names) Visit(name);
    }

    public object AddMasters(string id, long expectedRevision, string[] names)
    {
        var s = Get(id); CheckRevision(s, expectedRevision);
        var count = s.Dependencies.Count;
        try { LoadDependencies(s, names); }
        catch
        {
            foreach (var dep in s.Dependencies.Skip(count)) dep.Dispose();
            s.Dependencies.RemoveRange(count, s.Dependencies.Count - count);
            s.Masters.RemoveRange(count, s.Masters.Count - count);
            throw;
        }
        s.Revision++; s.Dirty = true;
        return s.Summary();
    }

    public static IMajorRecordGetter Find(PluginSession s, string key, bool dependencies = false)
    {
        var form = FormKey.Factory(key);
        var local = s.Mod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == form);
        if (local is not null) return local;
        if (dependencies)
            foreach (var dep in s.Dependencies.AsEnumerable().Reverse())
            {
                var match = dep.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == form);
                if (match is not null) return match;
            }
        throw new ArgumentException($"Record {key} not found{(dependencies ? " in loaded masters" : " in editable plugin; create an override first")}.");
    }

    public object Mutate(string id, long expectedRevision, Func<PluginSession, object?> action, bool commit = true)
    {
        var s = Get(id); CheckRevision(s, expectedRevision);
        var before = s.Mod;
        s.Mod = before.DeepCopy();
        try
        {
            var result = action(s);
            CheckIdentity(s);
            if (commit) { s.Revision++; s.Dirty = true; }
            else s.Mod = before;
            return new { session = id, revision = s.Revision, result };
        }
        catch { s.Mod = before; throw; }
    }

    public static IMajorRecord CreateRecord(PluginSession s, string type, string? editorId, JsonObject fields, string? parent = null, string? slot = null)
    {
        var t = RecordCodec.ResolveType(type);
        if (t.IsAbstract || !typeof(IMajorRecord).IsAssignableFrom(t)) throw new ArgumentException("Choose a concrete major record type from record_types.");
        if (s.Mod.IsSmallMaster && s.Mod.ModHeader.Stats.NextFormID > 0xFFF) throw new ArgumentException("Light plugin has exhausted the conservative 0x800..0xFFF range.");
        var record = (IMajorRecord)(System.Activator.CreateInstance(t, s.Mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) ?? throw new ArgumentException("Record cannot be constructed."));
        record.EditorID = editorId;
        RecordCodec.Apply(record, fields);
        if (record is Cell cell && parent is null)
        {
            cell.Flags |= Cell.Flag.IsInteriorCell;
            var blockNumber = (int)(cell.FormKey.ID % 10);
            var subNumber = (int)(cell.FormKey.ID / 10 % 10);
            var block = s.Mod.Cells.FirstOrDefault(b => b.BlockNumber == blockNumber);
            if (block is null) { block = new CellBlock { BlockNumber = blockNumber, GroupType = GroupTypeEnum.InteriorCellBlock }; s.Mod.Cells.Add(block); }
            var sub = block.SubBlocks.FirstOrDefault(b => b.BlockNumber == subNumber);
            if (sub is null) { sub = new CellSubBlock { BlockNumber = subNumber, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
            sub.Cells.Add(cell);
        }
        else if (parent is not null)
        {
            var owner = Find(s, parent);
            var p = owner.GetType().GetProperty(slot ?? "");
            var list = p?.GetValue(owner);
            var add = list?.GetType().GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsAssignableFrom(t));
            if (add is null) throw new ArgumentException("Parent slot must name a compatible child collection (e.g. Cell.Temporary or DialogTopic.Responses).");
            add.Invoke(list, [record]);
            if (record is IPlaced placed && owner is Cell) placed.MajorRecordFlagsRaw = slot == "Persistent" ? placed.MajorRecordFlagsRaw | 0x400 : placed.MajorRecordFlagsRaw & ~0x400;
        }
        else
        {
            var group = s.Mod.TryGetTopLevelGroup(t) ?? throw new ArgumentException("This record requires parent and slot.");
            group.AddUntyped(record);
        }
        return record;
    }

    public static IMajorRecord CopyRecord(PluginSession s, string source, bool asOverride, string? editorId)
    {
        var original = Find(s, source, true);
        var t = RecordCodec.ResolveType(TypeName(original));
        var group = s.Mod.TryGetTopLevelGroup(t);
        if (asOverride && s.Mod.EnumerateMajorRecords().Any(r => r.FormKey == original.FormKey)) throw new ArgumentException("Record is already editable; use record_update.");
        if (asOverride && original.FormKey.ModKey == s.Mod.ModKey) throw new ArgumentException("Cannot override a record owned by this plugin.");
        if (!asOverride && original.EnumerateMajorRecords().Any()) throw new ArgumentException("Subtree duplication requires an explicit FormKey remapping plan. Duplicate leaf records individually.");
        if (asOverride || group is null)
        {
            var mods = s.Dependencies.Cast<ISkyrimModGetter>().Append(s.Mod).ToArray();
            using var cache = mods.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
            foreach (var mod in mods.Reverse())
            {
                var context = mod.EnumerateMajorRecordContexts<ISkyrimMajorRecord, ISkyrimMajorRecordGetter>(cache).FirstOrDefault(c => c.Record.FormKey == original.FormKey);
                if (context is null) continue;
                return asOverride ? context.GetOrAddAsOverride(s.Mod) : context.DuplicateIntoAsNewRecord(s.Mod, editorId ?? throw new ArgumentException("A new editorId is required."));
            }
            throw new ArgumentException("Could not find source record context.");
        }
        // Mutagen copies all fields, including ones not exposed by RecordCodec.
        var copy = original.Duplicate(asOverride ? original.FormKey : s.Mod.GetNextFormKey());
        if (!asOverride) copy.EditorID = editorId ?? throw new ArgumentException("A new editorId is required for duplication.");
        group.AddUntyped(copy);
        return copy;
    }

    public static void RemoveRecord(PluginSession s, string key)
    {
        var record = Find(s, key);
        var refs = s.Mod.EnumerateMajorRecords().Where(r => r.FormKey != record.FormKey && r.EnumerateFormLinks().Any(l => l.FormKey == record.FormKey)).Select(r => r.FormKey.ToString()).Take(10).ToArray();
        if (refs.Length > 0) throw new ArgumentException("Record is referenced by: " + string.Join(", ", refs));
        if (record.EnumerateMajorRecords().Any()) throw new ArgumentException("Remove child records first.");
        s.Mod.Remove(record.FormKey);
    }

    public static void CheckRevision(PluginSession s, long expected)
    {
        if (s.Revision != expected) throw new InvalidOperationException($"Revision conflict: expected {expected}, actual {s.Revision}. Re-read the session before retrying.");
    }
    private static void CheckIdentity(PluginSession s)
    {
        var records = s.Mod.EnumerateMajorRecords().ToArray();
        if (s.Mod.IsSmallMaster && records.Any(r => r.FormKey.ModKey == s.Mod.ModKey && r.FormKey.ID is < 0x800 or > 0xFFF)) throw new ArgumentException("New light-plugin forms must stay in 0x800..0xFFF.");
        if (records.GroupBy(r => r.FormKey).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate FormKeys.");
        var duplicate = records.Where(r => !string.IsNullOrWhiteSpace(r.EditorID)).GroupBy(r => r.EditorID!, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Duplicate EditorID '{duplicate.Key}'.");
        foreach (var record in records)
            if (record.EditorID is { } edid && (edid.Length > 512 || edid.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))) throw new ArgumentException($"EditorID '{edid}' must use ASCII letters, digits, and underscores.");
    }

    public ValidationReport Validate(PluginSession s)
    {
        var issues = new List<ValidationIssue>();
        var records = s.Mod.EnumerateMajorRecords().ToArray();
        var own = records.Where(r => r.FormKey.ModKey == s.Mod.ModKey).ToArray();
        try { CheckIdentity(s); } catch (Exception e) { issues.Add(new("error", "identity", e.Message)); }
        if (s.Mod.UsingLocalization) issues.Add(new("error", "localized_output", "Localized output is not supported yet. Opened localized plugins may be inspected, but cannot be saved by this release."));
        if (s.Mod.IsSmallMaster && own.Any(r => r.FormKey.ID is < 0x800 or > 0xFFF)) issues.Add(new("error", "light_range", "New light-plugin FormIDs must be in 0x800..0xFFF. Existing forms are never silently compacted."));
        if (s.Mod.IsSmallMaster && own.Any(r => r is ICellGetter)) issues.Add(new("warning", "light_new_cells", "New cells in light plugins have engine limitations when another plugin overrides their contents. Consider a full plugin."));
        var lookup = new Dictionary<FormKey, IMajorRecordGetter>();
        foreach (var dep in s.Dependencies) foreach (var r in dep.EnumerateMajorRecords()) lookup[r.FormKey] = r;
        foreach (var r in records) lookup[r.FormKey] = r;
        foreach (var r in records)
        {
            foreach (var link in r.EnumerateFormLinks().Where(l => !l.IsNull).DistinctBy(l => (l.FormKey, l.Type)))
            {
                if (!lookup.TryGetValue(link.FormKey, out var target)) issues.Add(new("error", "unresolved_link", $"Missing target {link.FormKey} ({link.Type.Name}).", r.FormKey.ToString()));
                else if (!link.Type.IsInstanceOfType(target)) issues.Add(new("error", "link_type", $"{link.FormKey} is {TypeName(target)}, expected {link.Type.Name}.", r.FormKey.ToString()));
                else if (target.IsDeleted) issues.Add(new("warning", "deleted_target", $"Target {link.FormKey} is deleted.", r.FormKey.ToString()));
            }
            if (r.IsDeleted) issues.Add(new("warning", "deleted_record", "Deleted records can break dependent mods. Prefer disabling placed references where appropriate.", r.FormKey.ToString()));
        }
        return new(!issues.Any(i => i.Severity == "error"), records.Length, own.Length, s.Mod.IsSmallMaster, issues);
    }

    public object Save(string id, long revision, string relativePath, bool overwrite)
    {
        var s = Get(id); CheckRevision(s, revision);
        var path = config.OutputPath(relativePath);
        if (!Path.GetFileName(path).Equals(s.Mod.ModKey.ToString(), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output filename must match the session ModKey.");
        var report = Validate(s);
        if (!report.Valid) throw new InvalidDataException("Validation failed: " + string.Join("; ", report.Issues.Where(i => i.Severity == "error").Take(15).Select(i => i.Message)));
        if (File.Exists(path))
        {
            if (!overwrite) throw new IOException("File exists. Set overwrite=true to replace it with a backup.");
            if (s.SavedPath == path && s.SavedHash != HashFile(path)) throw new IOException("Output changed externally since the last save. Open it in a new session to reconcile changes.");
        }
        var staging = config.OutputPath(Path.Combine(".staging", Guid.NewGuid().ToString("N"), s.Mod.ModKey.ToString()));
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        try
        {
            s.Mod.BeginWrite.ToPath(staging).WithLoadOrder(s.Masters).WithDataFolder(config.Workspace).WithExtraIncludedMasters(s.Masters).Write();
            // A mutable import forces parsing of subrecords, unlike an overlay count.
            var check = SkyrimMod.CreateFromBinary(staging, SkyrimRelease.SkyrimSE);
            var reread = check.EnumerateMajorRecords().ToArray();
            var count = reread.Length;
            var expectedKeys = s.Mod.EnumerateMajorRecords().Select(r => r.FormKey).ToHashSet();
            if (count != report.Records || !expectedKeys.SetEquals(reread.Select(r => r.FormKey)) || check.IsSmallMaster != s.Mod.IsSmallMaster || check.IsMaster != s.Mod.IsMaster) throw new InvalidDataException("Binary round-trip validation failed.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string? backup = null;
            if (File.Exists(path))
            {
                backup = path + "." + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + ".bak";
                File.Replace(staging, path, backup);
            }
            else File.Move(staging, path);
            s.Dirty = false; s.SavedPath = path; s.SavedHash = HashFile(path);
            return new { session = id, revision = s.Revision, path, sha256 = s.SavedHash, backup, records = count, validation = report };
        }
        finally { if (File.Exists(staging)) File.Delete(staging); Directory.Delete(Path.GetDirectoryName(staging)!); }
    }

    public static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    public static void CheckFilename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !new[] { ".esp", ".esm", ".esl" }.Contains(Path.GetExtension(name).ToLowerInvariant())) throw new ArgumentException("Expected a plugin filename (.esp/.esm/.esl), without a path.");
    }
    private static void DisposeDependencies(PluginSession s) { foreach (var d in s.Dependencies) d.Dispose(); }
    public void Dispose() { foreach (var s in sessions.Values) DisposeDependencies(s); }
}
