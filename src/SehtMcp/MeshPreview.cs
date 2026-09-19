using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace SehtMcp;

/// <summary>Portable, dependency-free, untextured diagnostic rasterizer.</summary>
public static class MeshPreview
{
    public static byte[] Render(NifDocument nif, int width, int height, float yaw, float pitch)
    {
        if (width is < 64 or > 1024 || height is < 64 or > 1024 || !float.IsFinite(yaw) || !float.IsFinite(pitch)) throw new ArgumentException("Preview size must be 64..1024 and angles finite.");
        var vertices = nif.Meshes.SelectMany(m => m.Vertices).ToArray();
        if (vertices.Length == 0) throw new InvalidOperationException("No supported embedded SSE geometry found. Inspect warnings or use NifSkope.");
        var view = Matrix4x4.CreateRotationZ(yaw * MathF.PI / 180) * Matrix4x4.CreateRotationX(pitch * MathF.PI / 180);
        var transformed = vertices.Select(v => Vector3.Transform(v, view)).ToArray();
        var min = transformed.Aggregate(Vector3.Min); var max = transformed.Aggregate(Vector3.Max);
        if (!float.IsFinite(min.X + min.Y + min.Z + max.X + max.Y + max.Z)) throw new InvalidDataException("Non-finite geometry bounds.");
        var center = (min + max) / 2;
        var scale = 0.86f * Math.Min(width / Math.Max(max.X - min.X, 0.01f), height / Math.Max(max.Z - min.Z, 0.01f));
        var pixels = new byte[width * height * 3];
        for (var i = 0; i < pixels.Length; i += 3) { pixels[i] = 23; pixels[i + 1] = 28; pixels[i + 2] = 37; }
        var zbuffer = Enumerable.Repeat(float.PositiveInfinity, width * height).ToArray();
        Vector3 Project(Vector3 v) { v = Vector3.Transform(v, view) - center; return new(width / 2f + v.X * scale, height / 2f - v.Z * scale, v.Y); }
        static float Edge(Vector3 a, Vector3 b, float x, float y) => (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);
        var faceBudget = 1500000;
        long pixelBudget = 200000000;
        foreach (var mesh in nif.Meshes)
        {
            var points = mesh.Vertices.Select(Project).ToArray();
            for (var i = 0; i < mesh.Indices.Length; i += 3)
            {
                if (--faceBudget < 0) throw new InvalidDataException("Preview triangle budget exceeded.");
                var a = points[mesh.Indices[i]]; var b = points[mesh.Indices[i + 1]]; var c = points[mesh.Indices[i + 2]];
                var area = Edge(a, b, c.X, c.Y);
                if (MathF.Abs(area) < 0.001f) continue;
                var normal = Vector3.Cross(transformedVertex(mesh.Indices[i + 1]) - transformedVertex(mesh.Indices[i]), transformedVertex(mesh.Indices[i + 2]) - transformedVertex(mesh.Indices[i]));
                var light = normal.LengthSquared() < 1e-12f ? 0.5f : 0.3f + 0.7f * MathF.Abs(Vector3.Dot(Vector3.Normalize(normal), Vector3.Normalize(new Vector3(-0.4f, -0.7f, 0.6f))));
                var baseColor = new Vector3(108 + mesh.Block * 29 % 90, 154 + mesh.Block * 17 % 60, 190 + mesh.Block * 7 % 50) * light;
                var x0 = Math.Clamp((int)MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), 0, width - 1);
                var x1 = Math.Clamp((int)MathF.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))), 0, width - 1);
                var y0 = Math.Clamp((int)MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), 0, height - 1);
                var y1 = Math.Clamp((int)MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), 0, height - 1);
                pixelBudget -= (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                if (pixelBudget < 0) throw new InvalidDataException("Preview raster budget exceeded; request a smaller image or use NifSkope.");
                for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++)
                {
                    var u = Edge(b, c, x + 0.5f, y + 0.5f) / area;
                    var v = Edge(c, a, x + 0.5f, y + 0.5f) / area;
                    var w = 1 - u - v;
                    if (u < 0 || v < 0 || w < 0) continue;
                    var z = u * a.Z + v * b.Z + w * c.Z;
                    var index = y * width + x;
                    if (z >= zbuffer[index]) continue;
                    zbuffer[index] = z;
                    pixels[index * 3] = (byte)baseColor.X; pixels[index * 3 + 1] = (byte)baseColor.Y; pixels[index * 3 + 2] = (byte)baseColor.Z;
                }
                Vector3 transformedVertex(int index) => Vector3.Transform(mesh.Vertices[index], view);
            }
        }
        return Png(width, height, pixels);
    }

    private static byte[] Png(int width, int height, byte[] rgb)
    {
        using var output = new MemoryStream(); output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 2;
        Chunk(output, "IHDR", header);
        using var data = new MemoryStream();
        using (var zip = new ZLibStream(data, CompressionLevel.Fastest, true))
            for (var y = 0; y < height; y++) { zip.WriteByte(0); zip.Write(rgb, y * width * 3, width * 3); }
        Chunk(output, "IDAT", data.ToArray()); Chunk(output, "IEND", []);
        return output.ToArray();
    }
    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); output.Write(number);
        var tag = Encoding.ASCII.GetBytes(type); output.Write(tag); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (var b in tag.Concat(data)) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); output.Write(number);
    }
}
