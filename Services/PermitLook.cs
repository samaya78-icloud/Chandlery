using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>Words and colours for permits, so every screen says the same thing.</summary>
public static class PermitLook
{
    public static (string Label, string Tone) State(PermitState state) => state switch
    {
        PermitState.Requested => ("Waiting to be issued", "neutral"),
        PermitState.Issued => ("Issued, not started", "info"),
        PermitState.Active => ("Active", "success"),
        PermitState.Suspended => ("Suspended", "warning"),
        PermitState.Expired => ("Expired, not closed", "danger"),
        PermitState.Completed => ("Completed", "neutral"),
        PermitState.Cancelled => ("Cancelled", "neutral"),
        _ => ("Withdrawn", "neutral"),
    };

    public static (string Label, string Tone) Gas(PermitGasStatus status) => status switch
    {
        PermitGasStatus.NotRequired => ("No gas test needed", "neutral"),
        PermitGasStatus.Missing => ("No reading yet", "warning"),
        PermitGasStatus.OutOfLimits => ("Outside limits", "danger"),
        PermitGasStatus.Overdue => ("Reading overdue", "danger"),
        _ => ("In limits", "success"),
    };

    public static string Answer(PermitAnswerKind kind) => kind switch
    {
        PermitAnswerKind.Yes => "Yes",
        PermitAnswerKind.No => "No",
        PermitAnswerKind.NotApplicable => "Not applicable",
        _ => "Not answered",
    };

    public static string Event(PermitEventKind kind) => kind switch
    {
        PermitEventKind.Requested => "Requested",
        PermitEventKind.ChecklistCompleted => "Checklist completed",
        PermitEventKind.GasTested => "Gas tested",
        PermitEventKind.Issued => "Issued",
        PermitEventKind.MasterApproved => "Master approved",
        PermitEventKind.Started => "Started",
        PermitEventKind.PersonEntered => "Person entered",
        PermitEventKind.PersonLeft => "Person left",
        PermitEventKind.Suspended => "Suspended",
        PermitEventKind.Resumed => "Resumed",
        PermitEventKind.Extended => "Extended",
        PermitEventKind.HandedOver => "Handed over",
        PermitEventKind.ConflictAcknowledged => "Conflict acknowledged",
        PermitEventKind.WorkFinished => "Work finished (person in charge)",
        PermitEventKind.Completed => "Completed (issuer)",
        PermitEventKind.Cancelled => "Cancelled",
        PermitEventKind.Expired => "Expired",
        _ => "Withdrawn",
    };

    public static string Role(PermitRole role) => role switch { PermitRole.AnyCrew => "Any crew member", PermitRole.Officer => "An officer", _ => "The Master" };

    public static string When(DateTime at) => at.ToString("d MMM HH:mm");

    public static string Window(Permit p, DateTime until) => $"{When(p.ValidFrom)} to {When(until)}";

    public static string Close(PermitCloseStatus status) => status switch { PermitCloseStatus.Finished => "Work finished, area safe", PermitCloseStatus.LeftUnsafe => "Left in an unsafe state", _ => "Handed to the next shift" };

    // ---- the checklist as text, one item to a line: "Section|Item", with [optional] and [note] after it ----

    public static string ChecklistText(IEnumerable<PermitChecklistItem> items)
        => string.Join(Environment.NewLine, items.OrderBy(i => i.Position).Select(i => $"{i.Section}|{i.Text}{(i.Mandatory ? "" : " [optional]")}{(i.NoteRequired ? " [note]" : "")}"));

    public static List<PermitChecklistInput> ParseChecklist(string text)
    {
        var items = new List<PermitChecklistInput>();
        foreach (var raw in (text ?? "").ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            bool mandatory = true, note = false, again = true;
            while (again)
            {
                again = false;
                if (line.EndsWith("[optional]", StringComparison.OrdinalIgnoreCase)) { mandatory = false; line = line[..^10].TrimEnd(); again = true; }
                else if (line.EndsWith("[note]", StringComparison.OrdinalIgnoreCase)) { note = true; line = line[..^6].TrimEnd(); again = true; }
            }
            if (line.Length == 0) continue;
            var bar = line.IndexOf('|');
            items.Add(bar < 0 ? new PermitChecklistInput("", line, mandatory, note) : new PermitChecklistInput(line[..bar].Trim(), line[(bar + 1)..].Trim(), mandatory, note));
        }
        return items;
    }
}
