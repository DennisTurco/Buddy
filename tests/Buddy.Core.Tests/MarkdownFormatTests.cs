using Buddy.Core.Text;

namespace Buddy.Core.Tests;

public class MarkdownFormatTests
{
    [Test]
    public void Bold_wraps_the_selection_and_keeps_it_selected()
    {
        var r = MarkdownFormat.Apply("un testo qui", 3, 8, "bold");
        Assert.That(r.Text, Is.EqualTo("un **testo** qui"));
        Assert.That(r.Text[r.SelectionStart..r.SelectionEnd], Is.EqualTo("testo"));
    }

    [Test]
    public void Bold_again_removes_it()
    {
        var r = MarkdownFormat.Apply("un **testo** qui", 5, 10, "bold");
        Assert.That(r.Text, Is.EqualTo("un testo qui"));
        Assert.That(r.Text[r.SelectionStart..r.SelectionEnd], Is.EqualTo("testo"));
    }

    [Test]
    public void Without_a_selection_a_placeholder_is_inserted_and_selected()
    {
        var r = MarkdownFormat.Apply("ciao ", 5, 5, "italic");
        Assert.That(r.Text, Is.EqualTo("ciao *testo in corsivo*"));
        Assert.That(r.Text[r.SelectionStart..r.SelectionEnd], Is.EqualTo("testo in corsivo"));
    }

    [Test]
    public void Code_on_several_lines_becomes_a_block()
    {
        Assert.That(MarkdownFormat.Apply("a\nb", 0, 3, "code").Text, Is.EqualTo("```\na\nb\n```"));
    }

    [Test]
    public void A_list_marks_every_selected_line_and_a_second_click_removes_it()
    {
        var r = MarkdownFormat.Apply("titolo\nuno\ndue\nfine", 9, 12, "list");
        Assert.That(r.Text, Is.EqualTo("titolo\n- uno\n- due\nfine"));
        Assert.That(MarkdownFormat.Apply(r.Text, r.SelectionStart, r.SelectionEnd, "list").Text, Is.EqualTo("titolo\nuno\ndue\nfine"));
    }

    [Test]
    public void Numbered_lists_count_and_replace_another_mark()
    {
        Assert.That(MarkdownFormat.Apply("- uno\n- due", 0, 11, "numbered").Text, Is.EqualTo("1. uno\n2. due"));
        Assert.That(MarkdownFormat.Apply("- uno", 2, 2, "h1").Text, Is.EqualTo("# uno"));
    }

    [Test]
    public void A_link_selects_the_address_to_fill_in()
    {
        var r = MarkdownFormat.Apply("vedi docs", 5, 9, "link");
        Assert.That(r.Text, Is.EqualTo("vedi [docs](https://)"));
        Assert.That(r.Text[r.SelectionStart..r.SelectionEnd], Is.EqualTo("https://"));
    }

    [Test]
    public void A_table_goes_on_its_own_lines_after_the_current_one()
    {
        var r = MarkdownFormat.Apply("prima\ndopo", 2, 2, "table");
        Assert.That(r.Text, Does.StartWith("prima\n\n| Colonna | Colonna |\n|---|---|\n| | |\n\ndopo"));
    }
}
