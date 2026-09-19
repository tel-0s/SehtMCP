using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace SehtMcp.Tests;

public sealed class ExteriorNavmeshTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SehtExteriorTests", Guid.NewGuid().ToString("N"));
    private readonly PluginWorkspace workspace;
    private readonly NavmeshTools tools;
    private readonly WorldTools worlds;
    private static readonly int[][] Faces = [[0, 1, 2], [0, 2, 3]];
    private static float[][] Floor(int x = -2, int y = 3) => [[x*4096+1024, y*4096+1024, 64], [x*4096+3072, y*4096+1024, 64], [x*4096+3072, y*4096+3072, 64], [x*4096+1024, y*4096+3072, 64]];
    public ExteriorNavmeshTests()
    {
        Directory.CreateDirectory(directory);
        var config = new SehtConfig { Workspace = directory, DataRoots = [directory] };
        workspace = new(config); tools = new(workspace, new(config)); worlds = new(workspace);
    }
    private static JsonElement Ok(CallToolResult result) { Assert.False(result.IsError, JsonSerializer.Serialize(result)); return result.StructuredContent!.Value; }
    private PluginSession Session(string filename = "Island.esp", string kind = "esp")
    {
        var s = workspace.Create(filename, kind, "", "", []);
        workspace.Mutate(s.Id, 0, s => PluginWorkspace.CreateRecord(s, "Worldspace", "IslandWorld", new JsonObject { ["Flags"] = "SmallWorld" }));
        Ok(worlds.Exterior(s.Id, 1, s.Mod.Worldspaces.Single().FormKey.ToString(), "IslandCell", -2, 3));
        return s;
    }
    private static Worldspace World(PluginSession s) => s.Mod.Worldspaces.Single();
    private static Cell Cell(PluginSession s) => NavmeshCell.Cells(World(s)).Single();
    private void Valid(PluginSession s) => Assert.True(workspace.Validate(s).Valid, JsonSerializer.Serialize(workspace.Validate(s)));

    [Theory]
    [InlineData("Island.esp", "esp")]
    [InlineData("Island.esm", "esm")]
    [InlineData("Island.esl", "esl")]
    [InlineData("Island.esp", "esp-fe")]
    public void ExteriorParentsAndRawYXCoordinatesRoundTrip(string filename, string kind)
    {
        var s = Session(filename, kind); var key = Cell(s).FormKey.ToString(); var next = s.Mod.ModHeader.Stats.NextFormID;
        Ok(tools.Generate(s.Id, 2, key, Floor(), Faces, dryRun: true));
        Assert.Equal(next, s.Mod.ModHeader.Stats.NextFormID); Assert.Empty(Cell(s).NavigationMeshes); Assert.Equal(2, s.Revision);
        Ok(tools.Generate(s.Id, 2, key, Floor(), Faces, walkableSeeds: [[-6144,14336,64]]));
        var nav = Cell(s).NavigationMeshes.Single().FormKey;
        Ok(tools.Generate(s.Id, 3, key, Floor(), Faces, replaceExisting: true));
        Assert.Equal(nav, Cell(s).NavigationMeshes.Single().FormKey);
        Ok(tools.Get(s.Id, nav.ToString())); Ok(tools.Nearest(s.Id, key, -6144, 14336, 64)); Ok(tools.Preview(s.Id, key));
        Valid(s); workspace.Save(s.Id, 4, filename, false);
        var parsed = SkyrimMod.CreateFromBinary(Path.Combine(directory, filename), SkyrimRelease.SkyrimSE);
        var world = parsed.Worldspaces.Single(); var cell = NavmeshCell.Cells(world).Single();
        var parent = Assert.IsType<WorldspaceNavmeshParent>(cell.NavigationMeshes.Single().Data!.Parent);
        Assert.Equal(world.FormKey, parent.Parent.FormKey); Assert.Equal(new P2Int16(3,-2), parent.Coordinates);
        var info = Assert.Single(Assert.Single(parsed.NavigationMeshInfoMaps).MapInfos);
        var ip = Assert.IsType<NavigationMapInfoWorldParent>(info.Parent);
        Assert.Equal(parent.Parent.FormKey, ip.ParentWorldspace.FormKey); Assert.Equal(parent.Coordinates, ip.ParentWorldspaceCoord);
        Assert.NotNull(info.Island);
        // Check actual file bytes against the independent xEdit layout, not just Mutagen's round-trip.
        var records = Records(File.ReadAllBytes(Path.Combine(directory, filename))).ToArray();
        var nvnm = Subrecord(Assert.Single(records, r => r.Type == "NAVM").Data, "NVNM");
        Assert.Equal(world.FormKey.ID, BinaryPrimitives.ReadUInt32LittleEndian(nvnm.AsSpan(8)) & 0xFFFFFF);
        Assert.Equal(3, BinaryPrimitives.ReadInt16LittleEndian(nvnm.AsSpan(12)));
        Assert.Equal(-2, BinaryPrimitives.ReadInt16LittleEndian(nvnm.AsSpan(14)));
        var nvmi = Subrecord(Assert.Single(records, r => r.Type == "NAVI").Data, "NVMI");
        Assert.Equal(world.FormKey.ID, BinaryPrimitives.ReadUInt32LittleEndian(nvmi.AsSpan(nvmi.Length-8)) & 0xFFFFFF);
        Assert.Equal(3, BinaryPrimitives.ReadInt16LittleEndian(nvmi.AsSpan(nvmi.Length-4)));
        Assert.Equal(-2, BinaryPrimitives.ReadInt16LittleEndian(nvmi.AsSpan(nvmi.Length-2)));
        Valid(workspace.Open(Path.Combine(directory, filename)));
    }

    [Fact]
    public void CrossCellGeometryAndInheritedWorldFailWithoutAllocating()
    {
        var s = Session(); var key = Cell(s).FormKey.ToString(); var next = s.Mod.ModHeader.Stats.NextFormID;
        var outside = Floor(); outside[0][0] = -8193;
        Assert.True(tools.Create(s.Id, 2, key, outside, Faces).IsError);
        Assert.Empty(Cell(s).NavigationMeshes); Assert.Equal(next, s.Mod.ModHeader.Stats.NextFormID);
        World(s).Parent = new WorldspaceParent { Worldspace = World(s).FormKey.ToLink<IWorldspaceGetter>() };
        Assert.True(tools.Create(s.Id, 2, key, Floor(), Faces).IsError);
        Assert.Empty(Cell(s).NavigationMeshes); Assert.Equal(2, s.Revision);
    }

    [Fact]
    public void ExteriorValidationCatchesWrongGridAndNaviParent()
    {
        var s = Session(); Ok(tools.Create(s.Id, 2, Cell(s).FormKey.ToString(), Floor(), Faces));
        ((WorldspaceNavmeshParent)Cell(s).NavigationMeshes.Single().Data!.Parent!).Coordinates = new(-2,3);
        var issues = workspace.Validate(s).Issues;
        Assert.Contains(issues, i => i.Code == "navmesh_parent"); Assert.Contains(issues, i => i.Code == "navmesh_navi");
        Assert.Throws<InvalidDataException>(() => workspace.Save(s.Id, 3, "Island.esp", false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExteriorInteriorDoorPairUsesWorldPersistentCellAndPreservesIdentity(bool legacyChildDoor)
    {
        var s = Session(); var key = Cell(s).FormKey.ToString();
        var room = (Cell)PluginWorkspace.CreateRecord(s, "Cell", "Room", new());
        var doorBase = PluginWorkspace.CreateRecord(s, "Door", "DoorBase", new());
        string exteriorDoor;
        if (legacyChildDoor)
        {
            exteriorDoor = PluginWorkspace.CreateRecord(s, "PlacedObject", "OutsideDoor", new JsonObject { ["Base"] = doorBase.FormKey.ToString(), ["Placement"] = new JsonObject { ["Position"] = new JsonObject { ["X"] = -6144, ["Y"] = 14336, ["Z"] = 64 } } }, key, "Persistent").FormKey.ToString();
        }
        else exteriorDoor = Ok(worlds.Place(s.Id, s.Revision, key, doorBase.FormKey.ToString(), "OutsideDoor", -6144, 14336, 64, persistent: true)).GetProperty("result").GetProperty("formKey").GetString()!;
        var insideDoor = Ok(worlds.Place(s.Id, s.Revision, room.FormKey.ToString(), doorBase.FormKey.ToString(), "InsideDoor", 2048, 2048, 64, persistent: true)).GetProperty("result").GetProperty("formKey").GetString()!;
        var a = (PlacedObject)PluginWorkspace.Find(s, exteriorDoor); var b = (PlacedObject)PluginWorkspace.Find(s, insideDoor);
        a.TeleportDestination = new() { Door = b.FormKey.ToLink<IPlacedObjectGetter>(), Position = new(2048,2048,64) };
        b.TeleportDestination = new() { Door = a.FormKey.ToLink<IPlacedObjectGetter>(), Position = new(-6144,14336,64) };
        Ok(tools.Generate(s.Id, s.Revision, key, Floor(), Faces));
        Ok(tools.Generate(s.Id, s.Revision, room.FormKey.ToString(), Floor(0,0), Faces));
        var nav = Cell(s).NavigationMeshes.Single().FormKey.ToString();
        Ok(tools.LinkDoor(s.Id, s.Revision, nav, exteriorDoor, 0));
        var roomNav = ((Cell)PluginWorkspace.Find(s, room.FormKey.ToString())).NavigationMeshes.Single().FormKey.ToString();
        Ok(tools.LinkDoor(s.Id, s.Revision, roomNav, insideDoor, 0));
        Assert.Empty(Cell(s).Persistent); Assert.Equal(exteriorDoor, Assert.Single(World(s).TopCell!.Persistent).FormKey.ToString());
        Assert.True((World(s).TopCell!.MajorRecordFlagsRaw & 0x400) != 0);
        Assert.True(tools.LinkDoor(s.Id, s.Revision, nav, exteriorDoor, 0).IsError);
        Assert.True(tools.Generate(s.Id, s.Revision, key, Floor(), Faces, replaceExisting: true).IsError);
        Valid(s); workspace.Save(s.Id, s.Revision, "Island.esp", false);
        var reopened = workspace.Open(Path.Combine(directory, "Island.esp")); Valid(reopened);
        Assert.Equal(exteriorDoor, Assert.Single(World(reopened).TopCell!.Persistent).FormKey.ToString());
        var info = reopened.Mod.NavigationMeshInfoMaps.Single().MapInfos.Single(i => i.NavigationMesh.FormKey.ToString() == nav);
        Assert.IsType<NavigationMapInfoWorldParent>(info.Parent); Assert.Null(info.Island); Assert.Single(info.LinkedDoors);
    }

    [Fact]
    public void DoorInAnotherExteriorGridCannotLinkAndRollbackPreservesIt()
    {
        var s = Session(); var key = Cell(s).FormKey.ToString();
        var doorBase = PluginWorkspace.CreateRecord(s, "Door", "Base", new());
        var door = Ok(worlds.Place(s.Id, 2, key, doorBase.FormKey.ToString(), "Door", -6144,14336,64,persistent:true)).GetProperty("result").GetProperty("formKey").GetString()!;
        ((PlacedObject)PluginWorkspace.Find(s, door)).Placement!.Position = new(0,0,64);
        Ok(tools.Create(s.Id, 3, key, Floor(), Faces));
        Assert.True(tools.LinkDoor(s.Id, 4, Cell(s).NavigationMeshes.Single().FormKey.ToString(), door, 0).IsError);
        Assert.Equal(4, s.Revision); Assert.Empty(Cell(s).NavigationMeshes.Single().Data!.DoorTriangles);
        Assert.Single(World(s).TopCell!.Persistent);
    }

    [Fact]
    public void NifBakingIncludesPersistentGeometryAndRefusesUncollectedTerrain()
    {
        Directory.CreateDirectory(Path.Combine(directory,"meshes"));
        File.WriteAllBytes(Path.Combine(directory,"meshes","floor.nif"), NifTests.Mesh(Floor(0,0), [0,1,2,0,2,3]));
        var s = Session(); var key = Cell(s).FormKey.ToString();
        var floor = PluginWorkspace.CreateRecord(s, "Static", "Floor", JsonNode.Parse("""{"Model":{"File":"floor.nif"}}""")!.AsObject());
        var placed = Ok(worlds.Place(s.Id, 2, key, floor.FormKey.ToString(), "FloorRef", -8192,12288,0,persistent:true)).GetProperty("result").GetProperty("formKey").GetString()!;
        Ok(tools.GenerateFromCell(s.Id, 3, key, references: [placed])); Valid(s);
        Assert.InRange(Cell(s).NavigationMeshes.Single().Data!.Min.X, -7168,-7100);
        Cell(s).Landscape = new Landscape(s.Mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        Assert.True(tools.GenerateFromCell(s.Id, 4, key, references:[placed],replaceExisting:true).IsError);
        Assert.Equal(4, s.Revision);
    }

    private static IEnumerable<(string Type, byte[] Data)> Records(byte[] bytes)
    {
        for (var offset = 0; offset < bytes.Length;)
        {
            var type = Encoding.ASCII.GetString(bytes,offset,4); var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4)));
            if (type == "GRUP") { foreach(var record in Records(bytes[(offset+24)..(offset+size)])) yield return record; offset += size; continue; }
            var data = bytes[(offset+24)..(offset+24+size)];
            if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+8)) & 0x40000) != 0)
            {
                using var zip = new ZLibStream(new MemoryStream(data[4..]),CompressionMode.Decompress); using var buffer = new MemoryStream(); zip.CopyTo(buffer); data = buffer.ToArray();
            }
            yield return (type,data); offset += 24+size;
        }
    }
    private static byte[] Subrecord(byte[] bytes, string name)
    {
        for(var offset=0; offset<bytes.Length;) { var size=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset+4)); if(Encoding.ASCII.GetString(bytes,offset,4)==name) return bytes[(offset+6)..(offset+6+size)]; offset+=6+size; }
        throw new InvalidDataException("Missing "+name);
    }
    public void Dispose() { workspace.Dispose(); Directory.Delete(directory,true); }
}

