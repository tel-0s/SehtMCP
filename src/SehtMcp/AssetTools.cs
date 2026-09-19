using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SehtMcp;

[McpServerToolType]
public sealed class AssetTools(AssetService assets, ExternalTools external, SehtConfig config)
{
    [McpServerTool(Name = "asset_resolve", ReadOnly = true), Description("Find all loose-file providers for a relative Data path. dataRoots are low-to-high priority; the last provider wins. Archive lookup is explicit because archive load order differs from loose-file priority.")]
    public CallToolResult Resolve(string path) => ToolResult.Run(() => assets.Resolve(path));
    [McpServerTool(Name = "asset_search", ReadOnly = true), Description("Search loose assets across configured dataRoots by path substring. Paginates results and reports when the scan limit is reached. Skips symlink directories.")]
    public CallToolResult Search(string query, int offset = 0, int limit = 100) => ToolResult.Run(() => assets.SearchLoose(query, offset, limit));
    [McpServerTool(Name = "archive_list", ReadOnly = true), Description("List BSA files directly under configured dataRoots. Does not assume they are active in the game load order.")]
    public CallToolResult Archives() => ToolResult.Run(assets.Archives);
    [McpServerTool(Name = "archive_search", ReadOnly = true), Description("Search an explicitly selected BSA's file table by substring. No extraction is necessary. limit 1..500.")]
    public CallToolResult ArchiveSearch(string archive, string query = "", int offset = 0, int limit = 100) => ToolResult.Run(() => assets.SearchArchive(archive, query, offset, limit));
    [McpServerTool(Name = "archive_extract", Destructive = false), Description("Extract a single BSA entry to a new workspace-relative output file. Refuses overwrite and path traversal. Asset size is bounded.")]
    public CallToolResult Extract(string archive, string path, string output) => ToolResult.Run(() => assets.Extract(path, archive, output));
    [McpServerTool(Name = "nif_inspect", ReadOnly = true), Description("Inspect a Skyrim LE/SE NIF header, block types, textures, and supported geometry. path may be absolute, Data-relative, or a BSA entry with archive specified. Read-only; rejects malformed block tables.")]
    public CallToolResult Inspect(string path, string? archive = null) => ToolResult.Run(() => new NifDocument(assets.Read(path, archive)).Summary());
    [McpServerTool(Name = "nif_blocks", ReadOnly = true), Description("Page through NIF blocks with type, index, byte offset, and size. Does not modify the file.")]
    public CallToolResult Blocks(string path, string? archive = null, int offset = 0, int limit = 100) => ToolResult.Run(() => { AssetService.Page(offset, limit); var nif = new NifDocument(assets.Read(path, archive)); return new { total = nif.Blocks.Count, blocks = nif.Blocks.Skip(offset).Take(limit).ToArray() }; });
    [McpServerTool(Name = "nif_block_bytes", ReadOnly = true), Description("Read a bounded hex slice of a NIF block for diagnostics. count 1..4096. Blocks are addressed by nif_blocks index.")]
    public CallToolResult BlockBytes(string path, int block, string? archive = null, int offset = 0, int count = 256) => ToolResult.Run(() => new { block, offset, hex = new NifDocument(assets.Read(path, archive)).BlockHex(block, offset, count) });
    [McpServerTool(Name = "nif_preview", ReadOnly = true), Description("Return a PNG image of embedded Skyrim SE BSTriShape geometry directly to the model. Untextured diagnostic shading, orthographic view, angles in degrees. Applies supported parent transforms. Does not animate, deform skins, or reproduce Bethesda materials. Unsupported geometry is reported; NifSkope provides full viewing.")]
    public CallToolResult Preview(string path, string? archive = null, int width = 640, int height = 640, float yaw = 35, float pitch = 20)
    {
        try
        {
            var nif = new NifDocument(assets.Read(path, archive)); var png = MeshPreview.Render(nif, width, height, yaw, pitch);
            var result = ToolResult.Ok(new { path, width, height, renderer = "untextured geometry diagnostic", warnings = nif.Warnings });
            result.Content.Add(ImageContentBlock.FromBytes(png, "image/png"));
            return result;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return ToolResult.Error(e); }
    }
    [McpServerTool(Name = "nif_export_obj", Destructive = false), Description("Export supported embedded SSE geometry to a new workspace-relative OBJ for external viewers. Applies supported parent transforms. No materials, UVs, bones, or animation are exported. Source NIF is unchanged.")]
    public CallToolResult ExportObj(string path, string output, string? archive = null) => ToolResult.Run(() =>
    {
        var nif = new NifDocument(assets.Read(path, archive));
        if (nif.Meshes.Count == 0) throw new ArgumentException("No supported geometry.");
        if (Path.GetExtension(output).ToLowerInvariant() != ".obj") throw new ArgumentException("Output must end in .obj.");
        var destination = config.OutputPath(output); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var writer = new StreamWriter(new FileStream(destination, FileMode.CreateNew, FileAccess.Write));
        writer.WriteLine("# SehtMCP diagnostic geometry export; no materials or skin deformation");
        var start = 1;
        foreach (var mesh in nif.Meshes)
        {
            writer.WriteLine("o block_" + mesh.Block);
            foreach (var v in mesh.Vertices) writer.WriteLine(FormattableString.Invariant($"v {v.X:R} {v.Y:R} {v.Z:R}"));
            for (var i = 0; i < mesh.Indices.Length; i += 3) writer.WriteLine($"f {mesh.Indices[i] + start} {mesh.Indices[i + 1] + start} {mesh.Indices[i + 2] + start}");
            start += mesh.Vertices.Length;
        }
        return new { path = destination, warnings = nif.Warnings };
    });
    [McpServerTool(Name = "editor_status", ReadOnly = true), Description("Inspect configured Creation Kit, NifSkope, and Papyrus executables plus running CK process status. Does not access editor memory.")]
    public CallToolResult EditorStatus() => ToolResult.Run(() => new { tools = external.Status(), creationKitProcesses = external.Processes() });
    [McpServerTool(Name = "editor_launch", Destructive = false), Description("Open the configured Windows application: creation-kit or nifskope. Optional file is an existing NIF for NifSkope. CK opens normally; this tool does not load plugins, edit UI state, or guarantee MO2 VFS access.")]
    public CallToolResult Launch(string application, string? file = null) => ToolResult.Run(() => external.Launch(application, file));
    [McpServerTool(Name = "script_write", Destructive = true), Description("Write a Papyrus source script under workspace/scripts/source. Scriptname must match name. Existing sources require overwrite=true and receive a backup.")]
    public CallToolResult ScriptWrite(string name, string source, bool overwrite = false) => ToolResult.Run(() => external.WriteScript(name, source, overwrite));
    [McpServerTool(Name = "script_compile", Destructive = true), Description("Run the configured Papyrus compiler on a workspace script. Requires papyrusFlags and papyrusImports configuration. Captures diagnostics, supports cancellation, and times out after 120 seconds. Writes compiled PEX output in workspace/scripts/compiled.")]
    public async Task<CallToolResult> Compile(string name, CancellationToken cancellationToken)
    {
        try { var result = ToolResult.Ok(await external.Compile(name, cancellationToken)); result.IsError = !result.StructuredContent!.Value.GetProperty("success").GetBoolean(); return result; }
        catch (Exception e) when (e is not OutOfMemoryException) { return ToolResult.Error(e); }
    }
}
