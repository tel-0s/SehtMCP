using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace SehtMcp.Tests;

public sealed class NavmeshTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SehtMcpNavTests", Guid.NewGuid().ToString("N"));
    private readonly PluginWorkspace workspace;
    private readonly NavmeshTools tools;
    private static readonly float[][] Floor = [[-256, -256, 0], [256, -256, 0], [256, 256, 0], [-256, 256, 0]];
    private static readonly int[][] Faces = [[0, 1, 2], [0, 2, 3]];
    public NavmeshTests()
    {
        Directory.CreateDirectory(directory);
        var config = new SehtConfig { Workspace = directory, DataRoots = [directory] };
        workspace = new(config); tools = new(workspace, new(config));
    }
    private PluginSession Session(string name = "Room.esp", string kind = "esp")
    {
        var session = workspace.Create(name, kind, "", "", []);
        workspace.Mutate(session.Id, 0, s => PluginWorkspace.CreateRecord(s, "Cell", "NavRoom", new JsonObject()));
        return session;
    }
    private static Cell Cell(PluginSession s) => s.Mod.Cells.SelectMany(b => b.SubBlocks).SelectMany(b => b.Cells).Single();
    private static void Ok(ModelContextProtocol.Protocol.CallToolResult result) => Assert.False(result.IsError, JsonSerializer.Serialize(result));

    [Theory]
    [InlineData("Room.esp", "esp")]
    [InlineData("Room.esm", "esm")]
    [InlineData("Room.esl", "esl")]
    [InlineData("Room.esp", "esp-fe")]
    public void AuthoredNavmeshAndInfoMapRoundTrip(string filename, string kind)
    {
        var s = Session(filename, kind); var cellKey = Cell(s).FormKey;
        Ok(tools.Create(s.Id, 1, cellKey.ToString(), Floor, Faces));
        Assert.True(workspace.Validate(s).Valid, JsonSerializer.Serialize(workspace.Validate(s)));
        workspace.Save(s.Id, 2, filename, false);
        using var parsed = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(directory, filename), SkyrimRelease.SkyrimSE);
        var cell = parsed.Cells.SelectMany(b => b.SubBlocks).SelectMany(b => b.Cells).Single();
        var nav = Assert.Single(cell.NavigationMeshes); var data = nav.Data!;
        Assert.Equal(cellKey, Assert.IsAssignableFrom<ICellNavmeshParentGetter>(data.Parent).Parent.FormKey);
        Assert.Equal(4, data.Vertices.Count); Assert.Equal(2, data.Triangles.Count);
        Assert.Equal(1, data.Triangles[0].EdgeLink_2_0); Assert.Equal(0, data.Triangles[1].EdgeLink_0_1);
        Assert.Equal(-1, data.Triangles[0].EdgeLink_0_1);
        Assert.Equal(1u, data.NavmeshGridDivisor); Assert.Equal(512, data.MaxDistanceX);
        // Decode the opaque NVNM grid independently according to xEdit's array layout.
        using var grid = new BinaryReader(new MemoryStream(data.NavmeshGrid.ToArray()));
        Assert.Equal(2u, grid.ReadUInt32()); Assert.Equal(0, grid.ReadInt16()); Assert.Equal(1, grid.ReadInt16());
        Assert.Equal(grid.BaseStream.Length, grid.BaseStream.Position);
        var infoMap = Assert.Single(parsed.NavigationMeshInfoMaps); Assert.Equal(12u, infoMap.NavMeshVersion);
        var info = Assert.Single(infoMap.MapInfos); Assert.Equal(nav.FormKey, info.NavigationMesh.FormKey);
        Assert.Equal(cellKey, Assert.IsAssignableFrom<INavigationMapInfoCellParentGetter>(info.Parent).ParentCell.FormKey);
        Assert.Equal(unchecked((int)0xA5E9A03C), info.Unknown2);
        Assert.NotNull(info.Island); Assert.Equal(2, info.Island.Triangles.Count);
        Assert.Equal(0x20, info.Unknown);
    }

    [Fact]
    public void BakesWalkableFloorWithRadiusAndPreservesHeight()
    {
        var s = Session();
        var floor = Floor.Select(v => new[] { v[0], v[1], 128f }).ToArray();
        Ok(tools.Generate(s.Id, 1, Cell(s).FormKey.ToString(), floor, Faces));
        var nav = Assert.Single(Cell(s).NavigationMeshes);
        Assert.All(nav.Data!.Vertices, v => { Assert.InRange(v.Z, 128, 132); Assert.InRange(v.X, -240, 240); Assert.InRange(v.Y, -240, 240); });
        Assert.NotEmpty(nav.Data.Triangles);
        Assert.True(workspace.Validate(s).Valid, JsonSerializer.Serialize(workspace.Validate(s)));
        workspace.Save(s.Id, 2, "Room.esp", false);
    }

    [Fact]
    public void RecastRejectsLowCeilingAndExcessiveSlope()
    {
        var lowRoom = Floor.Concat(Floor.Select(v => new[] { v[0], v[1], 64f })).ToArray();
        // Downward ceiling triangles are solid obstruction, not walkable roof surfaces.
        var faces = Faces.Concat(new int[][] { [4, 6, 5], [4, 7, 6] }).ToArray();
        var s = Session(); var key = Cell(s).FormKey.ToString();
        Assert.True(tools.Generate(s.Id, 1, key, lowRoom, faces).IsError);
        Assert.Empty(Cell(s).NavigationMeshes); Assert.Empty(s.Mod.NavigationMeshInfoMaps); Assert.Equal(1, s.Revision);
        var steep = Floor.Select(v => new[] { v[0], v[1], (v[0] + 256) * 2 }).ToArray();
        Assert.True(tools.Generate(s.Id, 1, key, steep, Faces).IsError);
    }

    [Fact]
    public void DisconnectedRegionsGetSeparateRecordsAndRebuildDoesNotDeleteThem()
    {
        var s = Session(); var key = Cell(s).FormKey.ToString();
        var vertices = Floor.Concat(Floor.Select(v => new[] { v[0] + 1024, v[1], v[2] })).ToArray();
        var faces = Faces.Concat(Faces.Select(t => t.Select(i => i + 4).ToArray())).ToArray();
        Ok(tools.Create(s.Id, 1, key, vertices, faces));
        var keys = Cell(s).NavigationMeshes.Select(n => n.FormKey).ToArray(); Assert.Equal(2, keys.Length);
        Assert.Equal(2, Assert.Single(s.Mod.NavigationMeshInfoMaps).MapInfos.Count);
        Ok(tools.Create(s.Id, 2, key, vertices, faces, replaceExisting: true));
        Assert.Equal(keys, Cell(s).NavigationMeshes.Select(n => n.FormKey));
        Assert.True(tools.Create(s.Id, 3, key, Floor, Faces, replaceExisting: true).IsError);
        Assert.Equal(keys, Cell(s).NavigationMeshes.Select(n => n.FormKey)); Assert.Equal(3, s.Revision);
    }

    [Fact]
    public void BadGeometryDryRunAndStaleRevisionsLeaveAllocatorUnchanged()
    {
        var s = Session(); var key = Cell(s).FormKey.ToString(); var next = s.Mod.ModHeader.Stats.NextFormID;
        Ok(tools.Generate(s.Id, 1, key, Floor, Faces, dryRun: true));
        Assert.Empty(Cell(s).NavigationMeshes); Assert.Equal(next, s.Mod.ModHeader.Stats.NextFormID);
        Assert.True(tools.Create(s.Id, 0, key, Floor, Faces).IsError);
        Assert.True(tools.Create(s.Id, 1, key, Floor, [[0, 1, 999]]).IsError);
        Assert.True(tools.Create(s.Id, 1, key, Floor, [[0, 1, 2], [2, 1, 0]]).IsError);
        Assert.True(tools.Generate(s.Id, 1, key, Floor, Faces, new() { CellSize = 0 }).IsError);
        Assert.Equal(next, s.Mod.ModHeader.Stats.NextFormID); Assert.Equal(1, s.Revision);
    }

    [Fact]
    public void ValidationCatchesBrokenAdjacencyAndStaleLookupBeforeSave()
    {
        var s = Session();
        Ok(tools.Create(s.Id, 1, Cell(s).FormKey.ToString(), Floor, Faces));
        var data = Cell(s).NavigationMeshes[0].Data!;
        data.Triangles[0].EdgeLink_2_0 = -1;
        data.NavmeshGrid = new byte[] { 1, 0, 0, 0, 0, 0 };
        var report = workspace.Validate(s);
        Assert.Contains(report.Issues, i => i.Code == "navmesh_adjacency");
        Assert.Contains(report.Issues, i => i.Code == "navmesh_grid");
        Assert.Throws<InvalidDataException>(() => workspace.Save(s.Id, 2, "Room.esp", false));
    }

    [Fact]
    public void LightAllocationFailureRollsBackAllNavmeshRecords()
    {
        var s = Session("Room.esl", "esl"); s.Mod.ModHeader.Stats.NextFormID = 0xFFF;
        Assert.True(tools.Create(s.Id, 1, Cell(s).FormKey.ToString(), Floor, Faces).IsError);
        Assert.Empty(Cell(s).NavigationMeshes); Assert.Empty(s.Mod.NavigationMeshInfoMaps);
        Assert.Equal(0xFFFu, s.Mod.ModHeader.Stats.NextFormID);
    }

    [Fact]
    public void DoorLinksRoundTripAndNearestUsesSurfaceRatherThanVertexDistance()
    {
        var s = Session(); var key = Cell(s).FormKey.ToString();
        workspace.Mutate(s.Id, 1, session =>
        {
            var baseDoor = PluginWorkspace.CreateRecord(session, "Door", "DoorBase", new JsonObject());
            var a = (PlacedObject)PluginWorkspace.CreateRecord(session, "PlacedObject", "DoorA", new JsonObject(), key, "Persistent");
            var b = (PlacedObject)PluginWorkspace.CreateRecord(session, "PlacedObject", "DoorB", new JsonObject(), key, "Persistent");
            a.Base.SetTo(baseDoor.FormKey); b.Base.SetTo(baseDoor.FormKey);
            a.TeleportDestination = new() { Door = b.FormKey.ToLink<IPlacedObjectGetter>() };
            b.TeleportDestination = new() { Door = a.FormKey.ToLink<IPlacedObjectGetter>() };
            return null;
        });
        Ok(tools.Create(s.Id, 2, key, Floor, Faces));
        var closest = tools.Nearest(s.Id, key, 0, 0, 8, 10); Ok(closest);
        Assert.Equal(8, closest.StructuredContent!.Value.GetProperty("distance").GetSingle());
        var nav = Cell(s).NavigationMeshes[0].FormKey.ToString(); var door = Cell(s).Persistent[0].FormKey.ToString();
        Ok(tools.LinkDoor(s.Id, 3, nav, door, 0));
        Assert.True(tools.LinkDoor(s.Id, 4, nav, door, 1).IsError); Assert.Equal(4, s.Revision);
        Assert.True(tools.Create(s.Id, 4, key, Floor, Faces, replaceExisting: true).IsError);
        Assert.True(workspace.Validate(s).Valid, JsonSerializer.Serialize(workspace.Validate(s)));
        workspace.Save(s.Id, 4, "Room.esp", false);
        var parsed = SkyrimMod.CreateFromBinary(Path.Combine(directory, "Room.esp"), SkyrimRelease.SkyrimSE);
        var read = parsed.Cells.SelectMany(b => b.SubBlocks).SelectMany(b => b.Cells).Single().NavigationMeshes.Single().Data!;
        Assert.Equal(unchecked((int)0xE48B73F3), Assert.Single(read.DoorTriangles).Unknown);
        Assert.True(read.Triangles[0].Flags.HasFlag(NavmeshTriangle.Flag.Door));
        var info = Assert.Single(Assert.Single(parsed.NavigationMeshInfoMaps).MapInfos);
        Assert.Null(info.Island); Assert.Equal(0, info.Unknown); Assert.Equal(door, Assert.Single(info.LinkedDoors).Door.FormKey.ToString());
    }

    [Fact]
    public void BakesPlacedNifWithScaleClockwiseRotationAndTranslation()
    {
        Directory.CreateDirectory(Path.Combine(directory, "meshes"));
        File.WriteAllBytes(Path.Combine(directory, "meshes", "floor.nif"), NifTests.Mesh(Floor, [0, 1, 2, 0, 2, 3]));
        var assetReader = new AssetService(new() { Workspace = directory, DataRoots = [directory] });
        Assert.NotEmpty(assetReader.Read("MeShEs/FLOOR.NIF"));
        var s = Session(); var key = Cell(s).FormKey.ToString();
        workspace.Mutate(s.Id, 1, session =>
        {
            var source = PluginWorkspace.CreateRecord(session, "Static", "Floor", JsonNode.Parse("""{"Model":{"File":"floor.nif"}}""")!.AsObject());
            var fields = JsonNode.Parse("""{"Scale":2,"Placement":{"Position":{"X":1024,"Y":-512,"Z":64},"Rotation":{"Z":1.57079632679}}}""")!.AsObject();
            fields["Base"] = source.FormKey.ToString();
            PluginWorkspace.CreateRecord(session, "PlacedObject", "PlacedFloor", fields, key, "Temporary");
            return null;
        });
        Ok(tools.GenerateFromCell(s.Id, 2, key));
        var data = Assert.Single(Cell(s).NavigationMeshes).Data!;
        Assert.InRange(data.Min.X, 512, 544); Assert.InRange(data.Max.Y, -32, 0); Assert.InRange(data.Min.Z, 64, 68);
        Assert.True(workspace.Validate(s).Valid, JsonSerializer.Serialize(workspace.Validate(s)));
        var transform = NavmeshScene.PlacementTransform(new(10, 20, 30), new(MathF.PI / 2, MathF.PI / 2, MathF.PI / 2), 2);
        // Apply clockwise Z, Y, X in sequence: (1,2,3) -> (2,-1,3) -> (-3,-1,2) -> (-3,2,1).
        var point = Vector3.Transform(new(1, 2, 3), transform);
        Assert.InRange(Vector3.Distance(point, new(4, 24, 32)), 0, .001f);
    }

    [Fact]
    public void SceneMissingAssetsFailAtomically()
    {
        var s = Session(); var key = Cell(s).FormKey.ToString();
        workspace.Mutate(s.Id, 1, session =>
        {
            var source = PluginWorkspace.CreateRecord(session, "Static", "Missing", JsonNode.Parse("""{"Model":{"File":"missing.nif"}}""")!.AsObject());
            PluginWorkspace.CreateRecord(session, "PlacedObject", "Ref", JsonNode.Parse($$"""{"Base":"{{source.FormKey}}"}""")!.AsObject(), key, "Temporary");
            return null;
        });
        Assert.True(tools.GenerateFromCell(s.Id, 2, key).IsError); Assert.Equal(2, s.Revision);
        Assert.Empty(Cell(s).NavigationMeshes); Assert.Empty(s.Mod.NavigationMeshInfoMaps);
    }

    [Fact]
    public void ObstacleRemovesWalkableFloorUnderItsFootprint()
    {
        var vertices = Floor.Concat(new float[][] { [-40,-40,0], [40,-40,0], [40,40,0], [-40,40,0], [-40,-40,256], [40,-40,256], [40,40,256], [-40,40,256] }).ToArray();
        var triangles = Faces.Concat(new int[][] { [4,5,9], [4,9,8], [5,6,10], [5,10,9], [6,7,11], [6,11,10], [7,4,8], [7,8,11], [8,9,10], [8,10,11] }).ToArray();
        var s = Session(); var key = Cell(s).FormKey.ToString();
        Ok(tools.Generate(s.Id, 1, key, vertices, triangles));
        Assert.True(tools.Nearest(s.Id, key, 0, 0, 0, 40).IsError);
        Ok(tools.Nearest(s.Id, key, 128, 0, 0, 4));
        Assert.True(workspace.Validate(s).Valid, JsonSerializer.Serialize(workspace.Validate(s)));
    }

    [Fact]
    public void SeedsKeepOnlyTheReachableComponentAndRejectDistantPoints()
    {
        var s = Session(); var key = Cell(s).FormKey.ToString();
        var vertices = Floor.Concat(Floor.Select(v => new[] { v[0] + 1024, v[1], v[2] })).ToArray();
        var faces = Faces.Concat(Faces.Select(t => t.Select(i => i + 4).ToArray())).ToArray();
        Assert.True(tools.Generate(s.Id, 1, key, vertices, faces, walkableSeeds: [[5000,0,0]]).IsError);
        Assert.Empty(Cell(s).NavigationMeshes); Assert.Equal(1, s.Revision);
        Ok(tools.Generate(s.Id, 1, key, vertices, faces, walkableSeeds: [[0,0,0]]));
        Assert.Single(Cell(s).NavigationMeshes);
        Assert.True(tools.Nearest(s.Id, key, 1024, 0, 0).IsError);
        Ok(tools.Nearest(s.Id, key, 0, 0, 0));
    }

    [Fact]
    public void ClimbSettingControlsStairConnectivity()
    {
        var vertices = new List<float[]>(); var faces = new List<int[]>();
        for (var step = 0; step < 3; step++)
        {
            var offset = vertices.Count; var x = step * 128f; var z = step * 16f;
            vertices.AddRange(new float[][] { [x,0,z], [x+128,0,z], [x+128,256,z], [x,256,z] });
            faces.Add([offset,offset+1,offset+2]); faces.Add([offset,offset+2,offset+3]);
            if (step > 0) { faces.Add([offset-3,offset,offset+3]); faces.Add([offset-3,offset+3,offset-2]); }
        }
        var input = NavmeshGeometry.Parse(vertices.ToArray(), faces.ToArray());
        Assert.Single(input.Bake(new() { MaxClimb = 32 }).Normalize().Components());
        Assert.Equal(3, input.Bake(new() { MaxClimb = 8 }).Normalize().Components().Length);
    }

    [Fact]
    public void OrphanExteriorAndInheritedCellsAreRejectedWithoutAddingNavmeshes()
    {
        var source = Session("Source.esm", "esm"); var key = Cell(source).FormKey.ToString();
        workspace.Save(source.Id, 1, "Source.esm", false);
        var patch = workspace.Create("Patch.esp", "esp", "", "", ["Source.esm"]);
        workspace.Mutate(patch.Id, 0, s => PluginWorkspace.CopyRecord(s, key, true, null));
        Assert.True(tools.Create(patch.Id, 1, key, Floor, Faces).IsError);
        Assert.Empty(Cell(patch).NavigationMeshes);
        Cell(source).Flags &= ~Mutagen.Bethesda.Skyrim.Cell.Flag.IsInteriorCell;
        Assert.True(tools.Create(source.Id, 1, key, Floor, Faces).IsError);
        Assert.Empty(Cell(source).NavigationMeshes);
    }

    public void Dispose() { workspace.Dispose(); Directory.Delete(directory, true); }
}
