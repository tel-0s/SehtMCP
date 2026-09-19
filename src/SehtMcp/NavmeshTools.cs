using System.ComponentModel;
using System.Numerics;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace SehtMcp;

[McpServerToolType]
public sealed class NavmeshTools(PluginWorkspace workspace, AssetService assets)
{
    [McpServerTool(Name = "navmesh_preview", ReadOnly = true), Description("Return a PNG of the cell's navmesh triangles with visible triangle edges and a different shade per NAVM. Angles are degrees; pitch=90 gives a top view. Displays navigation surfaces only, without scene collision or runtime actors. Use after generation to inspect disconnected islands and floor coverage.")]
    public CallToolResult Preview(string session, string cell, int width = 640, int height = 640, float yaw = 35, float pitch = 45)
    {
        lock (workspace.Gate)
        {
            try
            {
                var target = PluginWorkspace.Find(workspace.Get(session), cell, true) as ICellGetter ?? throw new ArgumentException("Expected Cell.");
                var meshes = target.NavigationMeshes.Where(n => !n.IsDeleted && n.Data is not null).Select((nav, i) => new NifMesh(i, nav.FormKey.ToString(), nav.Data!.Vertices.Select(NavmeshRecords.Point).ToArray(),
                    nav.Data.Triangles.Where(t => !t.Flags.HasFlag(NavmeshTriangle.Flag.Deleted)).SelectMany(t => new[] { checked((ushort)t.Vertices.X), checked((ushort)t.Vertices.Y), checked((ushort)t.Vertices.Z) }).ToArray(), -1, -1)).ToArray();
                if (meshes.Any(m => m.Indices.Any(index => index >= m.Vertices.Length))) throw new InvalidDataException("Invalid navmesh vertex index. Run plugin_validate.");
                var png = MeshPreview.Render(meshes, width, height, yaw, pitch, true);
                var result = ToolResult.Ok(new { cell, navmeshes = meshes.Length, triangles = meshes.Sum(m => m.Indices.Length / 3), width, height });
                result.Content.Add(ImageContentBlock.FromBytes(png, "image/png")); return result;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return ToolResult.Error(ex); }
        }
    }

    [McpServerTool(Name = "navmesh_nearest", ReadOnly = true), Description("Find the closest triangle surface to a Skyrim-space point in a cell's navmeshes. Returns the NAVM FormKey, triangle index, snapped point and 3D distance, or fails if no surface is within maxDistance. Useful for matching a teleport arrival marker to navmesh_link_door. Does not establish walkability or line of sight between the supplied point and the mesh.")]
    public CallToolResult Nearest(string session, string cell, float x, float y, float z, float maxDistance = 64)
    {
        lock (workspace.Gate) return ToolResult.Run(() =>
        {
            if (new[] { x, y, z, maxDistance }.Any(v => !float.IsFinite(v)) || maxDistance is <= 0 or > 4096) throw new ArgumentException("Coordinates must be finite; maxDistance must be >0 and <=4096.");
            var target = PluginWorkspace.Find(workspace.Get(session), cell, true) as ICellGetter ?? throw new ArgumentException("Expected Cell.");
            var point = new Vector3(x, y, z); var best = maxDistance * maxDistance; object? result = null;
            foreach (var nav in target.NavigationMeshes.Where(n => !n.IsDeleted && n.Data is not null))
            {
                var data = nav.Data!;
                for (var i = 0; i < data.Triangles.Count; i++)
                {
                    var t = data.Triangles[i]; if (t.Flags.HasFlag(NavmeshTriangle.Flag.Deleted)) continue;
                    var indices = new[] { (int)t.Vertices.X, t.Vertices.Y, t.Vertices.Z };
                    if (indices.Any(index => index < 0 || index >= data.Vertices.Count)) throw new InvalidDataException("Navmesh has invalid vertex indices; run plugin_validate.");
                    var closest = NavmeshGeometry.ClosestPoint(point, NavmeshRecords.Point(data.Vertices[indices[0]]), NavmeshRecords.Point(data.Vertices[indices[1]]), NavmeshRecords.Point(data.Vertices[indices[2]]));
                    var distance = Vector3.DistanceSquared(point, closest);
                    if (distance <= best) { best = distance; result = new { navmesh = nav.FormKey.ToString(), triangle = i, point = new[] { closest.X, closest.Y, closest.Z }, distance = MathF.Sqrt(distance) }; }
                }
            }
            return result ?? throw new ArgumentException("No triangle surface is within maxDistance.");
        });
    }

    [McpServerTool(Name = "navmesh_generate_from_cell", Destructive = false), Description("Collect placed NIF render geometry in a new interior cell and bake NAVM/NAVI with Recast. Defaults to enabled Static references; references can explicitly select architecture/obstacles. Applies NIF node and REFR transforms. Loose files win; archives are explicit BSA paths in low-to-high priority order. Missing/unsupported/animated selected geometry fails the whole operation. Render geometry can differ from Havok collision: review the result or supply collision/proxy triangles to navmesh_generate. Does not process dynamic doors, actors, or conditional enable states. dryRun leaves the session unchanged and reports geometry sources/exclusions.")]
    public CallToolResult GenerateFromCell(string session, long expectedRevision, string cell, string[]? archives = null, string[]? references = null, NavmeshBuildSettings? settings = null, float[][]? walkableSeeds = null, bool replaceExisting = false, bool dryRun = false)
    {
        lock (workspace.Gate) return ToolResult.Run(() => workspace.Mutate(session, expectedRevision, s =>
        {
            var scene = NavmeshScene.Collect(s, cell, assets, archives ?? [], references);
            var result = NavmeshRecords.Write(s, cell, scene.Geometry.Bake(settings ?? new(), walkableSeeds), replaceExisting);
            return new { geometrySource = "NIF render geometry (not Havok collision)", sources = scene.Sources, excluded = scene.Excluded,
                inputVertices = scene.Geometry.Vertices.Length, inputTriangles = scene.Geometry.Triangles.Length, degenerateTrianglesRemoved = scene.DegenerateTrianglesRemoved, generation = result };
        }, !dryRun));
    }

    [McpServerTool(Name = "navmesh_create", Destructive = false), Description("Create NAVM/NAVI records for a new interior cell from explicitly authored walkable triangles. vertices are [x,y,z] in Skyrim cell coordinates; triangles are zero-based [a,b,c]. Welds identical positions, orients upward, builds reciprocal adjacency and spatial lookup, and separates disconnected components. Does not infer obstacles or actor clearance: use navmesh_generate for that. Atomic; plugin_save persists the result. Rebuilding linked meshes or changing component count is refused.")]
    public CallToolResult Create(string session, long expectedRevision, string cell, float[][] vertices, int[][] triangles, bool replaceExisting = false, bool dryRun = false)
    {
        lock (workspace.Gate) return ToolResult.Run(() => workspace.Mutate(session, expectedRevision,
            s => NavmeshRecords.Write(s, cell, NavmeshGeometry.Parse(vertices, triangles), replaceExisting), !dryRun));
    }

    [McpServerTool(Name = "navmesh_generate", Destructive = false), Description("Bake a new interior cell's scene triangle geometry with Recast into Skyrim NAVM/NAVI records. vertices [x,y,z], triangles zero-based [a,b,c], Z-up Skyrim units. Floor winding must face upward; include walls, ceilings, stairs and obstacles to enforce clearance. Settings control actor height/radius, climb, slope and voxel resolution. Returns one NAVM per connected component; all records commit atomically. Geometry is supplied explicitly; this tool does not read Havok collision or the running CK. dryRun leaves the session unchanged.")]
    public CallToolResult Generate(string session, long expectedRevision, string cell, float[][] vertices, int[][] triangles, NavmeshBuildSettings? settings = null, float[][]? walkableSeeds = null, bool replaceExisting = false, bool dryRun = false)
    {
        lock (workspace.Gate) return ToolResult.Run(() => workspace.Mutate(session, expectedRevision,
            s => NavmeshRecords.Write(s, cell, NavmeshGeometry.Parse(vertices, triangles).Bake(settings ?? new(), walkableSeeds), replaceExisting), !dryRun));
    }

    [McpServerTool(Name = "navmesh_get", ReadOnly = true), Description("Inspect a NAVM's parent, bounds, vertex and triangle counts, paginated geometry, adjacency, edge links and door links. offset/limit page both vertices and triangles independently (limit 1..500); indices are absolute zero-based indices. Use this to choose an explicit door triangle or review generated topology.")]
    public CallToolResult Get(string session, string navmesh, int offset = 0, int limit = 100)
    {
        lock (workspace.Gate) return ToolResult.Run(() =>
        {
            AssetService.Page(offset, limit);
            var nav = PluginWorkspace.Find(workspace.Get(session), navmesh, true) as INavigationMeshGetter ?? throw new ArgumentException("Expected a NavigationMesh FormKey.");
            var data = nav.Data ?? throw new ArgumentException("NAVM has no geometry.");
            return new { formKey = navmesh, parent = RecordCodec.Encode(data.Parent), bounds = new { min = NavmeshRecords.Coordinates(data.Min), max = NavmeshRecords.Coordinates(data.Max) },
                vertexCount = data.Vertices.Count, triangleCount = data.Triangles.Count, offset,
                vertices = data.Vertices.Skip(offset).Take(limit).Select((v, i) => new { index = offset + i, position = new[] { v.X, v.Y, v.Z } }).ToArray(),
                triangles = data.Triangles.Skip(offset).Take(limit).Select((t, i) => new { index = offset + i, vertices = new[] { t.Vertices.X, t.Vertices.Y, t.Vertices.Z }, neighbors = new[] { t.EdgeLink_0_1, t.EdgeLink_1_2, t.EdgeLink_2_0 }, flags = t.Flags.ToString() }).ToArray(),
                doorLinks = RecordCodec.Encode(data.DoorTriangles), edgeLinks = RecordCodec.Encode(data.EdgeLinks) };
        });
    }

    [McpServerTool(Name = "navmesh_link_door", Destructive = false), Description("Associate a persistent teleport-door REFR in this new interior cell with an explicit NAVM triangle. Writes PathingDoor CRC, NAVM Door flag/link, and synchronized NAVI links. Choose a triangle at the door's arrival marker using navmesh_get. Both destination endpoints need valid navmeshes and links; this does not edit teleport destinations, finalize exterior navmeshes, or verify runtime pathing. Refuses moving an existing door link silently.")]
    public CallToolResult LinkDoor(string session, long expectedRevision, string navmesh, string door, int triangle)
    {
        lock (workspace.Gate) return ToolResult.Run(() => workspace.Mutate(session, expectedRevision, s =>
        {
            var nav = PluginWorkspace.Find(s, navmesh) as NavigationMesh ?? throw new ArgumentException("Expected editable NAVM.");
            if (nav.FormKey.ModKey != s.Mod.ModKey || nav.Data?.Parent is not CellNavmeshParent parent) throw new ArgumentException("Expected a new interior NAVM owned by this plugin.");
            var data = nav.Data;
            if (triangle < 0 || triangle >= data.Triangles.Count) throw new ArgumentException("Triangle index out of range.");
            var cell = PluginWorkspace.Find(s, parent.Parent.FormKey.ToString()) as Cell ?? throw new ArgumentException("Missing editable cell.");
            var reference = cell.Persistent.OfType<PlacedObject>().FirstOrDefault(r => r.FormKey == FormKey.Factory(door)) ?? throw new ArgumentException("Door must be a persistent PlacedObject in this cell.");
            if (PluginWorkspace.Find(s, reference.Base.FormKey.ToString(), true) is not IDoorGetter || reference.TeleportDestination is null)
                throw new ArgumentException("Reference must use a Door base and have a teleport destination.");
            var destination = PluginWorkspace.Find(s, reference.TeleportDestination.Door.FormKey.ToString(), true) as IPlacedObjectGetter;
            if (destination is null || destination.FormKey == reference.FormKey || PluginWorkspace.Find(s, destination.Base.FormKey.ToString(), true) is not IDoorGetter)
                throw new ArgumentException("Teleport destination must resolve to another placed Door reference.");
            var old = cell.NavigationMeshes.SelectMany(n => n.Data?.DoorTriangles ?? []).Where(d => d.Door.FormKey == reference.FormKey).ToArray();
            if (old.Length > 0) throw new ArgumentException("Door already has a navmesh link; refusing to overwrite its triangle association.");
            var map = s.Mod.NavigationMeshInfoMaps.SingleOrDefault(m => m.MapInfos.Any(i => i.NavigationMesh.FormKey == nav.FormKey)) ?? throw new ArgumentException("Expected one editable NAVI entry for this mesh.");
            data.Triangles[triangle].Flags |= NavmeshTriangle.Flag.Door;
            data.DoorTriangles.Add(new DoorTriangle { TriangleBeforeDoor = checked((short)triangle), Unknown = NavmeshRecords.PathingDoor, Door = reference.FormKey.ToLink<IPlacedObjectGetter>() });
            var index = map.MapInfos.IndexOf(map.MapInfos.Single(i => i.NavigationMesh.FormKey == nav.FormKey));
            map.MapInfos[index] = NavmeshRecords.Info(nav);
            return new { navmesh, door, triangle, linked = true, destination = destination.FormKey.ToString(), reciprocalTeleport = destination.TeleportDestination?.Door.FormKey == reference.FormKey,
                note = "Validate the destination endpoint separately and test NPC traversal." };
        }));
    }
}
