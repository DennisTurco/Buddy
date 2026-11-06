using Buddy.Core.Claude;

namespace Buddy.Core.Tests;

public class ClaudeOneShotTests
{
    [TestCase("/home/me/Journal", "//home/me/Journal")]
    [TestCase("/home/me/Journal/", "//home/me/Journal")]
    public void AbsoluteRulePath_UsesDoubleSlashForUnixPaths(string directory, string expected)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Percorsi Unix");
        }

        Assert.That(ClaudeOneShot.AbsoluteRulePath(directory), Is.EqualTo(expected));
    }

    [Test]
    public void AbsoluteRulePath_ConvertsWindowsDrive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Percorsi Windows");
        }

        Assert.That(ClaudeOneShot.AbsoluteRulePath(@"C:\Users\me\Journal"), Is.EqualTo("//c/Users/me/Journal"));
    }
}
