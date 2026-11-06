using System.Text.RegularExpressions;

namespace Buddy.Core.Text;

public enum TokenKind
{
    Plain,
    Keyword,
    String,
    Comment,
    Number,
    Type,

    /// <summary>XML/HTML tag names and attribute names; a property or key elsewhere (JSON, YAML).</summary>
    Tag,
}

public readonly record struct CodeToken(string Text, TokenKind Kind);

/// <summary>
/// A small, forgiving syntax highlighter for the code blocks of the chat and the notes: comments, strings, numbers,
/// keywords and type names for the usual languages (C#, JS/TS, Python, Java, Go, Rust, SQL, shell, PowerShell, JSON,
/// YAML, XML/HTML). It is not a parser: an unknown language still gets strings, numbers and comments, and the
/// tokens always add up to the original text.
/// </summary>
public static partial class CodeTokenizer
{
    private static readonly HashSet<string> CommonKeywords = new(StringComparer.Ordinal)
    {
        // C-like (C#, Java, JS/TS, Go, Rust, C/C++)
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "export",
        "extends", "extern", "false", "final", "finally", "fixed", "float", "for", "foreach", "from", "func", "function",
        "get", "go", "goto", "if", "implements", "implicit", "import", "in", "init", "int", "interface", "internal", "is",
        "let", "lock", "long", "match", "mod", "module", "namespace", "new", "null", "object", "operator", "out",
        "override", "package", "params", "private", "protected", "public", "readonly", "record", "ref", "return",
        "sealed", "set", "short", "static", "string", "struct", "super", "switch", "this", "throw", "throws", "true",
        "try", "type", "typeof", "uint", "ulong", "undefined", "using", "var", "virtual", "void", "volatile", "when",
        "where", "while", "with", "yield", "fn", "impl", "pub", "mut", "use", "crate", "self", "trait", "loop", "nil",
        "defer", "chan", "map", "range", "select", "instanceof", "of", "declare", "keyof", "readonly", "required",
        "nameof", "sizeof", "stackalloc", "unsafe", "checked", "unchecked", "global", "partial", "file", "scoped",
        "value", "and", "or", "not",
    };

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except",
        "False", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "None", "nonlocal", "not",
        "or", "pass", "raise", "return", "self", "True", "try", "while", "with", "yield", "match", "case",
    };

    private static readonly HashSet<string> ShellKeywords = new(StringComparer.Ordinal)
    {
        "if", "then", "else", "elif", "fi", "for", "in", "do", "done", "while", "until", "case", "esac", "function",
        "return", "export", "local", "readonly", "echo", "cd", "sudo", "exit", "set", "unset", "source",
    };

    private static readonly HashSet<string> PowerShellKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "begin", "break", "catch", "class", "continue", "do", "else", "elseif", "end", "enum", "exit", "filter",
        "finally", "for", "foreach", "function", "if", "in", "param", "process", "return", "switch", "throw", "trap",
        "try", "until", "using", "while", "$true", "$false", "$null",
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "and", "or", "not", "insert", "into", "values", "update", "set", "delete", "create",
        "table", "alter", "drop", "index", "view", "join", "inner", "left", "right", "outer", "full", "on", "group",
        "by", "order", "having", "as", "distinct", "union", "all", "limit", "offset", "top", "null", "is", "in",
        "exists", "between", "like", "case", "when", "then", "else", "end", "primary", "key", "foreign", "references",
        "default", "begin", "commit", "rollback", "transaction", "declare", "with", "asc", "desc", "count", "sum",
        "avg", "min", "max", "cast", "varchar", "nvarchar", "int", "bigint", "bit", "datetime", "true", "false",
    };

    /// <summary>Normalizes the info string of a fence ("C#", "ts", "sh"…) to a language family.</summary>
    public static string Family(string? language)
    {
        var lang = (language ?? "").Trim().ToLowerInvariant();
        var space = lang.IndexOf(' ');
        if (space > 0)
        {
            lang = lang[..space];
        }

        return lang switch
        {
            "c#" or "cs" or "csharp" => "csharp",
            "js" or "javascript" or "jsx" or "ts" or "typescript" or "tsx" or "mjs" => "js",
            "py" or "python" => "python",
            "sh" or "bash" or "shell" or "zsh" or "console" => "shell",
            "ps" or "ps1" or "powershell" or "pwsh" => "powershell",
            "sql" or "tsql" or "mysql" or "postgres" or "postgresql" => "sql",
            "xml" or "html" or "xaml" or "axaml" or "csproj" or "svg" or "htm" => "xml",
            "json" or "jsonc" => "json",
            "yaml" or "yml" => "yaml",
            "" => "",
            _ => lang,
        };
    }

    public static IReadOnlyList<CodeToken> Tokenize(string code, string? language)
    {
        var family = Family(language);
        var tokens = new List<CodeToken>();
        if (code.Length == 0)
        {
            return tokens;
        }

        var regex = family switch
        {
            "xml" => XmlRegex(),
            "python" or "shell" or "yaml" or "toml" or "ini" or "dockerfile" or "makefile" or "r" or "ruby" or "rb" => HashCommentRegex(),
            "powershell" => PowerShellRegex(),
            "sql" or "lua" or "haskell" => DashCommentRegex(),
            _ => CLikeRegex(),
        };

        var keywords = family switch
        {
            "python" => PythonKeywords,
            "shell" => ShellKeywords,
            "powershell" => PowerShellKeywords,
            "sql" => SqlKeywords,
            "json" or "yaml" or "xml" => null,
            _ => CommonKeywords,
        };

        var position = 0;
        foreach (Match match in regex.Matches(code))
        {
            if (match.Index > position)
            {
                tokens.Add(new CodeToken(code[position..match.Index], TokenKind.Plain));
            }

            tokens.Add(new CodeToken(match.Value, Classify(match, family, keywords)));
            position = match.Index + match.Length;
        }

        if (position < code.Length)
        {
            tokens.Add(new CodeToken(code[position..], TokenKind.Plain));
        }

        return Merge(tokens);
    }

    private static TokenKind Classify(Match match, string family, HashSet<string>? keywords)
    {
        if (match.Groups["comment"].Success)
        {
            return TokenKind.Comment;
        }

        if (match.Groups["string"].Success)
        {
            // A JSON/YAML key ("name": …) reads better as a property than as a value.
            return family == "json" && match.Groups["key"].Success ? TokenKind.Tag : TokenKind.String;
        }

        if (match.Groups["number"].Success)
        {
            return TokenKind.Number;
        }

        if (match.Groups["tag"].Success)
        {
            return TokenKind.Tag;
        }

        if (match.Groups["word"].Success)
        {
            var word = match.Value;
            if (family == "yaml" && match.Groups["key"].Success)
            {
                return TokenKind.Tag;
            }

            if (keywords?.Contains(word) == true)
            {
                return TokenKind.Keyword;
            }

            if (family is "json" or "yaml")
            {
                return word is "true" or "false" or "null" ? TokenKind.Keyword : TokenKind.Plain;
            }

            if (family == "powershell" && word.StartsWith('$'))
            {
                return TokenKind.Tag;
            }

            // PascalCase names (types, classes, cmdlets) stand out a little.
            return word.Length > 1 && char.IsUpper(word[0]) && word.Any(char.IsLower) && family is not ("sql" or "shell")
                ? TokenKind.Type
                : TokenKind.Plain;
        }

        return TokenKind.Plain;
    }

    private static List<CodeToken> Merge(List<CodeToken> tokens)
    {
        var merged = new List<CodeToken>(tokens.Count);
        foreach (var token in tokens)
        {
            if (merged.Count > 0 && merged[^1].Kind == token.Kind)
            {
                merged[^1] = new CodeToken(merged[^1].Text + token.Text, token.Kind);
            }
            else
            {
                merged.Add(token);
            }
        }

        return merged;
    }

    private const string Strings = """
        (?<string>@?\$?"(?:[^"\\\n]|\\.)*"(?<key>\s*:)?|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\.)*`)
        """;

    private const string Numbers = @"(?<number>\b(?:0[xX][0-9a-fA-F_]+|\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?)[a-zA-Z]{0,2}\b)";

    [GeneratedRegex(@"(?<comment>//[^\n]*|/\*[\s\S]*?(?:\*/|$))|" + Strings + "|" + Numbers + @"|(?<word>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex CLikeRegex();

    [GeneratedRegex(@"(?<comment>#[^\n]*)|" + Strings + "|" + Numbers + @"|(?<word>[A-Za-z_][A-Za-z0-9_\-]*)(?<key>(?=\s*:(?:\s|$)))?", RegexOptions.Multiline)]
    private static partial Regex HashCommentRegex();

    [GeneratedRegex(@"(?<comment><#[\s\S]*?(?:#>|$)|#[^\n]*)|" + Strings + "|" + Numbers + @"|(?<word>\$?[A-Za-z_][A-Za-z0-9_\-]*)")]
    private static partial Regex PowerShellRegex();

    [GeneratedRegex(@"(?<comment>--[^\n]*|/\*[\s\S]*?(?:\*/|$))|" + Strings + "|" + Numbers + @"|(?<word>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex DashCommentRegex();

    [GeneratedRegex(@"(?<comment><!--[\s\S]*?(?:-->|$))|(?<string>""[^""]*""|'[^']*')|(?<tag>(?<=</?)[A-Za-z_][\w:.\-]*|[A-Za-z_][\w:.\-]*(?==))")]
    private static partial Regex XmlRegex();
}
