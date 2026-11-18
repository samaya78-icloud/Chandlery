using System.Security.Claims;
using Chandlery.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>Who may ask, where, and how often; what happens when the model fails; the company switch; and what the vessel does with no connection. A fake model stands in for Claude.</summary>
public class HelpServiceTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private static readonly HelpContent Content = HelpContent.LoadEmbedded();

    public Task InitializeAsync() => _s.InitializeAsync();
    public Task DisposeAsync() => _s.DisposeAsync();

    private sealed class FakeModel : IHelpModel
    {
        public int Calls;
        public HelpPrompt? Last;
        public Func<HelpModelReply> Reply = () => new HelpModelReply(HelpOutcome.Ok, "Open [Plan](/pms).", 100, 20);
        public Exception? Throw;

        public Task<HelpModelReply> AskAsync(HelpPrompt prompt, CancellationToken ct = default)
        {
            Calls++;
            Last = prompt;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Reply());
        }
    }

    private HelpService Service(bool vessel, FakeModel model, HelpOptions? options = null)
    {
        options ??= new HelpOptions { ApiKey = "test-key", PerUserPerHour = 3 };
        return new HelpService(vessel ? _s.VesselFactory : _s.ShoreFactory, Content, model, new HelpRateLimiter(options), options, NullLogger<HelpService>.Instance);
    }

    private ClaimsPrincipal Person(string role = "CompanyUser", Guid? id = null) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.NameIdentifier, (id ?? Guid.NewGuid()).ToString()), new Claim("CompanyId", _s.CompanyId.ToString())], "test"));

    [Fact]
    public async Task AnOfficeUser_GetsTheAnswer_AndTheModelSeesOnlyThePageName()
    {
        var model = new FakeModel();
        var result = await Service(false, model).AskAsync(Person(), "/pms/certificates", "How do I renew one?", null);

        Assert.Equal(HelpStatus.Ok, result.Status);
        Assert.Equal("Open [Plan](/pms).", result.Text);
        Assert.Equal(1, model.Calls);
        Assert.Contains("Certificates", model.Last!.Messages[^1].Text);
        Assert.DoesNotContain(_s.CompanyId.ToString(), model.Last.System + model.Last.Messages[^1].Text);
    }

    [Fact]
    public async Task OnAVessel_NoCallIsMade_AndTheBuiltInHelpIsOffered()
    {
        var model = new FakeModel();
        var service = Service(true, model);

        var result = await service.AskAsync(Person(), "/pms", "How do I record hours?", null);
        var panel = await service.GetPanelAsync(Person(), "/pms/hours");

        Assert.Equal(HelpStatus.NeedsOffice, result.Status);
        Assert.Contains("connection to the office", result.Text);
        Assert.Equal(0, model.Calls);
        Assert.Equal(HelpStatus.NeedsOffice, panel.Availability);
        Assert.Equal("Maintenance (PMS) and certificates", panel.ModuleTitle);
        Assert.Contains("Running hours", panel.BuiltInHelp);
    }

    [Theory]
    [InlineData("YardUser")]
    [InlineData("SupplierAdmin")]
    [InlineData("SupplierUser")]
    public async Task YardsAndSuppliers_AreRefused_AndGetNoPanel(string role)
    {
        var model = new FakeModel();
        var service = Service(false, model);

        Assert.Equal(HelpStatus.NotAllowed, (await service.AskAsync(Person(role), "/", "hello", null)).Status);
        Assert.Equal(HelpStatus.NotAllowed, (await service.GetPanelAsync(Person(role), "/")).Availability);
        Assert.Equal(HelpStatus.NotAllowed, (await service.AskAsync(new ClaimsPrincipal(new ClaimsIdentity()), "/", "hello", null)).Status);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task WithNoKey_TheOfficeIsToldItIsNotSetUp_AndTheModelIsNotCalled()
    {
        var model = new FakeModel();
        var service = Service(false, model, new HelpOptions());

        Assert.Equal(HelpStatus.NotConfigured, (await service.AskAsync(Person(), "/", "hello", null)).Status);
        Assert.Equal(HelpStatus.NotConfigured, (await service.GetPanelAsync(Person(), "/")).Availability);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task TheCompanySwitch_TurnsTheAssistantOff_AndOn()
    {
        var model = new FakeModel();
        var service = Service(false, model);
        Assert.True(await service.GetEnabledAsync(_s.CompanyId));

        await service.SetEnabledAsync(_s.CompanyId, false);
        Assert.False(await service.GetEnabledAsync(_s.CompanyId));
        Assert.Equal(HelpStatus.DisabledForCompany, (await service.AskAsync(Person(), "/", "hello", null)).Status);
        Assert.Equal(HelpStatus.DisabledForCompany, (await service.GetPanelAsync(Person(), "/")).Availability);
        Assert.Equal(0, model.Calls);

        await service.SetEnabledAsync(_s.CompanyId, true);
        Assert.Equal(HelpStatus.Ok, (await service.AskAsync(Person(), "/", "hello", null)).Status);
    }

    [Fact]
    public async Task AVessel_CannotSwitchTheAssistant()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(true, new FakeModel()).SetEnabledAsync(_s.CompanyId, false));
    }

    [Fact]
    public async Task AnEmptyOrOverlongQuestion_IsRefused_WithoutCallingTheModel_OrCountingAgainstTheHour()
    {
        var model = new FakeModel();
        var options = new HelpOptions { ApiKey = "k", PerUserPerHour = 1, MaxQuestionChars = 50 };
        var service = Service(false, model, options);
        var me = Person();

        Assert.Equal(HelpStatus.Empty, (await service.AskAsync(me, "/", "   ", null)).Status);
        Assert.Equal(HelpStatus.TooLong, (await service.AskAsync(me, "/", new string('x', 51), null)).Status);
        Assert.Equal(0, model.Calls);
        Assert.Equal(HelpStatus.Ok, (await service.AskAsync(me, "/", new string('x', 50), null)).Status);
    }

    [Fact]
    public async Task TheLimit_IsPerPerson()
    {
        var model = new FakeModel();
        var service = Service(false, model); // 3 an hour
        var ann = Person(id: Guid.NewGuid());
        var bob = Person(id: Guid.NewGuid());

        for (var i = 0; i < 3; i++) Assert.Equal(HelpStatus.Ok, (await service.AskAsync(ann, "/", "q", null)).Status);
        var blocked = await service.AskAsync(ann, "/", "q", null);

        Assert.Equal(HelpStatus.RateLimited, blocked.Status);
        Assert.Equal(3, model.Calls);
        Assert.Equal(HelpStatus.Ok, (await service.AskAsync(bob, "/", "q", null)).Status);
    }

    [Fact]
    public async Task AModelFailure_OrException_IsAPlainMessage_NeverTheDetail()
    {
        var model = new FakeModel { Throw = new InvalidOperationException("secret stack detail sk-ant-123") };
        var service = Service(false, model);

        var threw = await service.AskAsync(Person(), "/", "hello", null);
        model.Throw = null;
        model.Reply = () => new HelpModelReply(HelpOutcome.Failed, "");
        var failed = await service.AskAsync(Person(), "/", "hello", null);

        Assert.Equal(HelpStatus.Unavailable, threw.Status);
        Assert.Equal(HelpStatus.Unavailable, failed.Status);
        Assert.DoesNotContain("secret", threw.Text);
        Assert.DoesNotContain("sk-ant", threw.Text);
    }

    [Fact]
    public async Task ARefusal_IsShownAsAFriendlyAnswer()
    {
        var model = new FakeModel { Reply = () => new HelpModelReply(HelpOutcome.Refused, "") };
        var result = await Service(false, model).AskAsync(Person(), "/", "hello", null);

        Assert.Equal(HelpStatus.Ok, result.Status);
        Assert.Contains("questions about how to use Chandlery", result.Text);
    }

    [Fact]
    public async Task TheConversationSoFar_IsPassedOn_UpToTheCap()
    {
        var model = new FakeModel();
        var options = new HelpOptions { ApiKey = "k", PerUserPerHour = 50, MaxHistoryMessages = 2 };
        var history = new List<HelpTurn> { new(true, "one"), new(false, "uno"), new(true, "two"), new(false, "dos") };

        await Service(false, model, options).AskAsync(Person(), "/worklist", "three", history);

        Assert.Equal(["two", "dos"], model.Last!.Messages.Take(2).Select(m => m.Text));
        Assert.EndsWith("three", model.Last.Messages[^1].Text);
    }
}
