using System.Text;
using System.Text.RegularExpressions;

namespace Chandlery.Services;

/// <summary>
/// The help assistant without an AI key: it finds the part of the built-in help (docs/help, every module) that best answers the question and shows it.
/// Nothing leaves the server and nothing is generated, so an answer is always text from the help files. It matches words, not meaning: a question
/// worded very differently from the help may not be found, and then the person is shown the questions the module's help does cover.
/// </summary>
public partial class LocalHelpModel : IHelpModel
{
    private sealed record Section(HelpModule Module, string Heading, string Body, string[] Tokens, HashSet<string> HeadingTokens);

    private readonly List<Section> _sections = [];
    private readonly Dictionary<string, int> _docFrequency = new();
    private readonly double _averageLength;
    private readonly HelpContent _content;

    public LocalHelpModel(HelpContent content)
    {
        _content = content;
        foreach (var module in content.Modules)
            foreach (var (heading, body) in Split(module))
            {
                var headingTokens = Tokens(heading).ToHashSet();
                var tokens = Tokens(heading + "\n" + body).ToArray();
                _sections.Add(new Section(module, heading, body, tokens, headingTokens));
                foreach (var t in tokens.Distinct()) _docFrequency[t] = _docFrequency.GetValueOrDefault(t) + 1;
            }
        _averageLength = _sections.Count == 0 ? 1 : _sections.Average(s => s.Tokens.Length);
    }

    public Task<HelpModelReply> AskAsync(HelpPrompt prompt, CancellationToken ct = default)
        => Task.FromResult(new HelpModelReply(HelpOutcome.Ok, Answer(prompt.Question, prompt.ModuleKey)));

    /// <summary>The answer to a question asked on a page of the given module: the best-matching section or two, or the questions the module's help covers.</summary>
    public string Answer(string question, string currentModule)
    {
        var current = _content.ForModule(currentModule);
        // Words the help never uses (like "difference") say nothing about which section answers, so only the others are matched.
        var query = Tokens(question).Distinct().Where(_docFrequency.ContainsKey).ToList();
        if (query.Count == 0) return NotFound(current);

        var ranked = _sections
            .Select(s => (Section: s, Score: Score(s, query, current.Key)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();
        if (ranked.Count == 0) return NotFound(current);

        var best = ranked[0];
        // A weak match (one common word) is not an answer.
        var matched = query.Count(q => best.Section.Tokens.Contains(q));
        if (matched * 3 < query.Count && best.Section.HeadingTokens.Overlaps(query) == false) return NotFound(current);

        var answer = new StringBuilder();
        Append(answer, best.Section);
        var second = ranked.Skip(1).FirstOrDefault(x => x.Score >= best.Score * 0.7 && x.Section.Heading != best.Section.Heading);
        if (second.Section is not null) Append(answer, second.Section);
        answer.AppendLine().Append("This is from the built-in help. Rephrase the question if it is not what you meant.");
        return answer.ToString().Trim();
    }

    private static void Append(StringBuilder sb, Section s)
    {
        if (sb.Length > 0) sb.AppendLine();
        sb.AppendLine($"#### {s.Heading} ({s.Module.Title})").AppendLine().AppendLine(s.Body.Trim());
    }

    private string NotFound(HelpModule module)
    {
        var sb = new StringBuilder("I could not find that in the built-in help.");
        if (module.Questions.Count > 0)
        {
            sb.AppendLine().AppendLine().AppendLine($"The help for {module.Title} covers questions like:");
            foreach (var q in module.Questions) sb.AppendLine($"- {q}");
        }
        sb.AppendLine().Append("Try fewer words (for example \"start PMS\" or \"link worklist task\"), or use the menu to find the page.");
        return sb.ToString().Trim();
    }

    // BM25 over the sections, with a boost for a heading hit, the module the person is in and a module the question names.
    private double Score(Section s, List<string> query, string currentModule)
    {
        const double k1 = 1.2, b = 0.75;
        var n = _sections.Count;
        double score = 0;
        foreach (var q in query)
        {
            var tf = s.Tokens.Count(t => t == q);
            var inHeading = s.HeadingTokens.Contains(q);
            if (tf == 0 && !inHeading) continue;
            var idf = Math.Log(1 + (n - _docFrequency.GetValueOrDefault(q) + 0.5) / (_docFrequency.GetValueOrDefault(q) + 0.5));
            score += idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * s.Tokens.Length / _averageLength));
            if (inHeading) score += idf * 1.5;
        }
        if (score == 0) return 0;
        // A section that answers more of the words in the question beats one that repeats a single word.
        var covered = query.Count(q => s.HeadingTokens.Contains(q) || s.Tokens.Contains(q));
        score *= 0.6 + 0.8 * covered / query.Count;
        if (string.Equals(s.Module.Key, currentModule, StringComparison.OrdinalIgnoreCase)) score *= 1.25;
        if (Tokens(s.Module.Title).Any(query.Contains)) score *= 1.4; // "...in the worklist": the question names this module
        return score;
    }

    // One section per "## " heading; whatever comes before the first heading is the module's overview.
    private static IEnumerable<(string Heading, string Body)> Split(HelpModule module)
    {
        var heading = "Overview";
        var body = new StringBuilder();
        foreach (var line in module.Body.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("## "))
            {
                if (body.ToString().Trim().Length > 0) yield return (heading, body.ToString().Trim());
                heading = line[3..].Trim();
                body.Clear();
            }
            else body.AppendLine(line);
        }
        if (body.ToString().Trim().Length > 0) yield return (heading, body.ToString().Trim());
    }

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "i", "we", "you", "do", "does", "did", "how", "what", "where", "when", "why", "which", "who", "is", "are", "was", "can", "could",
        "to", "of", "in", "on", "for", "with", "and", "or", "my", "our", "it", "this", "that", "me", "should", "would", "get", "go", "find", "use", "want",
        "need", "please", "about", "from", "at", "be", "by", "as", "if", "there", "your", "will", "have", "has", "so", "then", "up", "out", "into",
    };

    // Words people use for the same thing in a question and in the help text. Keys and values go through Norm, so every form of a word matches.
    private static readonly Dictionary<string, string> Synonyms = Build(new Dictionary<string, string>
    {
        ["begin"] = "start", ["first"] = "start", ["setup"] = "start", ["initial"] = "start", ["onboard"] = "start",
        ["connect"] = "link", ["attach"] = "link", ["relate"] = "link", ["tie"] = "link", ["join"] = "link", ["associate"] = "link",
        ["create"] = "add", ["make"] = "add", ["raise"] = "add", ["new"] = "add", ["register"] = "add", ["record"] = "add", ["enter"] = "add", ["log"] = "add",
        ["fault"] = "defect", ["breakdown"] = "defect", ["problem"] = "defect", ["issue"] = "defect",
        ["late"] = "overdue", ["delete"] = "remove", ["cancel"] = "remove", ["postpone"] = "defer", ["delay"] = "defer", ["extend"] = "defer",
        ["approve"] = "approval", ["sign"] = "approval", ["export"] = "report", ["download"] = "report", ["print"] = "report",
        ["rfq"] = "quote", ["quotation"] = "quote", ["po"] = "order", ["ra"] = "risk", ["hor"] = "rest", ["ptw"] = "permit",
    });

    private static Dictionary<string, string> Build(Dictionary<string, string> raw) => raw.ToDictionary(kv => Norm(kv.Key), kv => Norm(kv.Value));

    internal static IEnumerable<string> Tokens(string text)
    {
        foreach (Match m in WordPattern().Matches(text.ToLowerInvariant()))
        {
            if (Stop.Contains(m.Value)) continue;
            var word = Norm(m.Value);
            if (Synonyms.TryGetValue(word, out var canonical)) word = canonical;
            if (word.Length > 1 && !Stop.Contains(word)) yield return word;
        }
    }

    // A light stemmer: plural and -ed/-ing endings, a trailing "e", and a doubled last letter (deferred, getting).
    private static string Norm(string w)
    {
        if (w.Length > 4 && w.EndsWith("ies")) w = w[..^3] + "y";
        else if (w.Length > 5 && w.EndsWith("ing")) w = Undouble(w[..^3]);
        else if (w.Length > 4 && w.EndsWith("ed")) w = Undouble(w[..^2]);
        else if (w.Length > 4 && w.EndsWith("es") && !w.EndsWith("ses")) w = w[..^2];
        else if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us")) w = w[..^1];
        if (w.Length > 3 && w.EndsWith('e')) w = w[..^1];
        return w;
    }

    private static string Undouble(string w) => w.Length > 2 && w[^1] == w[^2] && "bdgmnprt".Contains(w[^1]) ? w[..^1] : w;

    [GeneratedRegex("[a-z0-9]+")] private static partial Regex WordPattern();
}
