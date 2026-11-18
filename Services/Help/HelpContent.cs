using System.Reflection;
using System.Text.RegularExpressions;

namespace Chandlery.Services;

/// <summary>One module's help: its key, title, the questions suggested in the panel, and the help text itself (markdown).</summary>
public record HelpModule(string Key, string Title, IReadOnlyList<string> Questions, string Body);

/// <summary>A page the help text points at.</summary>
public record HelpLink(string Label, string Href);

/// <summary>
/// The short, user-facing help files in docs/help (one per module, embedded in the app). The assistant answers from these and from nothing else,
/// so it speaks about this product. Loaded once.
/// </summary>
public partial class HelpContent
{
    public const string GeneralKey = "general";

    private readonly Dictionary<string, HelpModule> _modules;
    private readonly List<HelpLink> _links;
    private readonly HashSet<string> _hrefs;

    public HelpContent(IEnumerable<(string Name, string Text)> files)
    {
        _modules = files.Select(f => Parse(f.Name, f.Text)).ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        if (!_modules.ContainsKey(GeneralKey)) throw new InvalidOperationException("The help files need a 'general' module.");
        _links = _modules.Values.SelectMany(m => LinkPattern().Matches(m.Body).Select(x => new HelpLink(x.Groups[1].Value, x.Groups[2].Value)))
            .GroupBy(l => l.Href).Select(g => g.First()).OrderBy(l => l.Href, StringComparer.Ordinal).ToList();
        _hrefs = _links.Select(l => l.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads the help files embedded in this assembly.</summary>
    public static HelpContent LoadEmbedded()
    {
        var assembly = typeof(HelpContent).Assembly;
        var files = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && n.Contains(".docs.help.", StringComparison.OrdinalIgnoreCase))
            .Select(n =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                return (n, reader.ReadToEnd());
            });
        return new HelpContent(files);
    }

    public IReadOnlyCollection<HelpModule> Modules => _modules.Values;

    /// <summary>The help for a module key; the general help for a key we don't have.</summary>
    public HelpModule ForModule(string key) => _modules.TryGetValue(key, out var m) ? m : _modules[GeneralKey];

    /// <summary>Every page the help files link to, by address: the only pages an answer may link to.</summary>
    public IReadOnlyList<HelpLink> PageIndex => _links;

    /// <summary>True for an address one of the help files links to. Anything else (another site, a page we never described) is not linked in an answer.</summary>
    public bool IsKnownPath(string href)
    {
        var path = href.Split('?', '#')[0].TrimEnd('/');
        return _hrefs.Contains(path.Length == 0 ? "/" : path);
    }

    private static HelpModule Parse(string name, string text)
    {
        text = text.TrimStart('﻿').Replace("\r\n", "\n");
        if (!text.StartsWith("---\n")) throw new InvalidOperationException($"Help file {name} has no front matter.");
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException($"Help file {name} has an unclosed front matter.");

        string? key = null, title = null;
        var questions = new List<string>();
        foreach (var line in text[4..end].Split('\n'))
        {
            if (line.StartsWith("- ")) questions.Add(line[2..].Trim());
            else if (line.StartsWith("module:")) key = line["module:".Length..].Trim();
            else if (line.StartsWith("title:")) title = line["title:".Length..].Trim();
        }
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException($"Help file {name} needs a module and a title.");
        return new HelpModule(key, title, questions, text[(end + 5)..].Trim());
    }

    [GeneratedRegex(@"\[([^\]]+)\]\((/[^)\s]*)\)")]
    private static partial Regex LinkPattern();
}
