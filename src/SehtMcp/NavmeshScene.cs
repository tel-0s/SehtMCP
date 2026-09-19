using System.Numerics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace SehtMcp;

public sealed record NavmeshScene(NavmeshGeometry Geometry, object[] Sources, object[] Excluded, int DegenerateTrianglesRemoved)
{
    public static Matrix4x4 PlacementTransform(Vector3 position, Vector3 rotation, float scale)
    {
        // Skyrim NiMatrix3::SetEulerAnglesXYZ, transposed for System.Numerics row vectors.
        // The editor angles are clockwise: apply Z, then Y, then X to model vertices.
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationZ(-rotation.Z) * Matrix4x4.CreateRotationY(-rotation.Y) *
               Matrix4x4.CreateRotationX(-rotation.X) * Matrix4x4.CreateTranslation(position);
    }

    public static NavmeshScene Collect(PluginSession session, string cellKey, AssetService assets, string[] archives, string[]? references)
    {
        if (archives.Length > 64) throw new ArgumentException("At most 64 explicitly ordered archives are supported.");
        var cell = PluginWorkspace.Find(session, cellKey) as ICellGetter ?? throw new ArgumentException("Expected editable Cell.");
        if (!cell.Flags.HasFlag(Cell.Flag.IsInteriorCell)) throw new ArgumentException("Cell geometry baking currently supports interiors.");
        var selected = references?.Select(r => FormKey.Factory(r)).ToHashSet();
        if (selected?.Count == 0) throw new ArgumentException("An explicit reference selection cannot be empty.");
        var found = new HashSet<FormKey>();
        var points = new List<Vector3>(); var faces = new List<int[]>(); var sources = new List<object>(); var excluded = new List<object>();
        var models = new Dictionary<string, (NifDocument Nif, string? Archive)>(StringComparer.OrdinalIgnoreCase);
        var degenerate = 0;
        var placedObjects = cell.Temporary.Concat(cell.Persistent).OfType<IPlacedObjectGetter>().Where(p => selected is null || selected.Contains(p.FormKey)).ToArray();
        var wantedBases = placedObjects.Select(p => p.Base.FormKey).ToHashSet();
        var bases = new Dictionary<FormKey, Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter>();
        // Resolve the selected bases in one load-order pass, not a full master scan per REFR.
        foreach (var mod in session.Dependencies.Cast<ISkyrimModGetter>().Append(session.Mod))
            foreach (var record in mod.EnumerateMajorRecords())
                if (wantedBases.Contains(record.FormKey)) bases[record.FormKey] = record;
        foreach (var placed in placedObjects)
        {
            if (selected is not null && !selected.Contains(placed.FormKey)) continue;
            found.Add(placed.FormKey);
            var baseRecord = bases.GetValueOrDefault(placed.Base.FormKey) ?? throw new ArgumentException($"Missing base {placed.Base.FormKey} for {placed.FormKey}.");
            var reason = placed.IsDeleted || (placed.MajorRecordFlagsRaw & 0x800) != 0 ? "deleted or initially disabled" :
                placed.EnableParent is not null ? "conditional enable parent" :
                selected is null && baseRecord is not IStaticGetter ? "default collection includes Static bases only" :
                baseRecord is IDoorGetter ? "doors need explicit navigation links; closed door geometry would obstruct traversal" : null;
            if (reason is not null)
            {
                if (selected is not null) throw new ArgumentException($"Selected reference {placed.FormKey}: {reason}.");
                excluded.Add(new { reference = placed.FormKey.ToString(), reason }); continue;
            }
            var model = baseRecord.GetType().GetProperty("Model")?.GetValue(baseRecord) as IModelGetter;
            var path = model?.File.ToString().Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"Reference {placed.FormKey} ({baseRecord.EditorID}) has no model. Select architectural references explicitly.");
            if (!path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) path = "meshes/" + path;
            path = AssetService.Normalize(path);
            if (!models.TryGetValue(path, out var asset))
            {
                if (models.Count >= 256) throw new ArgumentException("Cell geometry exceeds 256 distinct models. Select a smaller set of references.");
                byte[]? bytes = null; string? sourceArchive = null;
                try { bytes = assets.Read(path); }
                catch (FileNotFoundException)
                {
                    foreach (var archive in archives.Reverse())
                    {
                        try { bytes = assets.Read(path, archive); sourceArchive = archive; break; }
                        catch (FileNotFoundException) { }
                    }
                }
                if (bytes is null) throw new FileNotFoundException($"Model {path} for {placed.FormKey} was not found. Supply archive paths in low-to-high priority order for packed assets.");
                var nif = new NifDocument(bytes);
                if (nif.Meshes.Count == 0 || nif.Warnings.Count > 0 || nif.Meshes.Any(m => m.Skin >= 0) ||
                    nif.Blocks.Any(b => b.Type.Contains("Controller") || b.Type is "NiSwitchNode" or "NiBillboardNode" or "BSLeafAnimNode" or "BSDynamicTriShape"))
                    throw new ArgumentException($"Model {path} has unsupported, animated, or incomplete render geometry; use explicit collision/proxy triangles with navmesh_generate. {string.Join("; ", nif.Warnings.Take(4))}");
                asset = (nif, sourceArchive); models.Add(path, asset);
            }
            if (placed.Placement is null) throw new ArgumentException($"Reference {placed.FormKey} has no placement transform.");
            var position = NavmeshRecords.Point(placed.Placement.Position); var rotation = NavmeshRecords.Point(placed.Placement.Rotation); var scale = placed.Scale ?? 1;
            if (new[] { position.X, position.Y, position.Z, rotation.X, rotation.Y, rotation.Z, scale }.Any(x => !float.IsFinite(x)) || scale <= 0)
                throw new ArgumentException($"Reference {placed.FormKey} has an invalid transform.");
            var matrix = PlacementTransform(position, rotation, scale);
            var count = 0;
            foreach (var mesh in asset.Nif.Meshes)
            {
                if (points.Count + mesh.Vertices.Length > 250000 || faces.Count + mesh.Indices.Length / 3 > 250000) throw new ArgumentException("Cell geometry exceeds 250000 vertices or triangles. Select a smaller region or supply simplified collision/proxy geometry.");
                var start = points.Count; points.AddRange(mesh.Vertices.Select(v => Vector3.Transform(v, matrix)));
                for (var i = 0; i < mesh.Indices.Length; i += 3)
                {
                    var a = start + mesh.Indices[i]; var b = start + mesh.Indices[i + 1]; var c = start + mesh.Indices[i + 2];
                    if (Vector3.Cross(points[b] - points[a], points[c] - points[a]).LengthSquared() < .000001f) { degenerate++; continue; }
                    faces.Add([a, b, c]); count++;
                }
            }
            sources.Add(new { reference = placed.FormKey.ToString(), baseFormKey = placed.Base.FormKey.ToString(), model = path, archive = asset.Archive, triangles = count });
        }
        if (selected is not null && !selected.IsSubsetOf(found)) throw new ArgumentException("A selected reference is not a placed object in the target cell.");
        if (sources.Count == 0) throw new ArgumentException("No enabled static geometry found. Select references or use navmesh_generate with explicit scene triangles.");
        var geometry = NavmeshGeometry.Parse(points.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(), faces.ToArray());
        return new(geometry, sources.ToArray(), excluded.ToArray(), degenerate);
    }
}
