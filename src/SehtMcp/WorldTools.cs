using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SehtMcp;

[McpServerToolType]
public sealed class WorldTools(PluginWorkspace workspace)
{
    [McpServerTool(Name = "cell_create_exterior", Destructive = false), Description("Create an exterior Cell at grid x,y in an editable Worldspace, arranging correct exterior block/subblock groups (including negative coordinates). The worldspace must be created or overridden first. No terrain or navmesh is generated.")]
    public CallToolResult Exterior(string session, long expectedRevision, string worldspace, string editorId, int x, int y, JsonObject? fields = null)
    {
        lock (workspace.Gate) return ToolResult.Run(() => workspace.Mutate(session, expectedRevision, s =>
        {
            if (x is < -32768 or > 32767 || y is < -32768 or > 32767) throw new ArgumentException("Cell coordinates must fit signed 16-bit range.");
            var world = PluginWorkspace.Find(s, worldspace) as Worldspace ?? throw new ArgumentException("Expected editable Worldspace.");
            if (world.SubCells.SelectMany(b => b.Items).SelectMany(b => b.Items).Any(c => c.Grid?.Point == new P2Int(x, y))) throw new ArgumentException("Worldspace already has an editable cell at those coordinates.");
            if (s.Mod.IsSmallMaster && s.Mod.ModHeader.Stats.NextFormID > 0xFFF) throw new ArgumentException("Light FormID range exhausted.");
            var cell = new Cell(s.Mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = editorId, Grid = new CellGrid { Point = new P2Int(x, y) } };
            if (fields is not null) RecordCodec.Apply(cell, fields);
            cell.Flags &= ~Cell.Flag.IsInteriorCell;
            cell.Grid = new CellGrid { Point = new P2Int(x, y) };
            // Arithmetic right shift implements floor division for negative grid coordinates.
            var bx = (short)(x >> 5); var by = (short)(y >> 5); var sx = (short)(x >> 3); var sy = (short)(y >> 3);
            var block = world.SubCells.FirstOrDefault(b => b.BlockNumberX == bx && b.BlockNumberY == by);
            if (block is null) { block = new WorldspaceBlock { BlockNumberX = bx, BlockNumberY = by, GroupType = GroupTypeEnum.ExteriorCellBlock }; world.SubCells.Add(block); }
            var sub = block.Items.FirstOrDefault(b => b.BlockNumberX == sx && b.BlockNumberY == sy);
            if (sub is null) { sub = new WorldspaceSubBlock { BlockNumberX = sx, BlockNumberY = sy, GroupType = GroupTypeEnum.ExteriorCellSubBlock }; block.Items.Add(sub); }
            sub.Items.Add(cell);
            return PluginWorkspace.Brief(cell);
        }));
    }

    [McpServerTool(Name = "cell_place", Destructive = false), Description("Place a base object or NPC into an editable Cell. Automatically selects REFR/ACHR and persistent/temporary group. Exterior persistent references are stored in the owning worldspace's persistent cell, created if needed. Exterior positions use world-space Skyrim units; rotation uses radians. Base FormKey must resolve. Does not generate navmesh or collision.")]
    public CallToolResult Place(string session, long expectedRevision, string cell, string baseFormKey, string editorId, float x = 0, float y = 0, float z = 0, float rx = 0, float ry = 0, float rz = 0, float scale = 1, bool persistent = false)
    {
        lock (workspace.Gate) return ToolResult.Run(() => workspace.Mutate(session, expectedRevision, s =>
        {
            if (new[] { x, y, z, rx, ry, rz, scale }.Any(v => !float.IsFinite(v)) || scale is <= 0 or > 10) throw new ArgumentException("Transforms must be finite; scale must be >0 and <=10.");
            var target = PluginWorkspace.Find(s, baseFormKey, true);
            var type = target is INpcGetter ? "PlacedNpc" : "PlacedObject";
            var fields = new JsonObject { ["Base"] = baseFormKey, ["Scale"] = scale, ["Placement"] = new JsonObject { ["Position"] = new JsonObject { ["X"] = x, ["Y"] = y, ["Z"] = z }, ["Rotation"] = new JsonObject { ["X"] = rx, ["Y"] = ry, ["Z"] = rz } } };
            var owner = PluginWorkspace.Find(s, cell) as Cell ?? throw new ArgumentException("Expected editable Cell.");
            if (persistent && !owner.Flags.HasFlag(Cell.Flag.IsInteriorCell))
            {
                var world = s.Mod.Worldspaces.SingleOrDefault(w => NavmeshCell.Cells(w).Any(c => c.FormKey == owner.FormKey));
                if (world is not null)
                {
                    var context = new NavmeshCell(owner, world);
                    if (owner.Grid is null || !context.Contains(new(x, y, z))) throw new ArgumentException("Exterior placement must lie inside the target grid cell, in world coordinates.");
                    cell = context.PersistentCell(s).FormKey.ToString();
                }
                else if (!s.Mod.Worldspaces.Any(w => w.TopCell?.FormKey == owner.FormKey)) throw new ArgumentException("Exterior cell has no owning worldspace.");
            }
            return PluginWorkspace.Brief(PluginWorkspace.CreateRecord(s, type, editorId, fields, cell, persistent ? "Persistent" : "Temporary"));
        }));
    }
}
