using System.Security.Claims;
using Chandlery.Data;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public enum HelpStatus { Ok, Empty, NotAllowed, NeedsOffice, NotConfigured, DisabledForCompany, TooLong, RateLimited, Unavailable }

/// <summary>The answer, or a short plain-language reason there is none. Text is always safe to show.</summary>
public record HelpResult(HelpStatus Status, string Text);

/// <summary>What the panel needs to open: whether the assistant works here, the module and page it is on, suggested questions, and the built-in help text (shown when there is no AI, such as on a vessel).</summary>
public record HelpPanelInfo(HelpStatus Availability, string ModuleTitle, string PageLabel, IReadOnlyList<string> Questions, string BuiltInHelp);

/// <summary>
/// The help assistant. Checks who may ask and where, applies the limits, builds the prompt from the page name alone, and asks the model.
/// It reads no user data, records, names or emails, and logs only counts and token usage, never a question.
/// </summary>
public partial class HelpService(IDbContextFactory<ChandleryDbContext> factory, HelpContent content, IHelpModel model, HelpRateLimiter limiter, HelpOptions options, ILogger<HelpService> logger)
{
    public static string MessageFor(HelpStatus status) => status switch
    {
        HelpStatus.Empty => "Type a question first.",
        HelpStatus.NotAllowed => "The help assistant is for company users.",
        HelpStatus.NeedsOffice => "The assistant needs a connection to the office. The built-in help for this module is shown instead.",
        HelpStatus.NotConfigured => "The help assistant is not set up on this server yet.",
        HelpStatus.DisabledForCompany => "Your company has switched the help assistant off.",
        HelpStatus.TooLong => "That question is too long. Please shorten it.",
        HelpStatus.RateLimited => "You have asked a lot of questions in the last hour. Please try again a little later.",
        HelpStatus.Unavailable => "The assistant could not answer just now. Please try again, or use the built-in help.",
        _ => "",
    };

    private static bool IsCompanyUser(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && (user.IsInRole("CompanyAdmin") || user.IsInRole("CompanyUser"));

    private async Task<(bool Vessel, bool Enabled)> NodeAndSwitchAsync(ClaimsPrincipal user)
    {
        await using var db = await factory.CreateDbContextAsync();
        var enabled = true;
        if (Guid.TryParse(user.FindFirst("CompanyId")?.Value, out var companyId))
        {
            db.CurrentCompanyId = companyId;
            enabled = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => (bool?)c.HelpAssistantEnabled).FirstOrDefaultAsync() ?? true;
        }
        return (db.Node.IsVessel, enabled);
    }

    public async Task<HelpPanelInfo> GetPanelAsync(ClaimsPrincipal user, string? path)
    {
        var page = HelpContext.Build(path);
        var module = content.ForModule(page.ModuleKey);
        var status = HelpStatus.Ok;
        if (!IsCompanyUser(user)) status = HelpStatus.NotAllowed;
        else
        {
            var (vessel, enabled) = await NodeAndSwitchAsync(user);
            status = !enabled ? HelpStatus.DisabledForCompany : vessel ? HelpStatus.NeedsOffice : !options.IsConfigured ? HelpStatus.NotConfigured : HelpStatus.Ok;
        }
        return new HelpPanelInfo(status, module.Title, page.PageLabel, module.Questions, module.Body);
    }

    public async Task<HelpResult> AskAsync(ClaimsPrincipal user, string? path, string? question, IReadOnlyList<HelpTurn>? history, CancellationToken ct = default)
    {
        if (!IsCompanyUser(user) || !Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)) return Fail(HelpStatus.NotAllowed);
        var (vessel, enabled) = await NodeAndSwitchAsync(user);
        if (!enabled) return Fail(HelpStatus.DisabledForCompany);
        if (vessel) return Fail(HelpStatus.NeedsOffice);
        if (!options.IsConfigured) return Fail(HelpStatus.NotConfigured);
        if (string.IsNullOrWhiteSpace(question)) return Fail(HelpStatus.Empty);
        if (question.Trim().Length > options.MaxQuestionChars) return Fail(HelpStatus.TooLong);
        if (!limiter.TryAcquire(userId)) return Fail(HelpStatus.RateLimited);

        var page = HelpContext.Build(path);
        var prompt = HelpPromptBuilder.Build(content, path, question, history, options.MaxHistoryMessages);
        try
        {
            var reply = await model.AskAsync(prompt, ct);
            logger.LogInformation("Help question in {Module}: {Outcome}, {In} tokens in, {Out} tokens out.", page.ModuleKey, reply.Outcome, reply.InputTokens, reply.OutputTokens);
            return reply.Outcome switch
            {
                HelpOutcome.Ok => new HelpResult(HelpStatus.Ok, reply.Text),
                HelpOutcome.Refused => new HelpResult(HelpStatus.Ok, "I can't help with that here. I can answer questions about how to use Chandlery."),
                _ => Fail(HelpStatus.Unavailable),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The help assistant failed in {Module}.", page.ModuleKey);
            return Fail(HelpStatus.Unavailable);
        }
    }

    private static HelpResult Fail(HelpStatus status) => new(status, MessageFor(status));
}
