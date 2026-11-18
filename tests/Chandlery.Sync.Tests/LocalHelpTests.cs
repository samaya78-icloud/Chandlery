using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>The help assistant without an AI key: it answers from the built-in help files, for every module. No database.</summary>
public class LocalHelpTests
{
    private static readonly HelpContent Content = HelpContent.LoadEmbedded();
    private static readonly LocalHelpModel Model = new(Content);

    [Theory]
    // "Where do I start?" asked from the module's own page
    [InlineData("pms", "how and where do I start off with PMS", "Getting started", "Import a plan")]
    [InlineData("worklist", "how do I start with the worklist", "Getting started", "+ New task")]
    [InlineData("procurement", "how do I start purchasing", "Getting started", "New requisition")]
    [InlineData("tanks", "how do I get started with tank inspections", "Getting started", "+ Add tank")]
    [InlineData("risk", "where do I begin a risk assessment", "Getting started", "New assessment")]
    [InlineData("rest", "how do I start entering hours of rest", "Getting started", "My hours")]
    [InlineData("incidents", "how do I start reporting an incident", "Getting started", "Incident settings")]
    [InlineData("drills", "how do I set up drills", "Getting started", "Drill and meeting types")]
    [InlineData("permits", "how do I start with permit to work", "Getting started", "Permit types and checklists")]
    [InlineData("projects", "how do I start a drydock project", "Getting started", "New project")]
    [InlineData("forms", "how do I start with forms", "Getting started", "Form types")]
    [InlineData("conversations", "how do I start a conversation", "Getting started", "conversation thread")]
    [InlineData("general", "how do I set up Chandlery for my company", "Getting started", "Company Profile")]
    // Linking one module's record to another's
    [InlineData("worklist", "how to link PMS to a worklist job", "Linking a job to the Worklist", "Report a defect")]
    [InlineData("pms", "how do I order spares for a job", "Linking a job to the Worklist", "Order the spares")]
    [InlineData("tanks", "how do I connect a tank inspection to a PMS job", "Linking tanks to other records", "PMS job it came from")]
    [InlineData("drills", "how do drill findings get onto the worklist", "Linking drills and meetings", "Findings from a drill")]
    [InlineData("general", "how do I link records between modules", "Linking records across modules", "Report a defect")]
    public void AQuestionFindsTheRightSectionOfTheRightModule(string fromModule, string question, string heading, string mustContain)
    {
        var answer = Model.Answer(question, fromModule);
        Assert.Contains(heading, answer);
        Assert.Contains(mustContain, answer);
        Assert.DoesNotContain("could not find", answer);
    }

    [Fact]
    public void TheQuestionDecidesTheModule_NotOnlyThePageThePersonIsOn()
    {
        // Asked from the Dashboard (general) and from an unrelated module, the question still reaches the PMS help.
        Assert.Contains("Maintenance (PMS)", Model.Answer("how do I start with PMS", "general"));
        Assert.Contains("Maintenance (PMS)", Model.Answer("how do I start with PMS", "forms"));
    }

    [Fact]
    public void ASuggestedQuestionOfEveryModule_IsAnsweredFromThatModulesHelp()
    {
        foreach (var module in Content.Modules)
            foreach (var question in module.Questions)
            {
                var answer = Model.Answer(question, module.Key);
                Assert.False(answer.Contains("could not find"), $"{module.Key}: {question}");
            }
    }

    [Fact]
    public void NothingTheHelpCoversGivesTheModulesOwnQuestions_AndNoInventedAnswer()
    {
        var answer = Model.Answer("what is the capital of france", "pms");
        Assert.Contains("could not find", answer);
        Assert.Contains("How do I record running hours?", answer);   // the module's own suggested questions are offered instead
        Assert.Contains("could not find", Model.Answer("   ", "forms"));
    }

    [Fact]
    public void EveryLinkInAnAnswer_IsAPageTheHelpFilesDescribe()
    {
        foreach (var module in Content.Modules)
        {
            var answer = Model.Answer("how do I start", module.Key);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(answer, @"\]\((/[^)]*)\)"))
                Assert.True(Content.IsKnownPath(m.Groups[1].Value), m.Groups[1].Value);
        }
    }

    [Fact]
    public async Task ThePromptCarriesTheModuleAndTheQuestion_SoTheLocalAssistantNeedsNothingElse()
    {
        var prompt = HelpPromptBuilder.Build(Content, "/pms/hours", "how do I record running hours?", null, 6);
        Assert.Equal("pms", prompt.ModuleKey);
        Assert.Equal("how do I record running hours?", prompt.Question);
        var reply = await Model.AskAsync(prompt);
        Assert.Equal(HelpOutcome.Ok, reply.Outcome);
        Assert.Contains("Running hours", reply.Text);
    }

    [Fact]
    public void WithoutAKey_TheServerAnswersFromTheBuiltInHelp_AndAKeyKeepsTheAi()
    {
        Assert.False(new HelpOptions().IsConfigured);                         // Claude with no key: the app then switches to Local (Program.cs)
        Assert.True(new HelpOptions { Provider = "Local" }.IsConfigured);
        Assert.True(new HelpOptions { ApiKey = "k" }.IsConfigured);
    }
}
