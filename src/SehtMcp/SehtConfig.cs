using System.Text.Json;

namespace SehtMcp;

public sealed class SehtConfig
{
    public string Workspace { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SehtMCP", "workspace");
    public string? GameDirectory { get; set; }
    public string? CreationKit { get; set; }
    public string? NifSkope { get; set; }
    public string? PapyrusCompiler { get; set; }
    public string? PapyrusFlags { get; set; }
    public string[] PapyrusImports { get; set; } = [];
    // Ordered low to high priority, like MO2's left pane. Last match wins.
    public string[] DataRoots { get; set; } = [];
    public int MaxSessions { get; set; } = 8;
    public long MaxAssetBytes { get; set; } = 128 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static SehtConfig Load(string[] args)
    {
        var index = Array.IndexOf(args, "--config");
        var path = index >= 0 && index + 1 < args.Length ? args[index + 1] : Environment.GetEnvironmentVariable("SEHT_CONFIG");
        var config = path is null ? new() : JsonSerializer.Deserialize<SehtConfig>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty config.");
        config.Workspace = Path.GetFullPath(Environment.GetEnvironmentVariable("SEHT_WORKSPACE") ?? config.Workspace);
        config.GameDirectory = Environment.GetEnvironmentVariable("SEHT_GAME_DIR") ?? config.GameDirectory;
        if (config.GameDirectory is not null)
        {
            config.GameDirectory = Path.GetFullPath(config.GameDirectory);
            config.CreationKit ??= Path.Combine(config.GameDirectory, "CreationKit.exe");
            config.PapyrusCompiler ??= Path.Combine(config.GameDirectory, "Papyrus Compiler", "PapyrusCompiler.exe");
            if (config.DataRoots.Length == 0) config.DataRoots = [Path.Combine(config.GameDirectory, "Data")];
        }
        config.DataRoots = config.DataRoots.Select(Path.GetFullPath).ToArray();
        if (config.MaxSessions is < 1 or > 32 || config.MaxAssetBytes is < 1024 or > 1024L * 1024 * 1024) throw new InvalidDataException("Invalid configured limits.");
        Directory.CreateDirectory(config.Workspace);
        return config;
    }

    public string OutputPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new ArgumentException("Output must be relative to the configured workspace.");
        if (relative.Contains(':') || relative.Split(['/', '\\']).Any(p => p.Length == 0 || p.EndsWith(' ') || (p.EndsWith('.') && p is not "." and not "..") || p.Any(c => char.IsControl(c)))) throw new ArgumentException("Invalid output path component.");
        var root = Path.GetFullPath(Workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output escapes the workspace.");
        for (var current = Path.GetDirectoryName(full); current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Output path contains a symlink or junction.");
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Output is a symlink.");
        return full;
    }
}
