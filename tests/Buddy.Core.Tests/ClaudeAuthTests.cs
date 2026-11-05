using Buddy.Core.Claude;

namespace Buddy.Core.Tests;

public class ClaudeAuthTests
{
    [TestCase("""{ "loggedIn": true, "authMethod": "claude.ai" }""", ClaudeLoginState.LoggedIn)]
    [TestCase("""{ "loggedIn": false, "authMethod": "none" }""", ClaudeLoginState.LoggedOut)]
    [TestCase("warning: something\n{ \"loggedIn\": false }", ClaudeLoginState.LoggedOut)]
    [TestCase("error: unknown command 'auth'", ClaudeLoginState.Unknown)]
    [TestCase("{ \"other\": 1 }", ClaudeLoginState.Unknown)]
    public void Parse_reads_the_logged_in_flag(string output, ClaudeLoginState expected) =>
        Assert.That(ClaudeAuth.Parse(output), Is.EqualTo(expected));

    [TestCase("Failed to authenticate: OAuth session expired and could not be refreshed", true)]
    [TestCase("Invalid API key · Please run /login", true)]
    [TestCase("Ho aggiornato la pagina di /login del portale", false)]
    [TestCase(null, false)]
    public void IsAuthError_recognises_cli_auth_failures(string? text, bool expected) =>
        Assert.That(ClaudeAuth.IsAuthError(text), Is.EqualTo(expected));

    [Test]
    public void Inherited_host_variables_are_removed()
    {
        var env = new Dictionary<string, string?>
        {
            ["CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH"] = "1",
            ["CLAUDECODE"] = "1",
            ["CLAUDE_AGENT_SDK_VERSION"] = "x",
            ["PATH"] = "p",
            ["ANTHROPIC_BASE_URL"] = "u",
        };

        ClaudeSession.RemoveInheritedHostVariables(env);

        Assert.That(env.Keys, Is.EquivalentTo(new[] { "PATH", "ANTHROPIC_BASE_URL" }));
    }
}
