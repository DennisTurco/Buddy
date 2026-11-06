using System.Text.Json;
using Buddy.Core.Chat;
using Buddy.Core.Claude;

namespace Buddy.Core.Tests;

public class QuestionTests
{
    private static PermissionEntry Ask(string json)
    {
        using var input = JsonDocument.Parse(json);
        return new PermissionEntry(new PermissionRequest("r1", "AskUserQuestion", input.RootElement.Clone()));
    }

    private const string TwoQuestions = """
        {"questions":[
          {"question":"Cosa faccio?","header":"T-004","multiSelect":false,
           "options":[{"label":"Rimanda","description":"tra 30 giorni"},{"label":"Approva","description":""}]},
          {"question":"Quali domini?","header":"Domini","multiSelect":true,
           "options":[{"label":"unipr.it","description":""},{"label":"studenti.unipr.it","description":""}]}
        ]}
        """;

    [Test]
    public void Answers_go_back_in_the_answers_map_of_the_tool_input()
    {
        var entry = Ask(TwoQuestions);
        Assert.That(entry.IsQuestion, Is.True);
        Assert.That(entry.CanAnswer, Is.False);

        entry.Questions[0].Options[0].IsSelected = true;
        entry.Questions[0].Options[1].IsSelected = true; // single choice: replaces Rimanda
        entry.Questions[1].Options[0].IsSelected = true;
        entry.Questions[1].Options[1].IsSelected = true; // multiple choice: both stay
        entry.Questions[1].Other = "uniparma.it";
        Assert.That(entry.CanAnswer, Is.True);

        entry.Answer();
        var decision = entry.Decision.Result;
        Assert.That(decision.Allow, Is.True);
        var answers = decision.UpdatedInput!["answers"]!;
        Assert.That(answers["Cosa faccio?"]!.GetValue<string>(), Is.EqualTo("Approva"));
        Assert.That(answers["Quali domini?"]!.GetValue<string>(), Is.EqualTo("unipr.it, studenti.unipr.it, uniparma.it"));
        Assert.That(decision.UpdatedInput["questions"]!.AsArray(), Has.Count.EqualTo(2), "the questions stay in the input");
        Assert.That(entry.IsPending, Is.False);
    }

    [Test]
    public void A_free_answer_replaces_the_choice_of_a_single_choice_question()
    {
        var entry = Ask(TwoQuestions);
        var question = entry.Questions[0];
        question.Options[0].IsSelected = true;
        question.Other = "Rimanda al 2027";
        Assert.That(question.AnswerText, Is.EqualTo("Rimanda al 2027"));
        Assert.That(question.Options[0].IsSelected, Is.False);
    }

    [Test]
    public void Skipping_tells_Claude_the_user_chose_not_to_answer()
    {
        var entry = Ask(TwoQuestions);
        entry.Skip();
        Assert.That(entry.Decision.Result.Allow, Is.False);
        Assert.That(entry.Decision.Result.Message, Does.Contain("chose not to answer"));
    }

    [Test]
    public void Other_tools_stay_a_yes_no_permission()
    {
        using var input = JsonDocument.Parse("""{"command":"ls"}""");
        var entry = new PermissionEntry(new PermissionRequest("r1", "Bash", input.RootElement.Clone()));
        Assert.That(entry.IsQuestion, Is.False);
        Assert.That(entry.IsPermission, Is.True);
        entry.Allow();
        Assert.That(entry.Decision.Result.UpdatedInput, Is.Null);
    }
}
