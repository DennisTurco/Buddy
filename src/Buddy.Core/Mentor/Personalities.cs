namespace Buddy.Core.Mentor;

/// <summary>Built-in personalities the user picks at first run; each is a tone added to the mentor's prompts.</summary>
public static class Personalities
{
    public const string Custom = "custom";

    public static IReadOnlyList<Personality> All { get; } =
    [
        new("mentore", "Mentore calmo e riflessivo",
            "Calmo, riflessivo, paziente. Fa domande che aprono il pensiero, dà spazio, valorizza i progressi."),
        new("coach", "Coach motivante",
            "Energico e incoraggiante, orientato agli obiettivi. Celebra i passi avanti e spinge ad alzare l'asticella."),
        new("amico", "Amico ironico",
            "Informale, spiritoso, con una battuta ogni tanto (mai sarcastico verso di lui). Leggero ma sincero."),
        new("diretto", "Diretto e sfidante",
            "Schietto e senza giri di parole. Mette in discussione le scelte, chiede il perché, non si accontenta."),
        new("saggio", "Vecchio saggio",
            "Parla per esempi e metafore, con calma. Collega il lavoro di oggi a lezioni più grandi."),
    ];

    public static string Describe(MentorSettings settings)
    {
        if (settings.Personality == Custom && !string.IsNullOrWhiteSpace(settings.CustomPersonality))
        {
            return settings.CustomPersonality.Trim();
        }

        return (All.FirstOrDefault(p => p.Key == settings.Personality) ?? All[0]).Tone;
    }
}

public sealed record Personality(string Key, string Label, string Tone);
