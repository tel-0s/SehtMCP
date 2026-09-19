using System.Numerics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SehtMcp;

public static class NavmeshRecords
{
    public const uint PathingCell = 0xA5E9A03C;
    public const int PathingDoor = unchecked((int)0xE48B73F3);
    public static P3Float Point(Vector3 v) => new(v.X, v.Y, v.Z);
    public static Vector3 Point(P3Float v) => new(v.X, v.Y, v.Z);
    public static float[] Coordinates(P3Float v) => [v.X, v.Y, v.Z];

    public static object Write(PluginSession session, string cellKey, NavmeshGeometry geometry, bool replaceExisting)
    {
        var cell = PluginWorkspace.Find(session, cellKey) as Cell ?? throw new ArgumentException("Expected an editable interior Cell.");
        if (!cell.Flags.HasFlag(Cell.Flag.IsInteriorCell) || cell.FormKey.ModKey != session.Mod.ModKey)
            throw new ArgumentException("Generation currently supports new interior cells owned by this plugin. Existing master cells and exterior stitching require CK finalization.");
        var parts = geometry.Normalize().Components();
        if (cell.NavigationMeshes.Count > 0)
        {
            if (!replaceExisting) throw new ArgumentException("Cell already has navmeshes. Use replaceExisting=true to rebuild unlinked meshes while preserving their FormKeys.");
            if (parts.Length != cell.NavigationMeshes.Count) throw new ArgumentException("Rebuild changes the component count. Refusing to delete or reassign existing navmesh identities; restore a checkpoint before initial generation instead.");
            if (cell.NavigationMeshes.Any(n => n.FormKey.ModKey != session.Mod.ModKey || n.Data is null || n.Data.EdgeLinks.Count > 0 || n.Data.DoorTriangles.Count > 0))
                throw new ArgumentException("Cannot rebuild inherited or linked navmeshes; existing triangle links would become invalid.");
            var keys = cell.NavigationMeshes.Select(n => n.FormKey).ToHashSet();
            if (session.Mod.EnumerateMajorRecords().OfType<INavigationMeshGetter>().Any(n => !keys.Contains(n.FormKey) && n.Data?.EdgeLinks.Any(e => keys.Contains(e.Mesh.FormKey)) == true))
                throw new ArgumentException("Other navmeshes link into this cell. Refusing to invalidate incoming triangle links.");
        }
        var map = session.Mod.NavigationMeshInfoMaps.FirstOrDefault(m => m.FormKey.ModKey == session.Mod.ModKey && !m.IsDeleted);
        var needed = (cell.NavigationMeshes.Count == 0 ? parts.Length : 0) + (map is null ? 1 : 0);
        if (session.Mod.IsSmallMaster && (long)session.Mod.ModHeader.Stats.NextFormID + needed - 1 > 0xFFF)
            throw new ArgumentException("Insufficient light-plugin FormIDs for NAVM and NAVI records.");
        if (map is null)
        {
            map = new NavigationMeshInfoMap(session.Mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { NavMeshVersion = 12 };
            session.Mod.NavigationMeshInfoMaps.Add(map);
        }
        var results = new List<object>();
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            NavigationMesh nav;
            if (replaceExisting && cell.NavigationMeshes.Count == parts.Length) nav = cell.NavigationMeshes[i];
            else { nav = new NavigationMesh(session.Mod.GetNextFormKey(), SkyrimRelease.SkyrimSE); cell.NavigationMeshes.Add(nav); }
            nav.Data = Data(cell.FormKey, part);
            nav.IsCompressed = true;
            // Explicitly rebuild derived NAVI information for this mesh, preserving other cells.
            foreach (var otherMap in session.Mod.NavigationMeshInfoMaps)
                for (var j = otherMap.MapInfos.Count - 1; j >= 0; j--)
                    if (otherMap.MapInfos[j].NavigationMesh.FormKey == nav.FormKey) otherMap.MapInfos.RemoveAt(j);
            map.MapInfos.Add(Info(nav));
            results.Add(new { formKey = nav.FormKey.ToString(), vertices = part.Vertices.Length, triangles = part.Triangles.Length,
                boundaryEdges = part.Adjacency().Sum(t => t.Count(n => n < 0)), bounds = new { min = Coordinates(nav.Data.Min), max = Coordinates(nav.Data.Max) } });
        }
        return new { cell = cellKey, navmeshInfoMap = map.FormKey.ToString(), components = results, warnings = new[] {
            "Generated from supplied geometry. Inspect the result against collision and test actor pathing in game.",
            "Teleport-door links are explicit: use navmesh_link_door for each endpoint. Cover and exterior edge links are not generated."
        } };
    }

    public static NavigationMeshData Data(FormKey cell, NavmeshGeometry geometry)
    {
        var neighbors = geometry.Adjacency();
        var min = geometry.Vertices.Aggregate(Vector3.Min); var max = geometry.Vertices.Aggregate(Vector3.Max);
        var data = new NavigationMeshData { NavmeshVersion = 12, CrcHash = PathingCell,
            Parent = new CellNavmeshParent { Parent = cell.ToLink<ICellGetter>() },
            Min = Point(min), Max = Point(max), NavmeshGridDivisor = 1,
            MaxDistanceX = max.X - min.X, MaxDistanceY = max.Y - min.Y };
        data.Vertices.AddRange(geometry.Vertices.Select(Point));
        for (var i = 0; i < geometry.Triangles.Length; i++)
        {
            var t = geometry.Triangles[i]; var n = neighbors[i];
            data.Triangles.Add(new NavmeshTriangle { Vertices = new P3Int16(checked((short)t[0]), checked((short)t[1]), checked((short)t[2])),
                EdgeLink_0_1 = checked((short)n[0]), EdgeLink_1_2 = checked((short)n[1]), EdgeLink_2_0 = checked((short)n[2]) });
        }
        // NVNM's divisor^2 cells each store a uint32 count followed by int16 triangle indices.
        // One exhaustive bucket is valid for interiors and avoids approximating triangle/grid intersections.
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
        {
            writer.Write(data.Triangles.Count);
            for (var i = 0; i < data.Triangles.Count; i++) writer.Write(checked((short)i));
        }
        data.NavmeshGrid = bytes.ToArray();
        return data;
    }

    public static NavigationMapInfo Info(INavigationMeshGetter nav)
    {
        var data = nav.Data ?? throw new ArgumentException("Navmesh has no geometry.");
        var parent = data.Parent as ICellNavmeshParentGetter ?? throw new ArgumentException("Expected an interior navmesh.");
        var center = data.Vertices.Aggregate(Vector3.Zero, (total, v) => total + Point(v)) / data.Vertices.Count;
        var info = new NavigationMapInfo { NavigationMesh = nav.FormKey.ToLink<INavigationMeshGetter>(), Point = Point(center),
            Unknown2 = unchecked((int)PathingCell), Parent = new NavigationMapInfoCellParent { ParentCell = parent.Parent.FormKey.ToLink<ICellGetter>() } };
        info.MergedTo.AddRange(data.EdgeLinks.Select(e => e.Mesh.FormKey).Distinct().Select(f => f.ToLink<INavigationMeshGetter>()));
        info.LinkedDoors.AddRange(data.DoorTriangles.Select(d => new LinkedDoor { Door = d.Door.FormKey.ToLink<IPlacedObjectGetter>(), Unknown = PathingDoor }));
        if (data.EdgeLinks.Count == 0 && data.DoorTriangles.Count == 0)
        {
            info.Unknown = 0x20; // xEdit: Is Island.
            info.Island = new IslandData { Min = data.Min, Max = data.Max };
            info.Island.Vertices.AddRange(data.Vertices);
            info.Island.Triangles.AddRange(data.Triangles.Select(t => t.Vertices));
        }
        return info;
    }

    public static IEnumerable<ValidationIssue> Validate(PluginSession session)
    {
        var maps = session.Mod.NavigationMeshInfoMaps.SelectMany(m => m.MapInfos).ToArray();
        foreach (var nav in session.Mod.EnumerateMajorRecords().OfType<INavigationMeshGetter>().Where(n => !n.IsDeleted))
        {
            var issues = new List<ValidationIssue>(); var key = nav.FormKey.ToString();
            void Error(string code, string message) => issues.Add(new("error", "navmesh_" + code, message, key));
            try
            {
                var data = nav.Data;
                if (data is null) { Error("data", "NAVM has no NVNM geometry."); }
                else
                {
                    if (data.Vertices.Count is < 3 or > 32767 || data.Triangles.Count is < 1 or > 32767) Error("size", "NAVM vertex/triangle counts must be nonzero and fit supported signed indices.");
                    if (data.NavmeshVersion != 12) issues.Add(new("warning", "navmesh_version", "Skyrim generation uses NVNM version 12.", key));
                    var vertices = data.Vertices.Select(Point).ToArray();
                    if (vertices.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))) Error("vertex", "Non-finite NAVM vertex.");
                    for (var i = 0; i < data.Triangles.Count; i++)
                    {
                        var triangle = data.Triangles[i]; var v = new[] { (int)triangle.Vertices.X, triangle.Vertices.Y, triangle.Vertices.Z };
                        var edges = new[] { triangle.EdgeLink_0_1, triangle.EdgeLink_1_2, triangle.EdgeLink_2_0 };
                        if (v.Any(x => x < 0 || x >= vertices.Length)) { Error("index", $"Triangle {i}: vertex index out of range."); continue; }
                        if (Vector3.Cross(vertices[v[1]] - vertices[v[0]], vertices[v[2]] - vertices[v[0]]).Z <= 0.0001f) Error("winding", $"Triangle {i}: degenerate, vertical, or downward winding.");
                        for (var edge = 0; edge < 3; edge++)
                        {
                            var target = edges[edge]; var external = ((int)triangle.Flags & (1 << edge)) != 0;
                            if (external) { if (target < 0 || target >= data.EdgeLinks.Count) Error("edge", $"Triangle {i}: external edge index out of range."); continue; }
                            if (target == -1) continue;
                            if (target < 0 || target >= data.Triangles.Count || target == i) { Error("edge", $"Triangle {i}: neighbor index out of range or self-link."); continue; }
                            var other = data.Triangles[target]; var ov = new[] { (int)other.Vertices.X, other.Vertices.Y, other.Vertices.Z };
                            var oe = new[] { other.EdgeLink_0_1, other.EdgeLink_1_2, other.EdgeLink_2_0 };
                            if (!Enumerable.Range(0, 3).Any(e => ov[e] == v[(edge + 1) % 3] && ov[(e + 1) % 3] == v[edge] && oe[e] == i && ((int)other.Flags & (1 << e)) == 0))
                                Error("adjacency", $"Triangle {i}: neighbor {target} does not reciprocate the shared edge.");
                        }
                    }
                    foreach (var door in data.DoorTriangles)
                    {
                        if (door.TriangleBeforeDoor < 0 || door.TriangleBeforeDoor >= data.Triangles.Count) Error("door", "Door triangle index out of range.");
                        else if (!data.Triangles[door.TriangleBeforeDoor].Flags.HasFlag(NavmeshTriangle.Flag.Door)) Error("door", "Door-linked triangle lacks its Door flag.");
                        if (door.Unknown != PathingDoor) Error("door", "Door link has an invalid PathingDoor CRC.");
                    }
                    if (data.Parent is ICellNavmeshParentGetter parent)
                    {
                        var cell = PluginWorkspace.Find(session, parent.Parent.FormKey.ToString(), true) as ICellGetter;
                        if (cell is null || !cell.Flags.HasFlag(Cell.Flag.IsInteriorCell) || !cell.NavigationMeshes.Any(n => n.FormKey == nav.FormKey)) Error("parent", "Interior NAVM parent does not match its owning cell.");
                    }
                    if (data.NavmeshGridDivisor is < 1 or > 256) Error("grid", "Navmesh grid divisor must be 1..256.");
                    else
                    {
                        using var reader = new BinaryReader(new MemoryStream(data.NavmeshGrid.ToArray()));
                        var found = new HashSet<int>();
                        for (var bucket = 0; bucket < data.NavmeshGridDivisor * data.NavmeshGridDivisor; bucket++)
                        {
                            var count = reader.ReadUInt32();
                            if (count > data.Triangles.Count) throw new InvalidDataException("Navmesh grid bucket count exceeds triangle count.");
                            for (var j = 0; j < count; j++) { var index = reader.ReadInt16(); if (index < 0 || index >= data.Triangles.Count) Error("grid", "Grid triangle index out of range."); else found.Add(index); }
                        }
                        if (reader.BaseStream.Position != reader.BaseStream.Length || found.Count != data.Triangles.Count) Error("grid", "Grid has trailing bytes or omits triangles.");
                    }
                    var min = Point(data.Min); var max = Point(data.Max);
                    if (new[] { min.X, min.Y, min.Z, max.X, max.Y, max.Z }.Any(v => !float.IsFinite(v))) Error("bounds", "Non-finite NAVM bounds.");
                    if (vertices.Any(v => v.X < min.X - .01f || v.Y < min.Y - .01f || v.Z < min.Z - .01f || v.X > max.X + .01f || v.Y > max.Y + .01f || v.Z > max.Z + .01f)) Error("bounds", "NAVM bounds do not contain its vertices.");
                    if (!float.IsFinite(data.MaxDistanceX) || !float.IsFinite(data.MaxDistanceY) || data.MaxDistanceX <= 0 || data.MaxDistanceY <= 0 ||
                        Math.Abs(data.MaxDistanceX * data.NavmeshGridDivisor - (max.X - min.X)) > Math.Max(.1f, (max.X - min.X) * .001f) ||
                        Math.Abs(data.MaxDistanceY * data.NavmeshGridDivisor - (max.Y - min.Y)) > Math.Max(.1f, (max.Y - min.Y) * .001f)) Error("grid", "Grid cell dimensions do not match the bounds/divisor.");
                    var infos = maps.Where(m => m.NavigationMesh.FormKey == nav.FormKey).ToArray();
                    if (infos.Length != 1) Error("navi", "Expected exactly one local NAVI entry for this NAVM.");
                    else
                    {
                        var info = infos[0];
                        if (info.Unknown2 != unchecked((int)PathingCell)) Error("navi", "NAVI entry has an invalid PathingCell CRC.");
                        if (data.Parent is ICellNavmeshParentGetter p && (info.Parent is not INavigationMapInfoCellParentGetter ip || ip.ParentCell.FormKey != p.Parent.FormKey)) Error("navi", "NAVI parent differs from NAVM parent.");
                        if (!info.LinkedDoors.Select(d => d.Door.FormKey).ToHashSet().SetEquals(data.DoorTriangles.Select(d => d.Door.FormKey))) Error("navi", "NAVI door links differ from NAVM door links.");
                        if (info.LinkedDoors.Any(d => d.Unknown != PathingDoor)) Error("navi", "NAVI door link has an invalid PathingDoor CRC.");
                        if (info.Island is { } island && (!island.Vertices.SequenceEqual(data.Vertices) || !island.Triangles.SequenceEqual(data.Triangles.Select(t => t.Vertices)) || island.Min != data.Min || island.Max != data.Max))
                            Error("navi", "NAVI island geometry is stale relative to NAVM.");
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or EndOfStreamException or OverflowException)
            { Error("malformed", ex.Message); }
            foreach (var issue in issues.Take(100)) yield return issue;
        }
    }
}
