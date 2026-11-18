using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Chandlery.Services;

/// <summary>Settings from the "Help" section (and Anthropic:ApiKey). The key lives in server configuration only; it never reaches the browser or a vessel.</summary>
public class HelpOptions
{
    /// <summary>"Claude" (the real thing; falls back to "Local" when there is no key), "Local" (answers from the built-in help files, no AI, no key) or "Echo" (Development only: repeats the question).</summary>
    public string Provider { get; set; } = "Claude";
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "claude-sonnet-5-5";
    public int PerUserPerHour { get; set; } = 20;
    public int MaxQuestionChars { get; set; } = 500;
    public int MaxHistoryMessages { get; set; } = 6;
    public int MaxOutputTokens { get; set; } = 600;

    public bool IsEcho => string.Equals(Provider, "Echo", StringComparison.OrdinalIgnoreCase);
    public bool IsLocal => string.Equals(Provider, "Local", StringComparison.OrdinalIgnoreCase);
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);
    public bool IsConfigured => IsEcho || IsLocal || HasKey;

    public static HelpOptions From(IConfiguration configuration)
    {
        var options = new HelpOptions();
        configuration.GetSection("Help").Bind(options);
        options.ApiKey = configuration["Anthropic:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return options;
    }
}

public enum HelpOutcome { Ok, Refused, Failed }

/// <summary>What came back from the model. Token counts are for the log only.</summary>
public record HelpModelReply(HelpOutcome Outcome, string Text, long InputTokens = 0, long OutputTokens = 0);

/// <summary>The model behind the help panel, so tests can use a fake one.</summary>
public interface IHelpModel
{
    Task<HelpModelReply> AskAsync(HelpPrompt prompt, CancellationToken ct = default);
}

/// <summary>Asks Claude through the official SDK. Never throws for an API problem: the caller shows a plain "unavailable" message.</summary>
public class ClaudeHelpModel(HelpOptions options, ILogger<ClaudeHelpModel> logger) : IHelpModel
{
    private AnthropicClient? _client;

    public async Task<HelpModelReply> AskAsync(HelpPrompt prompt, CancellationToken ct = default)
    {
        try
        {
            _client ??= new AnthropicClient { ApiKey = options.ApiKey };
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = options.Model,
                MaxTokens = options.MaxOutputTokens,
                System = prompt.System,
                CacheControl = new CacheControlEphemeral(),
                OutputConfig = new OutputConfig { Effort = Effort.Low },
                Messages = prompt.Messages
                    .Select(t => t.FromUser
                        ? (MessageParam)new UserMessageParam { Content = t.Text }
                        : new AssistantMessageParam { Content = t.Text })
                    .ToList(),
            }, ct);

            var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(b => b.Text)).Trim();
            if (string.Equals(response.StopReason?.ToString(), "refusal", StringComparison.OrdinalIgnoreCase)) return new HelpModelReply(HelpOutcome.Refused, "");
            return text.Length == 0
                ? new HelpModelReply(HelpOutcome.Failed, "")
                : new HelpModelReply(HelpOutcome.Ok, text, response.Usage.InputTokens, response.Usage.OutputTokens);
        }
        catch (OperationCanceledException) { throw; }
        catch (AnthropicApiException ex)
        {
            logger.LogWarning("The help model returned an API error ({Status}).", (int?)ex.StatusCode);
            return new HelpModelReply(HelpOutcome.Failed, "");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The help model could not be reached.");
            return new HelpModelReply(HelpOutcome.Failed, "");
        }
    }
}

/// <summary>Development stand-in (Help:Provider = Echo): no network, no cost. Answers with the page it was asked on and a link, so the panel's behaviour can be checked.</summary>
public class EchoHelpModel : IHelpModel
{
    public Task<HelpModelReply> AskAsync(HelpPrompt prompt, CancellationToken ct = default)
    {
        var last = prompt.Messages[^1].Text;
        var question = last[(last.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        return Task.FromResult(new HelpModelReply(HelpOutcome.Ok, $"(Echo mode, no AI call.) You asked: **{question}**. The Dashboard is at [Dashboard](/)."));
    }
}
