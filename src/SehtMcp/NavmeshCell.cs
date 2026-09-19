using System.Numerics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SehtMcp;

// Resolve through record ownership, not just links or an exterior flag on an orphan CELL.
public sealed record NavmeshCell(Cell Cell, Worldspace? World)
{
    public static IEnumerable<Cell> Cells(Worldspace world) => world.SubCells.SelectMany(b => b.Items).SelectMany(b => b.Items);

    public static NavmeshCell Resolve(PluginSession session, string key)
    {
        var cell = PluginWorkspace.Find(session, key) as Cell ?? throw new ArgumentException("Expected an editable Cell.");
        if (cell.IsDeleted || cell.FormKey.ModKey != session.Mod.ModKey)
            throw new ArgumentException("Navigation authoring requires a new, non-deleted cell owned by this plugin; inherited cells require CK finalization.");
        if (cell.Flags.HasFlag(Cell.Flag.IsInteriorCell))
        {
            if (!session.Mod.Cells.SelectMany(b => b.SubBlocks).SelectMany(b => b.Cells).Any(c => c.FormKey == cell.FormKey))
                throw new ArgumentException("Interior cell is not stored in an interior cell group.");
            return new(cell, null);
        }
        var world = session.Mod.Worldspaces.SingleOrDefault(w => Cells(w).Any(c => c.FormKey == cell.FormKey));
        if (world is null || cell.Grid is null) throw new ArgumentException("Exterior cell must belong to a worldspace's grid, not its persistent cell or an interior group.");
        if (world.IsDeleted || world.FormKey.ModKey != session.Mod.ModKey || world.Parent?.Worldspace.IsNull == false)
            throw new ArgumentException("Isolated exterior navigation requires a new worldspace owned by this plugin with no parent worldspace. Existing worlds and inherited navigation require CK finalization.");
        var grid = cell.Grid.Point;
        if (grid.X is < short.MinValue or > short.MaxValue || grid.Y is < short.MinValue or > short.MaxValue || Cells(world).Count(c => c.Grid?.Point == grid) != 1)
            throw new ArgumentException("Exterior cell grid must be unique in its worldspace and fit signed 16-bit coordinates.");
        return new(cell, world);
    }

    // Both NVNM and NVMI serialize Grid Y, Grid X. Mutagen exposes the raw pair as X,Y.
    public P2Int16 SerializedCoordinates => new(checked((short)Cell.Grid!.Point.Y), checked((short)Cell.Grid.Point.X));
    public ANavmeshParent Parent() => World is null
        ? new CellNavmeshParent { Parent = Cell.FormKey.ToLink<ICellGetter>() }
        : new WorldspaceNavmeshParent { Parent = World.FormKey.ToLink<IWorldspaceGetter>(), Coordinates = SerializedCoordinates };

    public bool Contains(Vector3 position) => World is null ||
        (float.IsFinite(position.X) && float.IsFinite(position.Y) &&
         position.X >= Cell.Grid!.Point.X * 4096f && position.X < (Cell.Grid.Point.X + 1) * 4096f &&
         position.Y >= Cell.Grid.Point.Y * 4096f && position.Y < (Cell.Grid.Point.Y + 1) * 4096f);

    public void CheckBounds(IEnumerable<Vector3> vertices)
    {
        if (World is not null && vertices.Any(v => !Contains(v)))
            throw new ArgumentException($"Isolated exterior navmesh must stay inside grid ({Cell.Grid!.Point.X},{Cell.Grid.Point.Y}): X [{Cell.Grid.Point.X * 4096f},{(Cell.Grid.Point.X + 1) * 4096f}), Y [{Cell.Grid.Point.Y * 4096f},{(Cell.Grid.Point.Y + 1) * 4096f}). Use world-space vertices. Cross-cell splitting/stitching is not supported.");
    }

    public IEnumerable<IPlacedObjectGetter> Objects() => Cell.Temporary.Concat(Cell.Persistent).OfType<IPlacedObjectGetter>()
        .Concat(World?.TopCell?.Persistent.OfType<IPlacedObjectGetter>().Where(p => p.Placement is not null && Contains(NavmeshRecords.Point(p.Placement.Position))) ?? []);

    public Cell PersistentCell(PluginSession session)
    {
        if (World is null) return Cell;
        if (World.TopCell is { } existing)
        {
            if (existing.IsDeleted || existing.Flags.HasFlag(Cell.Flag.IsInteriorCell) || (existing.MajorRecordFlagsRaw & 0x400) == 0)
                throw new ArgumentException("Worldspace persistent cell is deleted, interior, or lacks its Persistent flag.");
            return existing;
        }
        if (session.Mod.IsSmallMaster && session.Mod.ModHeader.Stats.NextFormID > 0xFFF) throw new ArgumentException("Light FormID range exhausted creating the worldspace persistent cell.");
        return World.TopCell = new Cell(session.Mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { MajorRecordFlagsRaw = 0x400 };
    }
}
