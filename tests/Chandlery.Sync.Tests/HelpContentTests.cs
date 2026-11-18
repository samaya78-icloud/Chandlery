using System.Text.RegularExpressions;
using Chandlery.Components.Shared;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>The help files, the page-to-module map, the prompt that leaves the server, and the small markdown renderer. No database.</summary>
public class HelpContentTests
{
    private static readonly HelpContent Content = HelpContent.LoadEmbedded();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Program.cs"))) dir = dir.Parent;
        return dir?.FullName ?? throw new FileNotFoundException("Program.cs not found above the test folder.");
    }

    [Fact]
    public void EveryModuleHasAHelpFile_WithTitleAndSuggestedQuestions()
    {
        foreach (var key in new[] { "general", "procurement", "worklist", "pms", "tanks", "risk", "rest", "incidents", "drills", "permits", "projects", "forms", "conversations" })
        {
            var module = Content.ForModule(key);
            Assert.Equal(key, module.Key);
            Assert.False(string.IsNullOrWhiteSpace(module.Title));
            Assert.InRange(module.Questions.Count, 3, 6);
            Assert.True(module.Body.Length is > 400 and < 6000, $"{key} help is {module.Body.Length} characters");
        }
        Assert.Equal("general", Content.ForModule("no-such-module").Key);
    }

    [Fact]
    public void EveryLinkInTheHelpFiles_IsARealPage()
    {
        var routes = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "Components", "Pages"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "@page\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value))
            .Select(r => new Regex("^" + Regex.Replace(Regex.Escape(r), @"\\\{[^}]+\}", "[^/]+") + "$", RegexOptions.IgnoreCase))
            .ToList();
        Assert.NotEmpty(routes);

        Assert.NotEmpty(Content.PageIndex);
        var missing = Content.PageIndex.Where(l => !routes.Any(r => r.IsMatch(l.Href))).Select(l => l.Href).ToList();
        Assert.True(missing.Count == 0, "Help links to pages that do not exist: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("/company/requisitions/new", "procurement", "Requisitions")]
    [InlineData("/vessel/orders", "procurement", "Orders")]
    [InlineData("/worklist/fleet", "worklist", "Fleet worklist")]
    [InlineData("/pms/certificates/fleet", "pms", "Certificates")]
    [InlineData("/pms/hours?x=1#top", "pms", "Running hours")]
    [InlineData("/company/pms-library", "pms", "Maintenance library")]
    [InlineData("/tanks/inspections/new", "tanks", "Inspections")]
    [InlineData("/risk/review", "risk", "Sign-off queue")]
    [InlineData("/company/risk-library", "risk", "Risk library")]
    [InlineData("/rest/sign", "rest", "Sign hours")]
    [InlineData("/incidents/queue", "incidents", "Investigation queue")]
    [InlineData("/drills/crew", "drills", "Crew participation")]
    [InlineData("/meetings/new", "drills", "Meetings")]
    [InlineData("/permits/fleet", "permits", "Fleet permit activity")]
    [InlineData("/projects/groups", "projects", "Project groups")]
    [InlineData("/forms/due", "forms", "Forms due")]
    [InlineData("/conversations", "conversations", "Conversations")]
    [InlineData("/company/vessels", "general", "Vessels")]
    [InlineData("/", "general", "Dashboard")]
    [InlineData("/something/we/never/built", "general", "this page")]
    [InlineData(null, "general", "Dashboard")]
    public void APath_MapsToItsModuleAndPageName(string? path, string module, string label)
    {
        var page = HelpContext.Build(path);
        Assert.Equal(module, page.ModuleKey);
        Assert.Equal(label, page.PageLabel);
    }

    [Fact]
    public void EveryPageTheBreadcrumbsKnow_GetsAModuleThatHasHelp()
    {
        foreach (var path in Breadcrumbs.KnownPaths)
        {
            var page = HelpContext.Build(path);
            Assert.Contains(Content.Modules, m => m.Key == page.ModuleKey);
        }
    }

    [Fact]
    public void ThePrompt_CarriesThePageNameAndTheModuleHelp_AndNothingFromTheRecordOrThePerson()
    {
        var id = Guid.NewGuid();
        var prompt = HelpPromptBuilder.Build(Content, $"/pms/certificates/{id}?name=Capt%20Jones&email=a@b.com", "When is it due?", null, 6);

        var everything = prompt.System + string.Join("\n", prompt.Messages.Select(m => m.Text));
        Assert.DoesNotContain(id.ToString(), everything);
        Assert.DoesNotContain("Jones", everything);
        Assert.DoesNotContain("a@b.com", everything);
        Assert.Contains("Certificates", prompt.Messages[^1].Text);
        Assert.Contains("When is it due?", prompt.Messages[^1].Text);
        Assert.Contains("Help for the Maintenance (PMS) and certificates module", prompt.System);
        Assert.Contains("/pms/certificates", prompt.System);
        Assert.DoesNotContain("Help for the Tank inspection module", prompt.System);
    }

    [Fact]
    public void TheHistory_IsTrimmedToTheLastMessages_StartsWithThePerson_AndAlternates()
    {
        var history = new List<HelpTurn>();
        for (var i = 1; i <= 10; i++)
        {
            history.Add(new HelpTurn(true, $"q{i}"));
            history.Add(new HelpTurn(false, $"a{i}"));
        }

        var prompt = HelpPromptBuilder.Build(Content, "/worklist", "next", history, 6);

        Assert.Equal(["q8", "a8", "q9", "a9", "q10", "a10"], prompt.Messages.Take(6).Select(m => m.Text));
        Assert.Equal(7, prompt.Messages.Count);
        Assert.True(prompt.Messages[0].FromUser);
        for (var i = 1; i < prompt.Messages.Count; i++) Assert.NotEqual(prompt.Messages[i - 1].FromUser, prompt.Messages[i].FromUser);

        // An odd cut that would start on an answer drops it.
        var odd = HelpPromptBuilder.Build(Content, "/worklist", "next", history, 5);
        Assert.True(odd.Messages[0].FromUser);
        Assert.Equal("q9", odd.Messages[0].Text);

        Assert.Single(HelpPromptBuilder.Build(Content, "/worklist", "next", history, 0).Messages);
    }

    [Fact]
    public void AnOverlongEarlierMessage_IsCut()
    {
        var prompt = HelpPromptBuilder.Build(Content, "/worklist", "next", [new HelpTurn(true, new string('x', 9000)), new HelpTurn(false, "ok")], 6);
        Assert.Equal(HelpPromptBuilder.MaxTurnChars, prompt.Messages[0].Text.Length);
    }

    [Fact]
    public void Markdown_EncodesHtml_KeepsKnownLinks_AndDropsOthers()
    {
        var html = HelpMarkdown.ToHtml("Open [Certificates](/pms/certificates) or [evil](https://evil.example/x) or [odd](//evil.example) or [gone](/no/such). <script>alert(1)</script> **bold**\n\n- one\n- two\n\n1. first", Content.IsKnownPath);

        Assert.Contains("<a href=\"/pms/certificates\"", html);
        Assert.DoesNotContain("evil.example", html);
        Assert.DoesNotContain("/no/such", html);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<ul><li>one</li><li>two</li></ul>", html);
        Assert.Contains("<ol><li>first</li></ol>", html);
        Assert.Contains("or evil or odd or gone.", html); // unlinked addresses keep only their label
    }

    [Fact]
    public void Markdown_LinkLabelWithMarkup_CannotBreakOutOfTheLink()
    {
        var html = HelpMarkdown.ToHtml("[<img src=x onerror=alert(1)>](/pms)", Content.IsKnownPath);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
    }

    [Fact]
    public void IsKnownPath_IgnoresQueryFragmentAndTrailingSlash()
    {
        Assert.True(Content.IsKnownPath("/pms/certificates/?a=1#x"));
        Assert.True(Content.IsKnownPath("/"));
        Assert.False(Content.IsKnownPath("/pms/certificates/abc"));
        Assert.False(Content.IsKnownPath("https://example.com/pms"));
    }

    [Fact]
    public void RateLimiter_AllowsTheLimit_ThenBlocks_PerPerson_AndFreesTheWindow()
    {
        var now = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        var limiter = new HelpRateLimiter(new HelpOptions { PerUserPerHour = 3 }, () => now);
        var ann = Guid.NewGuid();
        var bob = Guid.NewGuid();

        Assert.True(limiter.TryAcquire(ann));
        now = now.AddMinutes(20);
        Assert.True(limiter.TryAcquire(ann));
        Assert.True(limiter.TryAcquire(ann));
        Assert.Equal(0, limiter.Remaining(ann));
        Assert.False(limiter.TryAcquire(ann));
        Assert.True(limiter.TryAcquire(bob));
        Assert.Equal(2, limiter.Remaining(bob));

        // 41 minutes later Ann's first question (at 9:00) is an hour old; one place is free again.
        now = now.AddMinutes(41);
        Assert.Equal(1, limiter.Remaining(ann));
        Assert.True(limiter.TryAcquire(ann));
        Assert.False(limiter.TryAcquire(ann));
    }
}
