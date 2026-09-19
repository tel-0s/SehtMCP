using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace SehtMcp.Tests;

public sealed class AuthoringTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SehtMcpTests", Guid.NewGuid().ToString("N"));
    private readonly SehtConfig config;
    private readonly PluginWorkspace workspace;
    private readonly AuthoringTools tools;
    public AuthoringTests()
    {
        Directory.CreateDirectory(directory); config = new() { Workspace = directory, DataRoots = [directory] };
        workspace = new(config); tools = new(workspace, config, new(config));
    }
    private static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();

    [Theory]
    [InlineData("Example.esp", "esp", false, false)]
    [InlineData("Example.esm", "esm", true, false)]
    [InlineData("Example.esl", "esl", true, true)]
    [InlineData("Example.esp", "esp-fe", false, true)]
    public void AllPluginKindsRoundTrip(string filename, string kind, bool master, bool light)
    {
        var s = workspace.Create(filename, kind, "Seht", "Test", []);
        workspace.Mutate(s.Id, 0, x => PluginWorkspace.CreateRecord(x, "Keyword", "SehtKeyword", J("{}")));
        workspace.Save(s.Id, 1, filename, false);
        using var read = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(directory, filename), SkyrimRelease.SkyrimSE);
        Assert.Equal(master, read.IsMaster); Assert.Equal(light, read.IsSmallMaster);
        Assert.Equal("SehtKeyword", Assert.Single(read.Keywords).EditorID);
        Assert.Equal((uint)0x800, read.Keywords.First().FormKey.ID);
    }

    [Fact]
    public void TypedWeaponAndRecipeRoundTripWithLinks()
    {
        var s = workspace.Create("Smith.esp", "esp", "", "", []);
        var result = tools.Batch(s.Id, 0, JsonNode.Parse("""
        [
          {"op":"create","type":"Keyword","editorId":"SehtMaterial","alias":"material"},
          {"op":"create","type":"Weapon","editorId":"SehtBlade","alias":"blade","fields":{"Name":"Seht's Blade","BasicStats":{"Damage":12,"Value":150,"Weight":8},"Data":{"AnimationType":"OneHandSword","Speed":1,"Reach":1},"Keywords":["$material"],"Model":{"File":"weapons/iron/longsword.nif"}}},
          {"op":"create","type":"ConstructibleObject","editorId":"SehtRecipe","fields":{"CreatedObject":"$blade","CreatedObjectCount":1,"WorkbenchKeyword":"$material"}}
        ]
        """)!.AsArray());
        Assert.False(result.IsError, result.Content.ToString());
        Assert.True(workspace.Validate(s).Valid);
        workspace.Save(s.Id, 1, "Smith.esp", false);
        using var read = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(directory, "Smith.esp"), SkyrimRelease.SkyrimSE);
        var weapon = Assert.Single(read.Weapons);
        Assert.Equal((ushort)12, weapon.BasicStats!.Damage); Assert.Equal("Seht's Blade", weapon.Name!.String);
        Assert.Equal(weapon.FormKey, Assert.Single(read.ConstructibleObjects).CreatedObject.FormKey);
    }

    [Fact]
    public void BadBatchRollsBackRecordsAndAllocator()
    {
        var s = workspace.Create("Atomic.esp", "esp", "", "", []);
        var result = tools.Batch(s.Id, 0, JsonNode.Parse("""[{"op":"create","type":"Keyword","editorId":"Good"},{"op":"create","type":"Weapon","editorId":"Bad","fields":{"BogusField":1}}]""")!.AsArray());
        Assert.True(result.IsError); Assert.Empty(s.Mod.EnumerateMajorRecords()); Assert.Equal(0, s.Revision); Assert.Equal((uint)0x800, s.Mod.ModHeader.Stats.NextFormID);
    }

    [Fact]
    public void DryRunAndRevisionConflictDoNotCommit()
    {
        var s = workspace.Create("Dry.esp", "esp", "", "", []);
        var result = tools.Batch(s.Id, 0, JsonNode.Parse("""[{"op":"create","type":"Keyword","editorId":"Example"}]""")!.AsArray(), true);
        Assert.False(result.IsError); Assert.Empty(s.Mod.EnumerateMajorRecords()); Assert.Equal(0, s.Revision);
        Assert.Equal(0, result.StructuredContent!.Value.GetProperty("revision").GetInt64());
        Assert.Throws<InvalidOperationException>(() => workspace.Mutate(s.Id, 9, _ => null));
    }

    [Fact]
    public void TypedLinksAreValidatedAgainstActualTarget()
    {
        var s = workspace.Create("Links.esp", "esp", "", "", []);
        workspace.Mutate(s.Id, 0, x => PluginWorkspace.CreateRecord(x, "Weapon", "Wrong", J("{}")));
        workspace.Mutate(s.Id, 1, x => PluginWorkspace.CreateRecord(x, "Weapon", "Other", J("""{"Keywords":["000800:Links.esp"]}""")));
        Assert.Contains(workspace.Validate(s).Issues, i => i.Code == "link_type");
        Assert.Throws<InvalidDataException>(() => workspace.Save(s.Id, 2, "Links.esp", false));
    }

    [Fact]
    public void LightLimitsDoNotSilentlyCompact()
    {
        var s = workspace.Create("Light.esp", "esp-fe", "", "", []); s.Mod.ModHeader.Stats.NextFormID = 0xFFF;
        workspace.Mutate(s.Id, 0, x => PluginWorkspace.CreateRecord(x, "Keyword", "Last", J("{}")));
        Assert.Throws<ArgumentException>(() => workspace.Mutate(s.Id, 1, x => PluginWorkspace.CreateRecord(x, "Keyword", "Overflow", J("{}"))));
        Assert.Single(s.Mod.Keywords); Assert.Equal(1, s.Revision);
    }

    [Fact]
    public void InteriorAndPlacedObjectRoundTrip()
    {
        var s = workspace.Create("Room.esp", "esp", "", "", []);
        var result = tools.Batch(s.Id, 0, JsonNode.Parse("""
        [{"op":"create","type":"Static","editorId":"SehtStatic","alias":"stat"},
         {"op":"create","type":"Cell","editorId":"SehtRoom","alias":"room","fields":{"Name":"Test Room"}},
         {"op":"create","type":"PlacedObject","editorId":"SehtPlaced","parent":"$room","slot":"Temporary","fields":{"Base":"$stat"}}]
        """)!.AsArray());
        Assert.False(result.IsError, JsonSerializer.Serialize(result));
        workspace.Save(s.Id, 1, "Room.esp", false);
        using var read = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(directory, "Room.esp"), SkyrimRelease.SkyrimSE);
        Assert.Equal(3, read.EnumerateMajorRecords().Count());
        Assert.Single(read.Cells.SelectMany(b => b.SubBlocks).SelectMany(b => b.Cells).Single().Temporary);
    }

    [Fact]
    public void OverridePreservesIdentityAndOriginalSource()
    {
        var master = workspace.Create("Source.esm", "esm", "", "", []);
        workspace.Mutate(master.Id, 0, s => PluginWorkspace.CreateRecord(s, "Weapon", "BaseBlade", J("""{"Name":"Original","BasicStats":{"Damage":4,"Value":10,"Weight":2}}""")));
        workspace.Save(master.Id, 1, "Source.esm", false);
        var originalHash = PluginWorkspace.HashFile(Path.Combine(directory, "Source.esm"));
        var patch = workspace.Create("Patch.esp", "esp", "", "", ["Source.esm"]);
        Assert.False(tools.Override(patch.Id, 0, "000800:Source.esm", J("""{"BasicStats":{"Damage":20}}""")).IsError);
        workspace.Save(patch.Id, 1, "Patch.esp", false);
        using var read = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(directory, "Patch.esp"), SkyrimRelease.SkyrimSE);
        Assert.Equal("000800:Source.esm", read.Weapons.First().FormKey.ToString());
        Assert.Equal((ushort)20, read.Weapons.First().BasicStats!.Damage);
        Assert.Equal(originalHash, PluginWorkspace.HashFile(Path.Combine(directory, "Source.esm")));
    }

    [Fact]
    public void SaveBackupAndExternalChangeProtection()
    {
        var s = workspace.Create("Save.esp", "esp", "", "", []);
        workspace.Save(s.Id, 0, "Save.esp", false);
        Assert.Throws<IOException>(() => workspace.Save(s.Id, 0, "Save.esp", false));
        workspace.Save(s.Id, 0, "Save.esp", true);
        Assert.Single(Directory.GetFiles(directory, "*.bak"));
        File.AppendAllText(Path.Combine(directory, "Save.esp"), "changed");
        Assert.Throws<IOException>(() => workspace.Save(s.Id, 0, "Save.esp", true));
    }

    [Fact]
    public void CheckpointRestoreAndDiff()
    {
        var s = workspace.Create("Undo.esp", "esp", "", "", []);
        Assert.False(tools.Checkpoint(s.Id, "start").IsError);
        Assert.False(tools.CreateRecord(s.Id, 0, "Keyword", "New").IsError);
        Assert.False(tools.Diff(s.Id, "start").IsError);
        Assert.False(tools.Restore(s.Id, 1, "start").IsError);
        Assert.Empty(s.Mod.Keywords); Assert.Equal(2, s.Revision);
    }

    [Fact]
    public void DuplicateAndRemoveLeafRecordPreserveTheSource()
    {
        var s = workspace.Create("Copies.esp", "esp", "", "", []);
        Assert.False(tools.CreateRecord(s.Id, 0, "Keyword", "Original").IsError);
        Assert.False(tools.Checkpoint(s.Id, "before").IsError);
        Assert.Equal(0, tools.Diff(s.Id, "before").StructuredContent!.Value.GetProperty("total").GetInt32());
        var result = tools.Duplicate(s.Id, 1, "000800:Copies.esp", "Copy");
        Assert.False(result.IsError, JsonSerializer.Serialize(result));
        Assert.Equal(2, s.Mod.Keywords.Count);
        Assert.False(tools.Remove(s.Id, 2, "000801:Copies.esp").IsError);
        Assert.Equal("Original", Assert.Single(s.Mod.Keywords).EditorID);
    }

    [Theory]
    [InlineData("../escape.esp")]
    [InlineData("sub/../../escape.esp")]
    public void OutputTraversalRejected(string path) => Assert.Throws<ArgumentException>(() => config.OutputPath(path));

    public void Dispose() { workspace.Dispose(); Directory.Delete(directory, true); }
}
