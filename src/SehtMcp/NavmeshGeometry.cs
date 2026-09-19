using System.ComponentModel;
using System.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace SehtMcp;

public sealed class NavmeshBuildSettings
{
    [Description("Horizontal voxel size in Skyrim units. Smaller values cost more memory and time.")]
    public float CellSize { get; set; } = 8;
    public float CellHeight { get; set; } = 2;
    public float AgentHeight { get; set; } = 128;
    public float AgentRadius { get; set; } = 16;
    public float MaxClimb { get; set; } = 32;
    public float MaxSlope { get; set; } = 45;
    public float MaxEdgeLength { get; set; } = 256;
    public float MaxSimplificationError { get; set; } = 1.3f;
    [Description("Minimum isolated region area in square Skyrim units. Removes small unreachable fragments; set zero to retain all regions.")]
    public float MinRegionArea { get; set; } = 4096;
    public float MergeRegionArea { get; set; } = 16384;
    [Description("Maximum distance in Skyrim units from a walkable seed to its nearest generated surface.")]
    public float SeedMaxDistance { get; set; } = 64;

    public void Validate()
    {
        if (new[] { CellSize, CellHeight, AgentHeight, AgentRadius, MaxClimb, MaxSlope, MaxEdgeLength, MaxSimplificationError, MinRegionArea, MergeRegionArea, SeedMaxDistance }.Any(x => !float.IsFinite(x)))
            throw new ArgumentException("Navmesh settings must be finite.");
        if (CellSize is < 0.5f or > 128 || CellHeight is < 0.25f or > 32 || AgentHeight < 3 * CellHeight || AgentHeight > 1024 ||
            AgentRadius is < 0 or > 256 || MaxClimb < 0 || MaxClimb >= AgentHeight || MaxSlope is < 0 or >= 85 ||
            MaxEdgeLength < CellSize || MaxEdgeLength > 4096 || MaxSimplificationError is < 0.1f or > 4 ||
            MinRegionArea is < 0 or > 100000000 || MergeRegionArea is < 0 or > 100000000 || SeedMaxDistance is <= 0 or > 4096)
            throw new ArgumentException("Invalid navmesh settings. Require cellSize 0.5..128, cellHeight 0.25..32, agentHeight 3*cellHeight..1024, radius 0..256, climb >=0 and <height, slope 0..<85, edgeLength cellSize..4096, simplificationError 0.1..4.");
    }
}

public sealed record NavmeshGeometry(Vector3[] Vertices, int[][] Triangles)
{
    public static Vector3 ClosestPoint(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var normal = Vector3.Cross(ab, ac);
        var projected = point - normal * (Vector3.Dot(point - a, normal) / normal.LengthSquared());
        var v = projected - a; var d00 = Vector3.Dot(ab, ab); var d01 = Vector3.Dot(ab, ac); var d11 = Vector3.Dot(ac, ac);
        var d20 = Vector3.Dot(v, ab); var d21 = Vector3.Dot(v, ac); var denominator = d00 * d11 - d01 * d01;
        var u = (d11 * d20 - d01 * d21) / denominator; var w = (d00 * d21 - d01 * d20) / denominator;
        if (u >= 0 && w >= 0 && u + w <= 1) return projected;
        static Vector3 Edge(Vector3 p, Vector3 x, Vector3 y) => x + (y - x) * Math.Clamp(Vector3.Dot(p - x, y - x) / Vector3.DistanceSquared(x, y), 0, 1);
        return new[] { Edge(point, a, b), Edge(point, b, c), Edge(point, c, a) }.MinBy(p => Vector3.DistanceSquared(point, p));
    }

    public static NavmeshGeometry Parse(float[][] vertices, int[][] triangles)
    {
        if (vertices is null || vertices.Length is < 3 or > 250000 || triangles is null || triangles.Length is < 1 or > 250000)
            throw new ArgumentException("Provide 3..250000 vertices and 1..250000 triangles.");
        if (vertices.Any(v => v is null || v.Length != 3 || v.Any(x => !float.IsFinite(x) || Math.Abs(x) > 1000000)))
            throw new ArgumentException("Each vertex must be [x,y,z], finite and within +/-1,000,000 Skyrim units.");
        var points = vertices.Select(v => new Vector3(v[0], v[1], v[2])).ToArray();
        foreach (var t in triangles)
        {
            if (t is null || t.Length != 3 || t.Any(i => i < 0 || i >= points.Length)) throw new ArgumentException("Each triangle must contain three valid zero-based vertex indices.");
            if (Vector3.Cross(points[t[1]] - points[t[0]], points[t[2]] - points[t[0]]).LengthSquared() < 0.000001f)
                throw new ArgumentException("Input contains a degenerate triangle.");
        }
        return new(points, triangles.Select(t => t.ToArray()).ToArray());
    }

    public NavmeshGeometry Bake(NavmeshBuildSettings settings, float[][]? walkableSeeds = null)
    {
        settings.Validate();
        // A proper rotation (x,y,z) -> (x,z,-y) preserves winding while making Recast Y-up.
        var input = new RcSampleInputGeomProvider(Vertices.SelectMany(v => new[] { v.X, v.Z, -v.Y }).ToArray(), Triangles.SelectMany(t => t).ToArray());
        var min = input.GetMeshBoundsMin(); var max = input.GetMeshBoundsMax();
        var width = (max.X - min.X) / settings.CellSize + 1;
        var depth = (max.Z - min.Z) / settings.CellSize + 1;
        if (width < 2 || depth < 2 || width * depth > 1000000 || (max.Y - min.Y) / settings.CellHeight > 30000)
            throw new ArgumentException("Geometry has insufficient horizontal extent or exceeds the one-million-voxel-column/30000-height-step budget. Increase voxel sizes or divide the input.");
        double rasterWork = 0;
        foreach (var t in Triangles)
        {
            var a = Vertices[t[0]]; var b = Vertices[t[1]]; var c = Vertices[t[2]];
            rasterWork += ((Math.Max(a.X, Math.Max(b.X, c.X)) - Math.Min(a.X, Math.Min(b.X, c.X))) / settings.CellSize + 1) *
                          ((Math.Max(a.Y, Math.Max(b.Y, c.Y)) - Math.Min(a.Y, Math.Min(b.Y, c.Y))) / settings.CellSize + 1);
        }
        if (rasterWork > 50000000) throw new ArgumentException("Rasterization exceeds the 50-million-column-visit budget. Simplify input or increase cellSize.");
        var cfg = new RcConfig(false, 0, 0, 0, RcPartition.WATERSHED, settings.CellSize, settings.CellHeight,
            settings.MaxSlope, settings.AgentHeight, settings.AgentRadius, settings.MaxClimb,
            settings.MinRegionArea, settings.MergeRegionArea, settings.MaxEdgeLength, settings.MaxSimplificationError, 3, 6, 1,
            true, true, true, new RcAreaModification(1), false);
        var built = new RcBuilder().Build(input, new RcBuilderConfig(cfg, min, max), false);
        var mesh = built.Mesh;
        if (mesh.npolys == 0)
            throw new ArgumentException("No walkable surface survived baking. Check upward winding, clearance, slope, agent radius, and voxel sizes.");
        if (mesh.nverts > 250000 || mesh.npolys > 250000) throw new ArgumentException("Baked mesh exceeds 250000 vertices/triangles. Increase voxel sizes or simplify the scene.");
        var vertices = new Vector3[mesh.nverts]; var triangles = new int[mesh.npolys][];
        for (var i = 0; i < mesh.nverts; i++)
        {
            vertices[i] = new(mesh.bmin.X + mesh.verts[3 * i] * mesh.cs,
                -(mesh.bmin.Z + mesh.verts[3 * i + 2] * mesh.cs), mesh.bmin.Y + mesh.verts[3 * i + 1] * mesh.ch);
        }
        // Export Recast's shared polygon topology directly (MaxVertsPerPoly=3).
        // Its optional per-polygon detail triangulation can produce folds/overlaps around
        // complex stacked kit geometry. The contour mesh is the navigation topology;
        // heights are quantized to cellHeight and do not promise a detail-surface fit.
        for (var i = 0; i < mesh.npolys; i++)
        {
            var offset = i * mesh.nvp * 2;
            triangles[i] = [mesh.polys[offset], mesh.polys[offset + 1], mesh.polys[offset + 2]];
        }
        var geometry = new NavmeshGeometry(vertices, triangles);
        return walkableSeeds is null ? geometry : geometry.KeepSeedComponents(walkableSeeds, settings.SeedMaxDistance);
    }

    private NavmeshGeometry KeepSeedComponents(float[][] seeds, float maxDistance)
    {
        if (seeds.Length is < 1 or > 256 || seeds.Any(p => p is null || p.Length != 3 || p.Any(v => !float.IsFinite(v))))
            throw new ArgumentException("walkableSeeds must contain 1..256 finite [x,y,z] points.");
        var parts = Normalize().Components(); var keep = new HashSet<int>();
        foreach (var seed in seeds)
        {
            var point = new Vector3(seed[0], seed[1], seed[2]); var best = maxDistance * maxDistance; var selected = -1;
            for (var i = 0; i < parts.Length; i++) foreach (var t in parts[i].Triangles)
            {
                var part = parts[i]; var nearest = ClosestPoint(point, part.Vertices[t[0]], part.Vertices[t[1]], part.Vertices[t[2]]);
                var distance = Vector3.DistanceSquared(point, nearest);
                if (distance <= best) { best = distance; selected = i; }
            }
            if (selected < 0) throw new ArgumentException("A walkable seed has no generated surface within seedMaxDistance.");
            keep.Add(selected);
        }
        var vertices = new List<Vector3>(); var triangles = new List<int[]>();
        foreach (var index in keep.Order())
        {
            var offset = vertices.Count; vertices.AddRange(parts[index].Vertices);
            triangles.AddRange(parts[index].Triangles.Select(t => t.Select(v => v + offset).ToArray()));
        }
        return new(vertices.ToArray(), triangles.ToArray());
    }

    public NavmeshGeometry Normalize()
    {
        var points = new List<Vector3>(); var indices = new Dictionary<Vector3, int>();
        var map = new int[Vertices.Length];
        for (var i = 0; i < Vertices.Length; i++)
        {
            if (!indices.TryGetValue(Vertices[i], out var index)) { index = points.Count; indices.Add(Vertices[i], index); points.Add(Vertices[i]); }
            map[i] = index;
        }
        var triangles = Triangles.Select(t => t.Select(i => map[i]).ToArray()).ToArray();
        var unique = new HashSet<(int, int, int)>();
        foreach (var t in triangles)
        {
            var normal = Vector3.Cross(points[t[1]] - points[t[0]], points[t[2]] - points[t[0]]);
            if (Math.Abs(normal.Z) < 0.0001f) throw new ArgumentException("A navmesh triangle is degenerate or vertical. Supply walkable surfaces, or use navmesh_generate to filter scene geometry.");
            if (normal.Z < 0) (t[1], t[2]) = (t[2], t[1]);
            var sorted = t.Order().ToArray();
            if (!unique.Add((sorted[0], sorted[1], sorted[2]))) throw new ArgumentException("Duplicate navmesh triangle.");
        }
        return new(points.ToArray(), triangles);
    }

    public int[][] Adjacency()
    {
        var neighbors = Triangles.Select(_ => new[] { -1, -1, -1 }).ToArray();
        var edges = new Dictionary<(int, int), (int Triangle, int Edge, int Start)>();
        for (var i = 0; i < Triangles.Length; i++) for (var e = 0; e < 3; e++)
        {
            var a = Triangles[i][e]; var b = Triangles[i][(e + 1) % 3]; var key = (Math.Min(a, b), Math.Max(a, b));
            if (!edges.TryGetValue(key, out var prior)) edges.Add(key, (i, e, a));
            else
            {
                if (neighbors[prior.Triangle][prior.Edge] != -1) throw new ArgumentException("Non-manifold navmesh: more than two triangles share an edge.");
                if (prior.Start == a) throw new ArgumentException("Overlapping or inconsistently wound triangles share an edge.");
                neighbors[i][e] = prior.Triangle; neighbors[prior.Triangle][prior.Edge] = i;
            }
        }
        return neighbors;
    }

    public NavmeshGeometry[] Components()
    {
        var adjacency = Adjacency(); var visited = new bool[Triangles.Length]; var parts = new List<NavmeshGeometry>();
        for (var start = 0; start < Triangles.Length; start++)
        {
            if (visited[start]) continue;
            var work = new Queue<int>(); var faces = new List<int[]>(); work.Enqueue(start); visited[start] = true;
            while (work.TryDequeue(out var i))
            {
                faces.Add(Triangles[i]);
                foreach (var neighbor in adjacency[i].Where(n => n >= 0 && !visited[n])) { visited[neighbor] = true; work.Enqueue(neighbor); }
            }
            var used = faces.SelectMany(t => t).Distinct().Order().ToArray();
            if (used.Length > short.MaxValue || faces.Count > short.MaxValue) throw new ArgumentException("A connected navmesh exceeds 32767 vertices or triangles. Increase simplification or split the design.");
            var remap = used.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
            parts.Add(new(used.Select(i => Vertices[i]).ToArray(), faces.Select(t => t.Select(i => remap[i]).ToArray()).ToArray()));
        }
        if (parts.Count > 256) throw new ArgumentException("More than 256 disconnected navmesh components. Simplify or restrict the geometry.");
        return parts.ToArray();
    }
}
