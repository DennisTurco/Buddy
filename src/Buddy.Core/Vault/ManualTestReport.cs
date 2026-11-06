using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// The manual tests done, as a Word document to attach to the task's card (Jira): only the ticked cases, by section,
/// with the task, the date and how many were done. Cases still to do and struck ones are left out.
/// The .docx is written by hand (it is a zip of a few XML parts), so no library is needed.
/// </summary>
public static partial class ManualTestReport
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>The sections with at least one case done, and those cases (bold/code marks removed).</summary>
    public static IReadOnlyList<(string Section, IReadOnlyList<(string Text, bool Indented)> Cases)> DoneCases(ManualTestDoc doc) =>
        doc.Sections
            .Select(s => (s.Title, Cases: (IReadOnlyList<(string, bool)>)s.Lines
                .Where(l => l.IsCheck && l.Checked && !l.IsStruck)
                .Select(l => (Plain(l.Text), l.Indented))
                .ToList()))
            .Where(s => s.Cases.Count > 0)
            .ToList();

    /// <summary>"Test manuali NUSR-2367.docx".</summary>
    public static string FileName(ManualTestDoc doc) =>
        string.Concat((doc.TaskId is { } id ? $"Test manuali {id}" : doc.Title).Split(Path.GetInvalidFileNameChars())) + ".docx";

    public static void Write(ManualTestDoc doc, string? taskTitle, string path, DateOnly? date = null)
    {
        var sections = DoneCases(doc);
        var done = sections.Sum(s => s.Cases.Count);
        var day = (date ?? DateOnly.FromDateTime(DateTime.Today)).ToString("d MMMM yyyy", Italian);

        var body = new StringBuilder();
        body.Append(Paragraph(doc.TaskId is { } id ? $"Test manuali {id}" : doc.Title, "Title"));
        if (!string.IsNullOrWhiteSpace(taskTitle))
        {
            body.Append(Paragraph(taskTitle!, "Subtitle"));
        }

        // The document lists what was verified: the count is of those cases, not of the whole checklist, and the
        // checklist's own intro (working notes, warnings) stays out.
        body.Append(Paragraph($"Casi verificati: {done} su {done} · {day}", null));

        if (done == 0)
        {
            body.Append(Paragraph("Nessun caso ancora spuntato nella checklist.", null, italic: true));
        }

        foreach (var (section, cases) in sections)
        {
            if (section.Length > 0)
            {
                body.Append(Paragraph(section, "Heading2"));
            }

            foreach (var (text, indented) in cases)
            {
                body.Append(Paragraph("✔ " + text, null, indent: indented ? 720 : 360));
            }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Entry(zip, "[Content_Types].xml", ContentTypes);
        Entry(zip, "_rels/.rels", PackageRels);
        Entry(zip, "word/_rels/document.xml.rels", DocumentRels);
        Entry(zip, "word/styles.xml", Styles);
        Entry(zip, "word/document.xml",
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1134" w:right="1134" w:bottom="1134" w:left="1134" w:header="708" w:footer="708" w:gutter="0"/></w:sectPr></w:body></w:document>""");
    }

    private static string Plain(string markdown) => Marks().Replace(markdown, "").Trim();

    private static string Paragraph(string text, string? style, bool italic = false, bool small = false, int indent = 0)
    {
        var properties = new StringBuilder();
        if (style is not null)
        {
            properties.Append($"""<w:pStyle w:val="{style}"/>""");
        }

        if (indent > 0)
        {
            properties.Append($"""<w:ind w:left="{indent}"/>""");
        }

        var run = new StringBuilder();
        if (italic || small)
        {
            run.Append("<w:rPr>");
            if (italic)
            {
                run.Append("<w:i/>");
            }

            if (small)
            {
                run.Append("""<w:color w:val="808080"/><w:sz w:val="18"/>""");
            }

            run.Append("</w:rPr>");
        }

        return $"""<w:p><w:pPr>{properties}</w:pPr><w:r>{run}<w:t xml:space="preserve">{SecurityElement.Escape(text)}</w:t></w:r></w:p>""";
    }

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    [GeneratedRegex(@"\*\*|__|`|~~|\[\[|\]\]")]
    private static partial Regex Marks();

    private const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
          <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
        </Types>
        """;

    private const string PackageRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
        </Relationships>
        """;

    private const string DocumentRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
        </Relationships>
        """;

    private const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:docDefaults>
            <w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:lang w:val="it-IT"/></w:rPr></w:rPrDefault>
            <w:pPrDefault><w:pPr><w:spacing w:after="80" w:line="264" w:lineRule="auto"/></w:pPr></w:pPrDefault>
          </w:docDefaults>
          <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
          <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="60"/></w:pPr><w:rPr><w:b/><w:sz w:val="40"/><w:color w:val="1F3864"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Subtitle"><w:name w:val="Subtitle"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="160"/></w:pPr><w:rPr><w:sz w:val="26"/><w:color w:val="595959"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:pPr><w:keepNext/><w:spacing w:before="240" w:after="80"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:b/><w:sz w:val="26"/><w:color w:val="2F5496"/></w:rPr></w:style>
        </w:styles>
        """;
}
