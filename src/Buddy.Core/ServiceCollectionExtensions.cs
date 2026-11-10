using Buddy.Core.Chat;
using Buddy.Core.Context;
using Buddy.Core.Llm;
using Buddy.Core.Vault;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Buddy.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBuddyCore(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<VaultIndex>();
        services.AddSingleton<ContextBuilder>();
        services.AddSingleton(sp => new OllamaClient(
            new HttpClient { Timeout = TimeSpan.FromMinutes(5) },
            sp.GetRequiredService<IOptions<BuddySettings>>()));
        services.AddSingleton<Repo.GitRepo>();
        services.AddSingleton<KnowledgeIndex>();
        services.AddSingleton<AssistantCommands>();
        services.AddSingleton<LocalAssistant>();
        services.AddSingleton<Claude.ClaudeUsage>();
        services.AddSingleton<Claude.ClaudePlanUsage>();
        services.AddSingleton<Claude.ClaudeAuth>();
        services.AddSingleton<Claude.ClaudeOneShot>();
        services.AddSingleton<Mentor.MentorService>();
        services.AddSingleton<Conversation>();
        return services;
    }
}
