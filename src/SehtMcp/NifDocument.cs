using System.Numerics;
using System.Text;

namespace SehtMcp;

public sealed record NifBlock(int Index, string Type, int Offset, int Size);
public sealed record NifMesh(int Block, string Name, Vector3[] Vertices, ushort[] Indices, int Skin, int Shader);

/// <summary>Read-only Skyrim NIF inspection, based on niftools/nifxml. Never rewrites NIF bytes.</summary>
public sealed class NifDocument
{
    public uint Version { get; private set; }
    public uint UserVersion { get; private set; }
    public uint BethesdaVersion { get; private set; }
    public string Author { get; private set; } = "";
    public List<NifBlock> Blocks { get; } = [];
    public List<string> Strings { get; } = [];
    public List<string> Textures { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<NifMesh> Meshes { get; } = [];
    private readonly byte[] bytes;
    private readonly Dictionary<int, (Matrix4x4 Transform, int[] Children)> nodes = [];
    private readonly Dictionary<int, Matrix4x4> shapes = [];

    public NifDocument(byte[] data)
    {
        bytes = data;
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        var header = new List<byte>();
        byte c;
        do { c = r.ReadByte(); header.Add(c); if (header.Count > 128) throw new InvalidDataException("Invalid NIF header."); } while (c != 10);
        if (!Encoding.ASCII.GetString(header.ToArray()).StartsWith("Gamebryo File Format")) throw new InvalidDataException("Not a supported Gamebryo NIF.");
        Version = r.ReadUInt32();
        if (Version != 0x14020007 || r.ReadByte() != 1) throw new InvalidDataException("This reader supports little-endian NIF 20.2.0.7 (Skyrim LE/SE).");
        UserVersion = r.ReadUInt32();
        var count = Count(r, 200000);
        if (UserVersion != 12) throw new InvalidDataException("Only Bethesda user version 12 is supported.");
        BethesdaVersion = r.ReadUInt32();
        if (BethesdaVersion is not (83 or 100)) throw new InvalidDataException("Only Skyrim LE (83) and SE (100) streams are supported.");
        Author = ShortString(r); ShortString(r); ShortString(r);
        var types = new string[r.ReadUInt16()];
        for (var i = 0; i < types.Length; i++) types[i] = SizedString(r);
        var indices = new ushort[count]; for (var i = 0; i < count; i++) indices[i] = r.ReadUInt16();
        var sizes = new int[count]; for (var i = 0; i < count; i++) sizes[i] = Count(r, data.Length);
        var strings = Count(r, 200000); _ = r.ReadUInt32();
        for (var i = 0; i < strings; i++) Strings.Add(SizedString(r));
        var groups = Count(r, 200000); for (var i = 0; i < groups; i++) r.ReadUInt32();
        long offset = r.BaseStream.Position;
        for (var i = 0; i < count; i++)
        {
            if (indices[i] >= types.Length || offset + sizes[i] > data.Length) throw new InvalidDataException("Invalid NIF block table.");
            Blocks.Add(new(i, types[indices[i]], (int)offset, sizes[i])); offset += sizes[i];
        }
        if (offset + 4 > data.Length) throw new InvalidDataException("Missing NIF footer.");
        r.BaseStream.Position = offset;
        var roots = Count(r, count);
        for (var i = 0; i < roots; i++) { var root = r.ReadInt32(); if (root < -1 || root >= count) throw new InvalidDataException("Invalid root reference."); }
        if (r.BaseStream.Position != data.Length) Warnings.Add("Trailing bytes after the NIF footer.");
        foreach (var block in Blocks) ParseBlock(block);
        ApplyWorldTransforms();
        if (BethesdaVersion == 83) Warnings.Add("LE geometry preview is unsupported; block and texture inspection remains available.");
        if (Blocks.Any(b => b.Type.Contains("Skin"))) Warnings.Add("Preview shows stored geometry; bone deformation and animation are not evaluated.");
    }

    private static int Count(BinaryReader r, int max)
    {
        var value = r.ReadUInt32();
        if (value > max) throw new InvalidDataException("NIF count exceeds bounds.");
        return (int)value;
    }
    private static string SizedString(BinaryReader r)
    {
        var count = Count(r, 1024 * 1024);
        return Encoding.UTF8.GetString(Exact(r, count)).TrimEnd('\0');
    }
    private static string ShortString(BinaryReader r) => Encoding.UTF8.GetString(Exact(r, r.ReadByte())).TrimEnd('\0');
    private static byte[] Exact(BinaryReader r, int n) { var b = r.ReadBytes(n); if (b.Length != n) throw new EndOfStreamException(); return b; }
    private static Vector3 Vector(BinaryReader r)
    {
        var v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new InvalidDataException("Non-finite vertex or transform.");
        return v;
    }
    private (string Name, Matrix4x4 Matrix) AVObject(BinaryReader r)
    {
        var index = r.ReadInt32();
        if (index < -1 || index >= Strings.Count) throw new InvalidDataException("Invalid name index.");
        var name = index == -1 ? "" : Strings[index];
        var extras = Count(r, Blocks.Count); Exact(r, checked(extras * 4));
        r.ReadInt32(); r.ReadUInt32(); // controller, flags
        var translation = Vector(r);
        // NIF stores a column-vector rotation; System.Numerics uses row vectors.
        var a = Vector(r); var b = Vector(r); var c = Vector(r);
        var scale = r.ReadSingle();
        if (!float.IsFinite(scale)) throw new InvalidDataException("Invalid scale.");
        var matrix = new Matrix4x4(a.X, b.X, c.X, 0, a.Y, b.Y, c.Y, 0, a.Z, b.Z, c.Z, 0, 0, 0, 0, 1) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(translation);
        r.ReadInt32(); // collision object
        return (name, matrix);
    }
    private void ParseBlock(NifBlock block)
    {
        using var r = new BinaryReader(new MemoryStream(bytes, block.Offset, block.Size, false));
        try
        {
            if (block.Type == "BSShaderTextureSet")
            {
                var count = Count(r, 64);
                for (var i = 0; i < count; i++) { var texture = SizedString(r); if (texture.Length > 0) Textures.Add(texture); }
            }
            else if (new[] { "NiNode", "BSFadeNode", "BSLeafAnimNode", "BSOrderedNode", "BSMultiBoundNode", "NiSwitchNode", "NiBillboardNode" }.Contains(block.Type))
            {
                var av = AVObject(r);
                var children = new int[Count(r, Blocks.Count)];
                for (var i = 0; i < children.Length; i++) { children[i] = r.ReadInt32(); if (children[i] < -1 || children[i] >= Blocks.Count) throw new InvalidDataException("Invalid child index."); }
                nodes[block.Index] = (av.Matrix, children);
            }
            else if (BethesdaVersion == 100 && new[] { "BSTriShape", "BSSubIndexTriShape", "BSDynamicTriShape", "BSMeshLODTriShape" }.Contains(block.Type))
            {
                var av = AVObject(r); Vector(r); r.ReadSingle();
                var skin = r.ReadInt32(); var shader = r.ReadInt32(); var alpha = r.ReadInt32();
                foreach (var reference in new[] { skin, shader, alpha }) if (reference < -1 || reference >= Blocks.Count) throw new InvalidDataException("Invalid shape reference.");
                var desc = r.ReadUInt64();
                var triangles = r.ReadUInt16(); var vertices = r.ReadUInt16(); var size = r.ReadUInt32();
                var stride = (int)(desc & 15) * 4;
                if (size == 0 || (desc >> 44 & 1) == 0) { Warnings.Add($"Block {block.Index} {block.Type}: vertex positions are external or dynamic; omitted from preview."); return; }
                if (stride < 12 || size != (long)stride * vertices + triangles * 6L || size > block.Size) throw new InvalidDataException("Invalid vertex data layout.");
                var points = new Vector3[vertices];
                for (var i = 0; i < vertices; i++) { points[i] = Vector(r); Exact(r, stride - 12); }
                var indices = new ushort[triangles * 3];
                for (var i = 0; i < indices.Length; i++) { indices[i] = r.ReadUInt16(); if (indices[i] >= vertices) throw new InvalidDataException("Triangle index out of bounds."); }
                if (Meshes.Sum(m => m.Vertices.Length) + vertices > 2000000) throw new InvalidDataException("Geometry budget exceeded.");
                Meshes.Add(new(block.Index, av.Name, points, indices, skin, shader)); shapes[block.Index] = av.Matrix;
            }
            else if (block.Type.Contains("TriShape") || block.Type.Contains("TriStrips")) Warnings.Add($"Block {block.Index} {block.Type}: unsupported geometry; omitted from preview.");
            else if (block.Type.EndsWith("Node")) Warnings.Add($"Block {block.Index} {block.Type}: unsupported node; its transform is not evaluated in preview.");
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or OverflowException)
        {
            throw new InvalidDataException($"Malformed {block.Type} block {block.Index}: {ex.Message}", ex);
        }
    }
    private void ApplyWorldTransforms()
    {
        var parents = new Dictionary<int, int>();
        foreach (var (index, node) in nodes)
            foreach (var child in node.Children.Where(c => c >= 0))
                if (!parents.TryAdd(child, index)) Warnings.Add($"Block {child} has multiple parents; preview uses the first.");
        Matrix4x4 World(int index, HashSet<int> visited)
        {
            if (!visited.Add(index) || visited.Count > 512) throw new InvalidDataException("Cyclic or excessively deep NIF scene graph.");
            var local = nodes.TryGetValue(index, out var node) ? node.Transform : shapes.GetValueOrDefault(index, Matrix4x4.Identity);
            return parents.TryGetValue(index, out var parent) ? local * World(parent, visited) : local;
        }
        foreach (var index in nodes.Keys) _ = World(index, []);
        foreach (var mesh in Meshes)
        {
            var transform = World(mesh.Block, []);
            for (var i = 0; i < mesh.Vertices.Length; i++) mesh.Vertices[i] = Vector3.Transform(mesh.Vertices[i], transform);
        }
    }
    public object Summary() => new
    {
        version = "20.2.0.7", userVersion = UserVersion, bethesdaVersion = BethesdaVersion, author = Author,
        blockCount = Blocks.Count, types = Blocks.GroupBy(b => b.Type).ToDictionary(g => g.Key, g => g.Count()),
        textures = Textures.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        geometry = Meshes.Select(m => new { block = m.Block, name = m.Name, vertices = m.Vertices.Length, triangles = m.Indices.Length / 3, skin = m.Skin, shader = m.Shader }).ToArray(), warnings = Warnings
    };
    public string BlockHex(int index, int offset, int count)
    {
        if (index < 0 || index >= Blocks.Count || offset < 0 || count is < 1 or > 4096) throw new ArgumentException("Invalid block, offset, or count (1..4096).");
        var b = Blocks[index];
        if (offset > b.Size) throw new ArgumentException("Offset exceeds block.");
        return Convert.ToHexString(bytes.AsSpan(b.Offset + offset, Math.Min(count, b.Size - offset)));
    }
}
