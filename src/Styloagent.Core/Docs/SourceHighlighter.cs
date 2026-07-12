namespace Styloagent.Core.Docs;

/// <summary>Kind of a source token — the view maps each to a colour.</summary>
public enum SourceTokenKind { Default, Keyword, String, Comment, Number }

/// <summary>A run of source text of a single kind.</summary>
public readonly record struct SourceSpan(string Text, SourceTokenKind Kind);

/// <summary>
/// A small, language-agnostic source tokenizer: it colours line/block comments, strings (", ', `),
/// numbers and a broad cross-language keyword set — enough for a readable, highlighted read-only view
/// without a per-language grammar. Pure and total; never throws.
/// </summary>
public static class SourceHighlighter
{
    // A broad set spanning C#/TS/JS/Python/Go/Rust/Java etc. — good-enough colouring for review.
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract","as","async","await","base","bool","break","byte","case","catch","char","class",
        "const","continue","def","default","delegate","do","double","elif","else","enum","event",
        "export","extends","false","final","finally","float","fn","for","foreach","from","func",
        "function","get","go","goto","if","impl","implements","import","in","int","interface",
        "internal","is","let","lock","long","match","mut","namespace","new","null","object","operator",
        "out","override","package","params","private","protected","public","readonly","record","ref",
        "return","sealed","self","set","short","static","string","struct","super","switch","this",
        "throw","true","try","type","typeof","using","var","virtual","void","while","with","yield",
    };

    public static IReadOnlyList<SourceSpan> Highlight(string text)
    {
        var spans = new List<SourceSpan>();
        if (string.IsNullOrEmpty(text)) return spans;

        int i = 0, n = text.Length;
        var buf = new System.Text.StringBuilder();
        var kind = SourceTokenKind.Default;

        void Flush()
        {
            if (buf.Length > 0) { spans.Add(new SourceSpan(buf.ToString(), kind)); buf.Clear(); }
        }
        void Emit(string s, SourceTokenKind k) { Flush(); spans.Add(new SourceSpan(s, k)); }

        while (i < n)
        {
            char c = text[i];
            char next = i + 1 < n ? text[i + 1] : '\0';

            // Line comment: //…  or  #…  (to end of line)
            if ((c == '/' && next == '/') || c == '#')
            {
                int start = i;
                while (i < n && text[i] != '\n') i++;
                Emit(text[start..i], SourceTokenKind.Comment);
                continue;
            }
            // Block comment: /* … */
            if (c == '/' && next == '*')
            {
                int start = i; i += 2;
                while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/')) i++;
                i = Math.Min(n, i + 2);
                Emit(text[start..i], SourceTokenKind.Comment);
                continue;
            }
            // String: " … ", ' … ', ` … ` (with backslash escapes)
            if (c is '"' or '\'' or '`')
            {
                char quote = c; int start = i; i++;
                while (i < n && text[i] != quote)
                {
                    if (text[i] == '\\' && i + 1 < n) i++;
                    i++;
                }
                i = Math.Min(n, i + 1);
                Emit(text[start..i], SourceTokenKind.String);
                continue;
            }
            // Number
            if (char.IsDigit(c))
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '_')) i++;
                Emit(text[start..i], SourceTokenKind.Number);
                continue;
            }
            // Word → keyword or default
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var word = text[start..i];
                if (Keywords.Contains(word)) Emit(word, SourceTokenKind.Keyword);
                else { kind = SourceTokenKind.Default; buf.Append(word); }
                continue;
            }
            // Any other char → default
            kind = SourceTokenKind.Default;
            buf.Append(c);
            i++;
        }
        Flush();
        return spans;
    }
}
