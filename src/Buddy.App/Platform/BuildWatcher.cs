using System.Diagnostics;

namespace Buddy.App.Platform;

/// <summary>
/// Development aid: when the buddy runs from its source tree (bin/&lt;config&gt;/&lt;tfm&gt; under the project), notices a
/// newer build. While the buddy runs, `dotnet build` compiles but cannot replace the files it holds in bin, so the
/// fresh assemblies are looked for where they do land: the app's obj folder and Buddy.Core's own bin folder.
/// <see cref="RestartWithBuild"/> lets the buddy exit, builds again (now the copy succeeds) and starts it.
/// </summary>
public sealed class BuildWatcher : IDisposable
{
    private readonly Timer? _timer;
    private readonly string? _project;
    private readonly DateTime _loadedApp;
    private readonly DateTime _loadedCore;
    private readonly string _config = "";
    private readonly string _framework = "";
    private bool _notified;

    public BuildWatcher()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var project = bin.Parent?.Parent?.Parent;
        if (project is null || bin.Parent?.Parent?.Name != "bin" || !File.Exists(Path.Combine(project.FullName, "Buddy.App.csproj")))
        {
            return; // an installed copy: nothing to watch
        }

        _project = project.FullName;
        _framework = bin.Name;
        _config = bin.Parent!.Name;
        _loadedApp = Stamp(Path.Combine(bin.FullName, "Buddy.App.dll"));
        _loadedCore = Stamp(Path.Combine(bin.FullName, "Buddy.Core.dll"));
        _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>A newer build is ready (raised once, on a background thread): when it was compiled.</summary>
    public event Action<DateTime>? NewerBuild;

    public bool IsAvailable => _project is not null;

    private void Check()
    {
        if (_notified || _project is null)
        {
            return;
        }

        var app = Stamp(Path.Combine(_project, "obj", _config, _framework, "Buddy.App.dll"));
        var coreBin = new DirectoryInfo(Path.Combine(_project, "..", "Buddy.Core", "bin", _config));
        var core = coreBin.Exists
            ? coreBin.EnumerateFiles("Buddy.Core.dll", SearchOption.AllDirectories).Select(f => f.LastWriteTimeUtc).DefaultIfEmpty().Max()
            : default;

        // A margin: the copy in bin is written a moment after the compiled file.
        var newest = app > _loadedApp.AddSeconds(5) ? app : core > _loadedCore.AddSeconds(5) ? core : default;
        if (newest != default)
        {
            _notified = true;
            NewerBuild?.Invoke(newest.ToLocalTime());
        }
    }

    /// <summary>
    /// Starts a small shell that waits for this process to end, builds the app (the files are free now) and starts it
    /// again; the caller then exits. False when the buddy does not run from its sources.
    /// </summary>
    public bool RestartWithBuild(string exe)
    {
        if (_project is null)
        {
            return false;
        }

        var pid = Environment.ProcessId;
        var csproj = Path.Combine(_project, "Buddy.App.csproj");
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            var script = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; "
                         + $"dotnet build '{csproj.Replace("'", "''")}' -c {_config}; "
                         + $"Start-Process -FilePath '{exe.Replace("'", "''")}' -ArgumentList '--restarted'";
            startInfo = new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-WindowStyle", "Hidden", "-Command", script } };
        }
        else
        {
            var script = $"while kill -0 {pid} 2>/dev/null; do sleep 0.5; done; dotnet build '{csproj}' -c {_config}; '{exe}' --restarted &";
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
