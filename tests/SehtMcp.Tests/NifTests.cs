using System.Text;
using Xunit;

namespace SehtMcp.Tests;

public sealed class NifTests
{
    public static byte[] Triangle()
    {
        using var block = new MemoryStream(); using (var w = new BinaryWriter(block, Encoding.UTF8, true))
        {
            w.Write(0); w.Write(0u); w.Write(-1); w.Write(14u); // name, extras, controller, flags
            foreach (var f in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1 }) w.Write(f);
            w.Write(-1); // collision
            foreach (var f in new float[] { 0, 0, 0, 2 }) w.Write(f); // bound
            w.Write(-1); w.Write(-1); w.Write(-1); // skin, shader, alpha
            w.Write((1UL << 44) | 4UL); w.Write((ushort)1); w.Write((ushort)3); w.Write(54u);
            foreach (var f in new float[] { -1, 0, -1, 0, 1, 0, -1, 0, 0, 0, 1, 0 }) w.Write(f);
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2); w.Write(0u);
        }
        using var output = new MemoryStream(); using (var w = new BinaryWriter(output, Encoding.UTF8, true))
        {
            w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
            w.Write(0x14020007u); w.Write((byte)1); w.Write(12u); w.Write(1u); w.Write(100u);
            w.Write((byte)1); w.Write((byte)0); w.Write((byte)1); w.Write((byte)0); w.Write((byte)1); w.Write((byte)0);
            w.Write((ushort)1); WriteString(w, "BSTriShape"); w.Write((ushort)0); w.Write((uint)block.Length);
            w.Write(1u); w.Write(8u); WriteString(w, "Triangle"); w.Write(0u);
            w.Write(block.ToArray()); w.Write(1u); w.Write(0);
        }
        return output.ToArray();
    }
    private static void WriteString(BinaryWriter writer, string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
    [Fact]
    public void ParsesAndRendersEmbeddedGeometry()
    {
        var nif = new NifDocument(Triangle());
        var mesh = Assert.Single(nif.Meshes); Assert.Equal(3, mesh.Vertices.Length); Assert.Equal(3, mesh.Indices.Length);
        var image = MeshPreview.Render(nif, 128, 128, 0, 0);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, image[..8]); Assert.True(image.Length > 250);
    }
    [Fact]
    public void TruncatedAndCorruptNifsAreRejected()
    {
        var bytes = Triangle();
        Assert.ThrowsAny<Exception>(() => new NifDocument(bytes[..^6]));
        bytes[0] = 0; Assert.Throws<InvalidDataException>(() => new NifDocument(bytes));
    }
    [Fact]
    public void BlockReadsAreBounded()
    {
        var nif = new NifDocument(Triangle());
        Assert.Throws<ArgumentException>(() => nif.BlockHex(0, 0, 10000));
        Assert.Throws<ArgumentException>(() => nif.BlockHex(2, 0, 8));
        Assert.Throws<ArgumentException>(() => MeshPreview.Render(nif, 8192, 8192, 0, 0));
    }
}
