using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace SehtMcp.Tests;

public sealed class ComplexRecordTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SehtMcpTests", Guid.NewGuid().ToString("N"));
    private readonly PluginWorkspace workspace;
    private readonly AuthoringTools tools;
    private readonly WorldTools world;
    public ComplexRecordTests()
    {
        Directory.CreateDirectory(directory); var config = new SehtConfig { Workspace = directory, DataRoots = [directory] };
        workspace = new(config); tools = new(workspace, config, new(config)); world = new(workspace);
    }
    private static void Ok(CallToolResult r) => Assert.False(r.IsError, JsonSerializer.Serialize(r));
    private static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void NpcLevelsAndScriptPropertiesRoundTrip()
    {
        var s = workspace.Create("Actors.esp", "esp", "", "", []);
        Ok(tools.CreateRecord(s.Id, 0, "Npc", "SehtActor", J("""
        {"Name":"Seht Actor","Configuration":{"Level":{"$type":"NpcLevel","Level":12},"HealthOffset":50},
         "VirtualMachineAdapter":{"Scripts":[{"Name":"SehtActorScript","Properties":[{"$type":"ScriptIntProperty","Name":"Count","Data":3}]}]}}
        """)));
        workspace.Save(s.Id, 1, "Actors.esp", false);
        using var read = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(directory, "Actors.esp"), SkyrimRelease.SkyrimSE);
        var npc = Assert.Single(read.Npcs);
        Assert.Equal((short)12, ((INpcLevelGetter)npc.Configuration.Level).Level);
        Assert.Equal(3, ((IScriptIntPropertyGetter)npc.VirtualMachineAdapter!.Scripts[0].Properties[0]).Data);
        Ok(tools.Get(s.Id, npc.FormKey.ToString()));
    }

    [Fact]
    public void QuestConditionsAndSpellEffectsRoundTrip()
    {
        var s = workspace.Create("Magic.esp", "esp", "", "", []);
        Ok(tools.Batch(s.Id, 0, JsonNode.Parse("""
        [
          {"op":"create","type":"Quest","editorId":"SehtQuest","alias":"quest","fields":{"Name":"Seht Quest","Priority":50,"Stages":[{"Index":10},{"Index":20}],"Objectives":[{"Index":10,"DisplayText":"Find the item"}]}},
          {"op":"create","type":"MagicEffect","editorId":"SehtEffect","alias":"effect","fields":{"Name":"Seht Effect"}},
          {"op":"create","type":"Spell","editorId":"SehtSpell","fields":{"Name":"Seht Spell","Effects":[{"BaseEffect":"$effect","Data":{"Magnitude":5,"Area":0,"Duration":10},"Conditions":[{"$type":"ConditionFloat","ComparisonValue":10,"CompareOperator":"GreaterThanOrEqualTo","Data":{"$type":"GetStageConditionData","Quest":"$quest"}}]}]}}
        ]
        """)!.AsArray()));
        var validation = workspace.Validate(s); Assert.True(validation.Valid, JsonSerializer.Serialize(validation));
        workspace.Save(s.Id, 1, "Magic.esp", false);
        using var read = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(directory, "Magic.esp"), SkyrimRelease.SkyrimSE);
        var effect = Assert.Single(Assert.Single(read.Spells).Effects);
        Assert.Equal(5, effect.Data!.Magnitude);
        Assert.Equal(read.Quests.First().FormKey, ((IGetStageConditionDataGetter)effect.Conditions[0].Data).Quest.Link.FormKey);
        Ok(tools.Get(s.Id, read.Spells.First().FormKey.ToString()));
    }

    [Fact]
    public void ExteriorCellNegativeCoordinatesAndPlacementRoundTrip()
    {
        var s = workspace.Create("World.esp", "esp", "", "", []);
        Ok(tools.CreateRecord(s.Id, 0, "Worldspace", "SehtWorld"));
        Ok(tools.CreateRecord(s.Id, 1, "Static", "SehtObject"));
        Ok(world.Exterior(s.Id, 2, "000800:World.esp", "SehtCell", -1, -33));
        Ok(world.Place(s.Id, 3, "000802:World.esp", "000801:World.esp", "SehtPlaced", x: 12, y: 24, z: 36));
        workspace.Save(s.Id, 4, "World.esp", false);
        using var read = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(directory, "World.esp"), SkyrimRelease.SkyrimSE);
        var block = Assert.Single(Assert.Single(read.Worldspaces).SubCells);
        Assert.Equal(-1, block.BlockNumberX); Assert.Equal(-2, block.BlockNumberY);
        var cell = Assert.Single(Assert.Single(block.Items).Items);
        var placed = (IPlacedObjectGetter)Assert.Single(cell.Temporary);
        Assert.Equal(12, placed.Placement!.Position.X); Assert.Equal(24, placed.Placement.Position.Y); Assert.Equal(36, placed.Placement.Position.Z);
        Ok(tools.Get(s.Id, placed.FormKey.ToString()));
    }

    [Fact]
    public void NestedOverrideKeepsParentAndDoesNotCopySiblings()
    {
        var original = workspace.Create("Rooms.esm", "esm", "", "", []);
        Ok(tools.CreateRecord(original.Id, 0, "Static", "BaseStatic"));
        Ok(tools.CreateRecord(original.Id, 1, "Cell", "BaseCell"));
        Ok(world.Place(original.Id, 2, "000801:Rooms.esm", "000800:Rooms.esm", "First"));
        Ok(world.Place(original.Id, 3, "000801:Rooms.esm", "000800:Rooms.esm", "Second"));
        workspace.Save(original.Id, 4, "Rooms.esm", false);
        var patch = workspace.Create("RoomPatch.esp", "esp", "", "", ["Rooms.esm"]);
        Ok(tools.Override(patch.Id, 0, "000802:Rooms.esm", J("""{"Placement":{"Position":{"Z":40}}}""")));
        Assert.Equal(2, patch.Mod.EnumerateMajorRecords().Count());
        workspace.Save(patch.Id, 1, "RoomPatch.esp", false);
        var placed = (PlacedObject)PluginWorkspace.Find(patch, "000802:Rooms.esm"); Assert.Equal(40, placed.Placement!.Position.Z);
    }

    [Fact]
    public void InvalidVectorFieldFailsRatherThanBeingIgnored()
    {
        var s = workspace.Create("BadVector.esp", "esp", "", "", []);
        Ok(tools.CreateRecord(s.Id, 0, "Worldspace", "World"));
        var result = tools.Update(s.Id, 1, "000800:BadVector.esp", J("""{"WorldMapCellOffset":{"Typo":5}}"""));
        Assert.True(result.IsError); Assert.Equal(1, s.Revision);
    }

    public void Dispose() { workspace.Dispose(); Directory.Delete(directory, true); }
}
