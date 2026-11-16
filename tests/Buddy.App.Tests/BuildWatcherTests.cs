using Buddy.App.Platform;
using NUnit.Framework;

namespace Buddy.App.Tests;

public class BuildWatcherTests
{
    [Test]
    public void The_installed_copy_finds_its_sources_only_while_they_exist()
    {
        var folder = Directory.CreateTempSubdirectory("buddy-marker-").FullName;
        try
        {
            var csproj = Path.Combine(folder, "Buddy.App.csproj");
            File.WriteAllText(csproj, "<Project />");
            var marker = Path.Combine(folder, BuildWatcher.SourceMarker);
            File.WriteAllText(marker, csproj + Environment.NewLine);

            Assert.That(BuildWatcher.ReadMarker(marker), Is.EqualTo(csproj));

            File.Delete(csproj);
            Assert.That(BuildWatcher.ReadMarker(marker), Is.Null, "sources moved or deleted: nothing to watch");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void The_install_folder_is_outside_the_sources()
    {
        Assert.That(BuildWatcher.InstallFolder, Does.EndWith(Path.Combine("Buddy", "app")));
        Assert.That(BuildWatcher.InstallFolder, Does.Not.Contain(Path.Combine("src", "Buddy.App")));
    }
}
