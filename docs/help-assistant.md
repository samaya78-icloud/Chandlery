# In-app help assistant

A Help button in the top bar opens a side panel that knows which module you are on, suggests questions for it and answers from Chandlery's own help text.

## Turning it on

The office server needs an Anthropic API key. Keep it out of the repository:

```bash
dotnet user-secrets init
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."
```

In production set `Anthropic__ApiKey` (or `ANTHROPIC_API_KEY`) in the server's environment. Without a key the button is hidden on the office server. Vessels never call out; on a vessel the panel shows the module's built-in help.

To try the panel with no key and no cost, set `Help:Provider` to `Echo` in `appsettings.Development.json` (honoured in Development only).

A Company Admin can switch the assistant off for the company on the Company Profile page.

## Settings (`Help` section)

| Key | Default | Meaning |
|---|---|---|
| `Provider` | `Claude` | `Echo` for a no-AI stand-in (Development only) |
| `Model` | `claude-sonnet-5-5` | Any Claude model id |
| `PerUserPerHour` | 20 | Questions per person per hour (in memory; a restart clears it) |
| `MaxQuestionChars` | 500 | Longest question accepted |
| `MaxHistoryMessages` | 6 | Messages of the open conversation sent back with a question |
| `MaxOutputTokens` | 600 | Longest answer |

## What is sent, and what is kept

Sent to the model: fixed rules, the help file for the current module, a list of page names and addresses, the last few messages of the open panel, and the page name you are on. Never sent: your name, email, company, records or ids. Questions are not stored or logged; the log holds only the module, the outcome and token counts.

## Maintaining the help

Help is plain markdown in `docs/help/<module>.md`, embedded in the app. Each file starts with `module`, `title` and a list of suggested questions. Pages are linked as `[Label](/path)`; only linked pages can appear as links in an answer, and a test checks that every link is a real page. When a module changes, update its help file.
