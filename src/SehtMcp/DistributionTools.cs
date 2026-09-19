using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim;

namespace SehtMcp;

[McpServerToolType]
public sealed class DistributionTools(PluginWorkspace workspace, AssetService assets, SehtConfig config)
{
    [McpServerTool(Name = "record_assets", ReadOnly = true), Description("List explicit asset links attached to a record, with loose-file resolution. Does not infer all runtime assets or assume absent loose files are missing; they may be packed in BSAs.")]
    public CallToolResult RecordAssets(string session, string formKey)
    {
        lock (workspace.Gate) return ToolResult.Run(() =>
        {
            var record = PluginWorkspace.Find(workspace.Get(session), formKey, true);
            return record.EnumerateAssetLinks(AssetLinkQuery.Listed).Where(a => !a.IsNull).Select(a => a.DataRelativePath.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => new { path = p, loose = assets.Resolve(p) }).ToArray();
        });
    }

    [McpServerTool(Name = "record_history", ReadOnly = true), Description("Show every loaded version of a record in master load order, followed by the editable override. Useful for conflict review; only loaded masters are included.")]
    public CallToolResult History(string session, string formKey)
    {
        lock (workspace.Gate) return ToolResult.Run(() =>
        {
            var s = workspace.Get(session); var key = FormKey.Factory(formKey);
            return s.Dependencies.Cast<Mutagen.Bethesda.Skyrim.ISkyrimModGetter>().Append(s.Mod).SelectMany(m => m.EnumerateMajorRecords().Where(r => r.FormKey == key).Select(r => new { plugin = m.ModKey.ToString(), record = PluginWorkspace.Brief(r), editable = m.ModKey == s.Mod.ModKey })).ToArray();
        });
    }

    [McpServerTool(Name = "plugin_manifest", ReadOnly = true), Description("Summarize an editable plugin's dependencies, record counts by type, explicit asset paths, saved hash, and validation. Suitable for reviewing a build before packaging.")]
    public CallToolResult Manifest(string session)
    {
        lock (workspace.Gate) return ToolResult.Run(() => BuildManifest(workspace.Get(session)));
    }

    private object BuildManifest(PluginSession s) => new
    {
        format = "seht-manifest-1", generator = "SehtMCP 0.2.0", plugin = s.Mod.ModKey.ToString(), revision = s.Revision, dirty = s.Dirty, isMaster = s.Mod.IsMaster, isLight = s.Mod.IsSmallMaster, masters = s.Masters.Select(m => m.ToString()).ToArray(), sha256 = s.SavedHash,
        recordTypes = s.Mod.EnumerateMajorRecords().GroupBy(PluginWorkspace.TypeName).ToDictionary(g => g.Key, g => g.Count()),
        assets = s.Mod.EnumerateAssetLinks(AssetLinkQuery.Listed, null, null).Where(a => !a.IsNull).Select(a => a.DataRelativePath.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), validation = workspace.Validate(s)
    };

    [McpServerTool(Name = "plugin_package", Destructive = false), Description("Create a new mod ZIP containing the last saved plugin, a Seht manifest, and explicitly selected workspace files. files maps ZIP Data-relative entry names to workspace-relative source paths. Never auto-bundles masters or game assets. Requires a clean saved session. Existing ZIPs are not overwritten.")]
    public CallToolResult Package(string session, string output, Dictionary<string, string>? files = null)
    {
        lock (workspace.Gate) return ToolResult.Run(() =>
        {
            var s = workspace.Get(session);
            if (s.Dirty || s.SavedPath is null || !File.Exists(s.SavedPath) || s.SavedHash != PluginWorkspace.HashFile(s.SavedPath)) throw new InvalidOperationException("Save the current plugin first; package requires unchanged saved output.");
            var target = config.OutputPath(output);
            if (!Path.GetExtension(target).Equals(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be .zip.");
            if (files?.Count > 1000) throw new ArgumentException("Package limit is 1000 explicitly selected files.");
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [s.Mod.ModKey.ToString()] = s.SavedPath };
            foreach (var (entry, source) in files ?? [])
            {
                var name = AssetService.Normalize(entry);
                if (name.Equals("seht-manifest.json", StringComparison.OrdinalIgnoreCase) || !entries.TryAdd(name, config.OutputPath(source))) throw new ArgumentException("Duplicate or reserved package entry: " + name);
            }
            if (entries.Values.Any(p => !File.Exists(p))) throw new FileNotFoundException("A package source does not exist.");
            if (entries.Values.Sum(p => new FileInfo(p).Length) > 1024L * 1024 * 1024) throw new ArgumentException("Package input exceeds 1 GiB.");
            var stage = config.OutputPath(Path.Combine(".staging", Guid.NewGuid().ToString("N") + ".zip"));
            Directory.CreateDirectory(Path.GetDirectoryName(stage)!); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                using (var zip = ZipFile.Open(stage, ZipArchiveMode.Create))
                {
                    foreach (var (entry, path) in entries) zip.CreateEntryFromFile(path, entry, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(zip.CreateEntry("seht-manifest.json").Open());
                    writer.Write(JsonSerializer.Serialize(BuildManifest(s), SehtConfig.Json));
                }
                File.Move(stage, target);
                return new { path = target, sha256 = PluginWorkspace.HashFile(target), entries = entries.Keys.Append("seht-manifest.json").ToArray() };
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        });
    }

    [McpServerTool(Name = "mo2_profile_inspect", ReadOnly = true), Description("Read an MO2 profile's modlist.txt and plugins.txt without changing them. Returns enabled mod roots in low-to-high asset priority for config.dataRoots, plus active plugin order. It does not activate a virtual filesystem or infer archive priority.")]
    public CallToolResult Profile(string profileDirectory, string modsDirectory)
    {
        return ToolResult.Run(() =>
        {
            var profile = Path.GetFullPath(profileDirectory); var root = Path.GetFullPath(modsDirectory);
            var mods = File.ReadLines(Path.Combine(profile, "modlist.txt")).Where(l => l.StartsWith('+')).Select(l => l[1..].Trim()).Reverse().ToArray();
            var roots = mods.Select(m =>
            {
                if (m.IndexOfAny(['/', '\\', ':']) >= 0 || m is "." or "..") throw new InvalidDataException("Invalid MO2 mod directory name.");
                return Path.Combine(root, m);
            }).ToArray();
            var plugins = File.ReadLines(Path.Combine(profile, "plugins.txt")).Where(l => l.StartsWith('*')).Select(l => l[1..].Trim()).ToArray();
            return new { profile, dataRootsLowToHigh = roots, missingRoots = roots.Where(r => !Directory.Exists(r)).ToArray(), activePlugins = plugins, note = "Prepend your base game Data directory; append the appropriate overwrite/output directory if wanted. Copy roots into configuration and restart SehtMCP." };
        });
    }
}
