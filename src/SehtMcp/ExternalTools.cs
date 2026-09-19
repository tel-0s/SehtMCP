using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SehtMcp;

public sealed class ExternalTools(SehtConfig config)
{
    public object Status() => new
    {
        creationKit = Describe(config.CreationKit), nifSkope = Describe(config.NifSkope), papyrusCompiler = Describe(config.PapyrusCompiler),
        liveEditorBridge = false, note = "This release authors plugin files with Mutagen and generates new interior and isolated exterior NAVM/NAVI with Recast or explicit triangles. CK launch/status is available; live editor operations, Havok collision extraction, cross-cell navmesh stitching/finalization, FaceGen, and lip generation require further integration."
    };
    private static object Describe(string? path) => new { path, available = path is not null && File.Exists(path), version = path is not null && File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null };
    public object Launch(string application, string? file)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CK and NifSkope launching is Windows-only.");
        var executable = application switch { "creation-kit" => config.CreationKit, "nifskope" => config.NifSkope, _ => throw new ArgumentException("application must be creation-kit or nifskope.") };
        if (executable is null || !File.Exists(executable)) throw new FileNotFoundException("Application executable is not configured or missing.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        if (application == "creation-kit" && file is not null) throw new ArgumentException("CK file loading through command-line arguments is not supported. Launch CK and select the staged plugin in its Data dialog.");
        if (file is not null)
        {
            file = Path.GetFullPath(file);
            if (!File.Exists(file) || !Path.GetExtension(file).Equals(".nif", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Expected an existing .nif file.");
            info.ArgumentList.Add(file);
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Process failed to start.");
        return new { application, pid = process.Id, file };
    }
    public object Processes() => Process.GetProcessesByName("CreationKit").Select(p =>
    {
        using (p) return new { pid = p.Id, title = p.MainWindowTitle, responding = p.Responding };
    }).ToArray();

    public object WriteScript(string name, string source, bool overwrite)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,127}$")) throw new ArgumentException("Invalid script name.");
        if (source.Length > 1024 * 1024) throw new ArgumentException("Script exceeds 1 MiB.");
        if (!Regex.IsMatch(source, @"(?im)^\s*Scriptname\s+" + Regex.Escape(name) + @"\b")) throw new ArgumentException("Scriptname declaration must match name.");
        var path = config.OutputPath(Path.Combine("scripts", "source", name + ".psc"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && !overwrite) throw new IOException("Script exists. Set overwrite=true to replace with backup.");
        if (File.Exists(path)) File.Copy(path, path + "." + Guid.NewGuid().ToString("N") + ".bak");
        File.WriteAllText(path, source, new System.Text.UTF8Encoding(false));
        return new { path, sha256 = PluginWorkspace.HashFile(path) };
    }
    public async Task<object> Compile(string name, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,127}$")) throw new ArgumentException("Invalid script name.");
        if (config.PapyrusCompiler is null || !File.Exists(config.PapyrusCompiler)) throw new FileNotFoundException("Configure papyrusCompiler first.");
        if (config.PapyrusFlags is null || !File.Exists(config.PapyrusFlags)) throw new FileNotFoundException("Configure papyrusFlags (TESV_Papyrus_Flags.flg) first.");
        var source = config.OutputPath(Path.Combine("scripts", "source", name + ".psc"));
        if (!File.Exists(source)) throw new FileNotFoundException("Use script_write first.");
        var output = config.OutputPath(Path.Combine("scripts", "compiled")); Directory.CreateDirectory(output);
        var info = new ProcessStartInfo(config.PapyrusCompiler) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(config.PapyrusCompiler)! };
        foreach (var arg in new[] { source, "-f=" + config.PapyrusFlags, "-i=" + string.Join(';', config.PapyrusImports.Prepend(Path.GetDirectoryName(source)!)), "-o=" + output }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Compiler failed to start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken); var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var outText = await stdout; var errText = await stderr;
        return new { success = process.ExitCode == 0, exitCode = process.ExitCode, stdout = outText[..Math.Min(32000, outText.Length)], stderr = errText[..Math.Min(32000, errText.Length)], output };
    }
}
