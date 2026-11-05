using Buddy.Core;
using Buddy.Core.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Buddy.App;

/// <summary>
/// Sample objects for the XAML previewer (VS Code / Rider): the windows' parameterless constructors use them,
/// so the preview shows a chat with a few messages instead of an empty window. Not used by the running app.
/// </summary>
internal static class DesignData
{
    private static readonly Lazy<IServiceProvider> Services = new(() =>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new BuddySettings()));
        services.AddSingleton(Platform.PlatformServices.CreateActivityMonitor());
        services.AddBuddyCore();
        return services.BuildServiceProvider();
    });

    public static IOptions<BuddySettings> Settings => Services.Value.GetRequiredService<IOptions<BuddySettings>>();

    public static Buddy.Core.Llm.OllamaClient Ollama => Services.Value.GetRequiredService<Buddy.Core.Llm.OllamaClient>();

    /// <summary>A conversation with one message of each kind.</summary>
    public static Conversation Conversation
    {
        get
        {
            var conversation = Services.Value.GetRequiredService<Conversation>();
            if (conversation.Entries.Count == 0)
            {
                conversation.Entries.Add(new ChatEntry(Speaker.Mentor, "**TCP o UDP?** TCP garantisce ordine e consegna; UDP spedisce e basta. Dove sceglieresti UDP?"));
                conversation.Entries.Add(new ChatEntry(Speaker.User, "a che punto è il task?"));
                conversation.Entries.Add(new ChatEntry(Speaker.Local, "È in **Da controllare**: mancano i test di\n- riattivazione\n- report *Account bloccati*") { CanEscalate = true });
                conversation.Entries.Add(new ChatEntry(Speaker.System, "Ora parli con Claude."));
                conversation.Entries.Add(new ChatEntry(Speaker.Claude, "Leggo la voce e il codice. Piano:\n\n1. aggiorno `LoginFallito.vb`\n2. rigenero lo script"));
            }

            return conversation;
        }
    }
}
