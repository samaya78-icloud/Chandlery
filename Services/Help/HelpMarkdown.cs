using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Chandlery.Services;

/// <summary>
/// A deliberately tiny markdown to HTML converter for help text and answers: paragraphs, lists, headings, bold, code and links.
/// Everything is HTML-encoded first, so nothing the model (or a person) writes can become markup; a link is kept only when its address is one the help files describe.
/// </summary>
public static partial class HelpMarkdown
{
    public static string ToHtml(string? markdown, Func<string, bool> isKnownPath)
    {
        var html = new StringBuilder();
        string? open = null; // "ul" or "ol" while a list is being written
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            html.Append("<p>").Append(string.Join("<br/>", paragraph)).Append("</p>");
            paragraph.Clear();
        }
        void CloseList()
        {
            if (open is null) return;
            html.Append("</").Append(open).Append('>');
            open = null;
        }

        foreach (var raw in (markdown ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { FlushParagraph(); CloseList(); continue; }

            var bullet = BulletPattern().Match(line);
            var number = NumberPattern().Match(line);
            var heading = HeadingPattern().Match(line);
            if (heading.Success)
            {
                FlushParagraph(); CloseList();
                html.Append("<h4>").Append(Inline(heading.Groups[1].Value, isKnownPath)).Append("</h4>");
            }
            else if (bullet.Success || number.Success)
            {
                FlushParagraph();
                var kind = bullet.Success ? "ul" : "ol";
                if (open != kind) { CloseList(); html.Append('<').Append(kind).Append('>'); open = kind; }
                html.Append("<li>").Append(Inline((bullet.Success ? bullet : number).Groups[1].Value, isKnownPath)).Append("</li>");
            }
            else
            {
                CloseList();
                paragraph.Add(Inline(line, isKnownPath));
            }
        }
        FlushParagraph(); CloseList();
        return html.ToString();
    }

    private static string Inline(string text, Func<string, bool> isKnownPath)
    {
        var encoded = WebUtility.HtmlEncode(text);
        encoded = LinkPattern().Replace(encoded, m =>
        {
            var href = WebUtility.HtmlDecode(m.Groups[2].Value);
            return !href.StartsWith("//") && isKnownPath(href)
                ? $"<a href=\"{WebUtility.HtmlEncode(href)}\" class=\"font-semibold text-accent-600 underline\">{m.Groups[1].Value}</a>"
                : m.Groups[1].Value;
        });
        encoded = BoldPattern().Replace(encoded, "<strong>$1</strong>");
        encoded = CodePattern().Replace(encoded, "<code>$1</code>");
        return encoded;
    }

    [GeneratedRegex(@"^\s*[-*]\s+(.*)$")] private static partial Regex BulletPattern();
    [GeneratedRegex(@"^\s*\d+[.)]\s+(.*)$")] private static partial Regex NumberPattern();
    [GeneratedRegex(@"^#{1,4}\s+(.*)$")] private static partial Regex HeadingPattern();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]*)\)")] private static partial Regex LinkPattern();
    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex BoldPattern();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex CodePattern();
}
