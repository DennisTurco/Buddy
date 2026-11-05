using Buddy.Core.Text;

namespace Buddy.Core.Tests;

public class DeadKeysTests
{
    [TestCase('\'', "e", "é")]
    [TestCase('`', "a", "à")]
    [TestCase('`', "E", "È")]
    [TestCase('\'', "c", "ç")]
    [TestCase('"', "u", "ü")]
    [TestCase('^', "o", "ô")]
    [TestCase('~', "n", "ñ")]
    [TestCase('\'', " ", "'")]
    [TestCase('`', " ", "`")]
    [TestCase('\'', "s", "'s")]
    [TestCase('\'', "ciao", "'ciao")]
    public void Compose_follows_the_us_international_layout(char accent, string next, string expected) =>
        Assert.That(DeadKeys.Compose(accent, next), Is.EqualTo(expected));
}
