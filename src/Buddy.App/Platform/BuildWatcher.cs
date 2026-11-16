using System.Diagnostics;

namespace Buddy.App.Platform;

/// <summary>
/// Development aid: notices a newer build of the buddy and restarts on it (/aggiorna). Two ways to run:
/// <list type="bullet">
/// <item><b>Installed copy</b> (recommended, see <see cref="InstallFolder"/>): published from the sources, with a
/// <see cref="SourceMarker"/> file naming the project. The sources' bin folder stays free, so `dotnet build` and the
/// tests work while the buddy runs; a build newer than this copy is announced, and /aggiorna publishes it here.</item>
/// <item><b>From the source tree</b> (bin/&lt;config&gt;/&lt;tfm&gt; under the project): `dotnet build` compiles but cannot
/// replace the files the buddy holds in bin, so the fresh assemblies are looked for where they do land (the app's obj
/// folder, Buddy.Core's own bin); /aggiorna lets the buddy exit, builds again and starts it.</item>
/// </list>
/// </summary>
public sealed class BuildWatcher : IDisposable
{
    /// <summary>Written next to a published copy by the Publish target of Buddy.App.csproj: the project it comes from.</summary>
    public const string SourceMarker = "buddy-source.txt";

    /// <summary>Where the installed copy goes: %LOCALAPPDATA%\Buddy\app, or ~/.local/share/Buddy/app.</summary>
    public static string InstallFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buddy", "app");

    private readonly Timer? _timer;
    private readonly string? _project;
    private readonly bool _installed;
    private readonly DateTime _loadedApp;
    private readonly DateTime _loadedCore;
    private readonly string _config = "";
    private readonly string _framework = "";
    private bool _notified;

    public BuildWatcher()
    {
        var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _loadedApp = Stamp(Path.Combine(here, "Buddy.App.dll"));
        _loadedCore = Stamp(Path.Combine(here, "Buddy.Core.dll"));

        var marker = Path.Combine(here, SourceMarker);
        if (File.Exists(marker) && ReadMarker(marker) is { } csproj)
        {
            _project = Path.GetDirectoryName(csproj);
            _installed = true;
        }
        else
        {
            var bin = new DirectoryInfo(here);
            var project = bin.Parent?.Parent?.Parent;
            if (project is null || bin.Parent?.Parent?.Name != "bin" || !File.Exists(Path.Combine(project.FullName, "Buddy.App.csproj")))
            {
                return; // a copy that is not tied to sources (e.g. a release): nothing to watch
            }

            _project = project.FullName;
            _framework = bin.Name;
            _config = bin.Parent!.Name;
        }

        _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>A newer build is ready (raised once, on a background thread): when it was compiled.</summary>
    public event Action<DateTime>? NewerBuild;

    public bool IsAvailable => _project is not null;

    /// <summary>Running the installed copy (true) or straight from the source tree's bin (false).</summary>
    public bool IsInstalled => _installed;

    /// <summary>The project path in a marker file, when that project still exists.</summary>
    internal static string? ReadMarker(string marker)
    {
        try
        {
            var csproj = File.ReadLines(marker).FirstOrDefault()?.Trim();
            return csproj is { Length: > 0 } && File.Exists(csproj) ? csproj : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void Check()
    {
        if (_notified || _project is null)
        {
            return;
        }

        var newest = _installed ? NewestBuildOutput(_project) : NewestHeldBuild();
        if (newest != default)
        {
            _notified = true;
            NewerBuild?.Invoke(newest.ToLocalTime());
        }
    }

    /// <summary>Installed copy: the newest Buddy.App or Buddy.Core built in the sources (any configuration), if newer.</summary>
    private DateTime NewestBuildOutput(string project)
    {
        var app = Newest(Path.Combine(project, "bin"), "Buddy.App.dll");
        var core = Newest(Path.Combine(project, "..", "Buddy.Core", "bin"), "Buddy.Core.dll");

        // A margin: a publish copies the files a moment after compiling them.
        return app > _loadedApp.AddSeconds(5) ? app : core > _loadedCore.AddSeconds(5) ? core : default;
    }

    /// <summary>From the source tree: the builds that could not be copied over the files this process holds.</summary>
    private DateTime NewestHeldBuild()
    {
        var app = Stamp(Path.Combine(_project!, "obj", _config, _framework, "Buddy.App.dll"));
        var coreBin = new DirectoryInfo(Path.Combine(_project!, "..", "Buddy.Core", "bin", _config));
        var core = coreBin.Exists
            ? coreBin.EnumerateFiles("Buddy.Core.dll", SearchOption.AllDirectories).Select(f => f.LastWriteTimeUtc).DefaultIfEmpty().Max()
            : default;
        return app > _loadedApp.AddSeconds(5) ? app : core > _loadedCore.AddSeconds(5) ? core : default;
    }

    private static DateTime Newest(string folder, string file)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, file, SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max()
                : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>
    /// Starts a small shell that waits for this process to end, then publishes the sources over the installed copy (or
    /// builds them again, from the source tree) and starts the buddy; the caller then exits. False when the buddy does
    /// not come from sources.
    /// </summary>
    public bool RestartWithBuild(string exe)
    {
        if (_project is null)
        {
            return false;
        }

        var pid = Environment.ProcessId;
        var csproj = Path.Combine(_project, "Buddy.App.csproj");
        var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            static string Q(string s) => "'" + s.Replace("'", "''") + "'";
            var build = _installed
                ? $"dotnet publish {Q(csproj)} -c Release -o {Q(here)}"
                : $"dotnet build {Q(csproj)} -c {_config}";
            var script = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; {build}; "
                         + $"Start-Process -FilePath {Q(exe)} -ArgumentList '--restarted'";
            startInfo = new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-WindowStyle", "Hidden", "-Command", script } };
        }
        else
        {
            static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
            var build = _installed
                ? $"dotnet publish {Q(csproj)} -c Release -o {Q(here)}"
                : $"dotnet build {Q(csproj)} -c {_config}";
            var script = $"while kill -0 {pid} 2>/dev/null; do sleep 0.5; done; {build}; {Q(exe)} --restarted &";
            startInfo = new ProcessStartInfo("bash") { ArgumentList = { "-lc", script } };
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WorkingDirectory = _project;
        Process.Start(startInfo)?.Dispose();
        return true;
    }

    private static DateTime Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;

    public void Dispose() => _timer?.Dispose();
}
