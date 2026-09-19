using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SehtMcp;

public sealed class AssetService(SehtConfig config)
{
    public static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(p => p is ".." or "." or "")) throw new ArgumentException("Expected a relative Data path without traversal, e.g. meshes/weapons/iron/longsword.nif.");
        return path;
    }

    public object Resolve(string asset)
    {
        asset = Normalize(asset);
        var matches = config.DataRoots.Select(root => FindLoose(root, asset)).Where(p => p is not null).ToArray();
        return new { asset, winner = matches.LastOrDefault(), providersLowToHigh = matches, archiveSearch = "Archives are explicit: use archive_list and archive_search. Loose files win over archives." };
    }
    public byte[] Read(string path, string? archive = null)
    {
        if (archive is not null)
        {
            path = Normalize(path);
            var reader = Archive.CreateReader(GameRelease.SkyrimSE, Path.GetFullPath(archive));
            var entry = reader.Files.FirstOrDefault(f => f.Path.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException("Asset not found in archive.");
            if (entry.Size > (ulong)config.MaxAssetBytes) throw new ArgumentException("Asset exceeds configured size limit.");
            using var stream = entry.AsStream();
            return ReadBounded(stream);
        }
        var resolved = Path.IsPathRooted(path) ? Path.GetFullPath(path) : config.DataRoots.Reverse().Select(r => FindLoose(r, Normalize(path))).FirstOrDefault(p => p is not null) ?? throw new FileNotFoundException("Asset not found in dataRoots. For packed assets specify archive.");
        using var file = File.OpenRead(resolved);
        return ReadBounded(file);
    }

    private static string? FindLoose(string root, string asset)
    {
        var direct = Path.Combine(root, asset.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct)) return direct;
        // Skyrim asset names are case-insensitive, even when the authoring host's
        // filesystem is not. Resolve each component without recursively indexing Data.
        var current = root;
        foreach (var part in asset.Split('/'))
        {
            if (!Directory.Exists(current)) return null;
            var matches = Directory.EnumerateFileSystemEntries(current)
                .Where(p => Path.GetFileName(p).Equals(part, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length == 0) return null;
            if (matches.Length > 1) throw new IOException($"Ambiguous case-insensitive asset path '{asset}' under '{root}'.");
            current = matches[0];
        }
        return File.Exists(current) ? current : null;
    }
    private byte[] ReadBounded(Stream stream)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (output.Length + read > config.MaxAssetBytes) throw new ArgumentException("Asset exceeds configured size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    public object Archives() => config.DataRoots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateFiles(r, "*.bsa", SearchOption.TopDirectoryOnly)).Select(p => new { path = p, bytes = new FileInfo(p).Length }).ToArray();
    public object SearchArchive(string path, string query, int offset, int limit)
    {
        Page(offset, limit);
        var reader = Archive.CreateReader(GameRelease.SkyrimSE, Path.GetFullPath(path));
        var files = reader.Files.Where(f => f.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new { total = files.Length, offset, items = files.Skip(offset).Take(limit).Select(f => new { path = f.Path, bytes = f.Size }).ToArray() };
    }
    public object SearchLoose(string query, int offset, int limit)
    {
        Page(offset, limit);
        var files = new List<object>();
        int scanned = 0, matched = 0;
        foreach (var root in config.DataRoots.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (++scanned > 200000) return new { offset, scanned, matched, incomplete = true, items = files };
                var relative = Path.GetRelativePath(root, path);
                if (!relative.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                if (matched++ >= offset && files.Count < limit) files.Add(new { asset = relative, path, root });
            }
        }
        return new { offset, scanned, matched, incomplete = false, items = files };
    }
    public object Extract(string path, string archive, string output)
    {
        var destination = config.OutputPath(output);
        var bytes = Read(path, archive);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) stream.Write(bytes);
        return new { path = destination, bytes = bytes.Length, sha256 = PluginWorkspace.HashFile(destination) };
    }
    public static void Page(int offset, int limit) { if (offset < 0 || limit is < 1 or > 500) throw new ArgumentException("offset must be >=0; limit must be 1..500."); }
}
