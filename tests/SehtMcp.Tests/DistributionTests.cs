using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace SehtMcp.Tests;

public sealed class DistributionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SehtMcpTests", Guid.NewGuid().ToString("N"));
    private readonly SehtConfig config;
    private readonly PluginWorkspace workspace;
    private readonly DistributionTools tools;
    public DistributionTests()
    {
        Directory.CreateDirectory(directory); config = new() { Workspace = directory, DataRoots = [directory] };
        workspace = new(config); tools = new(workspace, new(config), config);
    }
    [Fact]
    public void PackageIncludesOnlyExplicitFilesAndManifest()
    {
        var s = workspace.Create("Pack.esp", "esp", "", "", []);
        workspace.Save(s.Id, 0, "Pack.esp", false);
        File.WriteAllText(Path.Combine(directory, "readme.txt"), "Test readme");
        Assert.False(tools.Package(s.Id, "Pack.zip", new() { ["readme.txt"] = "readme.txt" }).IsError);
        using var zip = ZipFile.OpenRead(Path.Combine(directory, "Pack.zip"));
        Assert.Equal(3, zip.Entries.Count); Assert.NotNull(zip.GetEntry("Pack.esp")); Assert.NotNull(zip.GetEntry("seht-manifest.json"));
        Assert.True(tools.Package(s.Id, "Pack.zip").IsError);
        Assert.True(tools.Package(s.Id, "Escape.zip", new() { ["../outside.txt"] = "readme.txt" }).IsError);
    }
    [Fact]
    public void ListedRecordAssetsAndManifestWork()
    {
        var s = workspace.Create("Assets.esp", "esp", "", "", []);
        workspace.Mutate(s.Id, 0, x => PluginWorkspace.CreateRecord(x, "Static", "AssetStatic", JsonNode.Parse("""{"Model":{"File":"clutter/test.nif"}}""")!.AsObject()));
        var result = tools.RecordAssets(s.Id, "000800:Assets.esp");
        Assert.False(result.IsError, JsonSerializer.Serialize(result)); Assert.Contains("test.nif", JsonSerializer.Serialize(result));
        Assert.False(tools.Manifest(s.Id).IsError);
    }
    [Fact]
    public void Mo2PriorityIsReadWithoutModifyingProfile()
    {
        File.WriteAllText(Path.Combine(directory, "modlist.txt"), "+HighPriority\n-LowDisabled\n+LowPriority\n");
        File.WriteAllText(Path.Combine(directory, "plugins.txt"), "# profile\n*Test.esp\nDisabled.esp\n");
        var result = tools.Profile(directory, directory);
        Assert.False(result.IsError);
        var json = result.StructuredContent!.Value;
        Assert.EndsWith("LowPriority", json.GetProperty("dataRootsLowToHigh")[0].GetString());
        Assert.Equal("Test.esp", json.GetProperty("activePlugins")[0].GetString());
    }
    [Theory]
    [InlineData("file.txt:secret")]
    [InlineData("sub./file.txt")]
    public void WindowsAmbiguousOutputPathsAreRejected(string path) => Assert.Throws<ArgumentException>(() => config.OutputPath(path));
    public void Dispose() { workspace.Dispose(); Directory.Delete(directory, true); }
}
