namespace Buddy.Core.Llm;

/// <summary>
/// The local model's philosophical side: now and then it explains the why of things through a thinker's idea, to make
/// the user reflect, and says plainly when it quotes someone (who they were, in a few words), so each quote also teaches
/// something. Small models misremember quotes: an idea in its own words is preferred to invented exact words.
/// </summary>
public static class Philosophy
{
    /// <summary>The line every quote starts with, so the chat (and the user) can tell it apart.</summary>
    public const string QuoteMark = "📜";

    public const string Instruction = """

        FILOSOFIA: ogni tanto (non sempre: quando la domanda tocca il perché delle cose, una scelta, un modo di lavorare o
        di stare con gli altri) aiuta a capire il perché con un'idea di un filosofo, in modo profondo ma semplice, per far
        riflettere; mai a forza e mai per domande pratiche o di stato. Quando citi o richiami un filosofo, segnalalo SEMPRE
        su una riga a parte, così:
        📜 Citazione — Nome (chi era in poche parole, epoca): «la frase» oppure "in sostanza: l'idea"
        Usa le virgolette «» solo se sei sicuro delle parole; altrimenti scrivi "in sostanza:" e l'idea con parole tue.
        Non inventare frasi né attribuirle a chi non le ha dette.
        """;
}
