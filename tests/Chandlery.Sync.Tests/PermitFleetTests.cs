using Chandlery.Models;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>The office's view of permits, worked out from what the vessels sent: counts by vessel and month, what needs attention, the CSV, and the checklist as text.</summary>
public class PermitFleetTests
{
    private static readonly Guid VesselA = Guid.NewGuid(), VesselB = Guid.NewGuid();
    private static DateTime At(int month, int day, int hour = 8) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    private static PermitRow Row(Guid vessel, string name, string type, PermitState state, DateTime from, DateTime until, bool valid = true, string reason = "Valid", params (DateTime At, bool Ok)[] gas)
    {
        var permit = new Permit
        {
            Id = Guid.NewGuid(), VesselId = vessel, Code = $"PTW-{name}-{Guid.NewGuid().ToString("N")[..4]}", TypeName = type, ValidFrom = from, ValidUntil = until, Location = "Hold 2", RequesterName = "Ana",
            Readings = gas.Select(g => new PermitGasReading { At = g.At, WithinLimits = g.Ok, Oxygen = 20.9m, FlammablePercentLel = 0 }).ToList(),
            Events = [new PermitEvent { Kind = PermitEventKind.Requested, At = from, ByName = "Ana" }],
        };
        return new PermitRow(permit, name, state, valid, reason, until, 0);
    }

    private static readonly DateTime From = At(9, 1), To = At(11, 30);

    [Fact]
    public void TheFleetIsCountedByVessel_TheWorstFirst_AndOnlyThePeriodLimitsMonthsAndExceedances()
    {
        var rows = new[]
        {
            Row(VesselA, "Alpha", "Hot work", PermitState.Active, At(11, 10), At(11, 10, 16)),
            Row(VesselA, "Alpha", "Hot work", PermitState.Completed, At(10, 3), At(10, 3, 16), gas: [(At(10, 3), true), (At(10, 3, 10), false)]),
            Row(VesselB, "Bravo", "Enclosed space entry", PermitState.Expired, At(11, 9), At(11, 9, 16), valid: false, reason: "Expired and not closed"),
            Row(VesselB, "Bravo", "Enclosed space entry", PermitState.Suspended, At(11, 11), At(11, 11, 16)),
            Row(VesselB, "Bravo", "Hot work", PermitState.Requested, At(11, 12), At(11, 12, 16)),
            Row(VesselB, "Bravo", "Hot work", PermitState.Completed, At(8, 1), At(8, 1, 16), gas: [(At(8, 1), false)]),     // before the period
        };
        var view = PermitFleet.Build(From, To, rows);

        Assert.Equal(["Bravo", "Alpha"], view.Vessels.Select(v => v.Name));                                  // the vessel with an expired permit first
        var bravo = view.Vessels[0];
        Assert.Equal((0, 1, 1, 1, 0, 3), (bravo.Active, bravo.Suspended, bravo.Awaiting, bravo.Expired, bravo.GasExceedances, bravo.InPeriod));
        var alpha = view.Vessels[1];
        Assert.Equal((1, 0, 0, 0, 1, 2), (alpha.Active, alpha.Suspended, alpha.Awaiting, alpha.Expired, alpha.GasExceedances, alpha.InPeriod));
        Assert.Equal(1, view.Expired);
        Assert.Equal([("2026-10", "Hot work", 1), ("2026-11", "Enclosed space entry", 2), ("2026-11", "Hot work", 2)], view.ByTypeMonth.Select(m => (m.Month, m.TypeName, m.Count)));
    }

    [Fact]
    public void ThePermitsToLookAtAreTheExpired_TheActiveButNotValid_AndTheSuspended_OldestFirst()
    {
        var expired = Row(VesselB, "Bravo", "Hot work", PermitState.Expired, At(11, 2), At(11, 2, 16));
        var notValid = Row(VesselA, "Alpha", "Enclosed space entry", PermitState.Active, At(11, 5), At(11, 5, 16), valid: false, reason: "Gas reading overdue");
        var suspended = Row(VesselA, "Alpha", "Hot work", PermitState.Suspended, At(11, 4), At(11, 4, 16));
        var fine = Row(VesselA, "Alpha", "Hot work", PermitState.Active, At(11, 6), At(11, 6, 16));
        var view = PermitFleet.Build(From, To, [fine, notValid, suspended, expired]);
        Assert.Equal([expired.Permit.Code, suspended.Permit.Code, notValid.Permit.Code], view.Attention.Select(a => a.Row.Permit.Code));
        Assert.Contains("not closed", view.Attention[0].Why);
        Assert.Equal("Suspended", view.Attention[1].Why);
        Assert.Contains("gas reading overdue", view.Attention[2].Why);
    }

    [Fact]
    public void ThePermitsAreCountedOnceWhenTheyAreInBothTheOpenAndThePeriodLists()
    {
        var one = Row(VesselA, "Alpha", "Hot work", PermitState.Active, At(11, 10), At(11, 10, 16));
        var view = PermitFleet.Build(From, To, [one, one]);
        Assert.Equal(1, view.Vessels.Single().Active);
        Assert.Equal(1, view.Vessels.Single().InPeriod);
    }

    [Fact]
    public void ANoPermitsFleetHasNothingToShow()
    {
        var view = PermitFleet.Build(From, To, []);
        Assert.Empty(view.Vessels);
        Assert.Empty(view.Attention);
        Assert.Equal(0, view.Expired);
    }

    [Fact]
    public void TheCsvFilesCarryTheSameFiguresAsTheScreens()
    {
        var rows = new[]
        {
            Row(VesselA, "Alpha", "Hot work", PermitState.Completed, At(10, 3), At(10, 3, 16), gas: [(At(10, 3), false)]),
            Row(VesselB, "Bravo", "Enclosed space entry", PermitState.Expired, At(11, 9), At(11, 9, 16)),
        };
        var view = PermitFleet.Build(From, To, rows);
        var fleet = PermitReports.FleetCsv(view).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("\"Vessel\",\"Active\",\"Suspended\",\"Awaiting issue\",\"Expired not closed\",\"Gas readings outside limits\",\"Permits in the period\"", fleet[0]);
        Assert.Equal("\"Bravo\",\"0\",\"0\",\"0\",\"1\",\"0\",\"1\"", fleet[1]);
        Assert.Equal("\"Alpha\",\"0\",\"0\",\"0\",\"0\",\"1\",\"1\"", fleet[2]);

        var register = PermitReports.RegisterCsv(rows).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, register.Length);
        Assert.Contains("\"Expired, not closed\"", register[2]);
        Assert.Contains("\"1\",\"1\"", register[1]);                                                          // one reading, one outside the limits

        var events = PermitReports.EventsCsv(rows).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, events.Length);
        Assert.Contains("\"Requested\",\"Ana\"", events[1]);
        Assert.Equal(["Month", "Kind", "Permits"], PermitReports.MonthsCsv(view).ReplaceLineEndings("\n").Split('\n')[0].Split(',').Select(c => c.Trim('"')));
    }

    [Fact]
    public void AChecklistRoundTripsThroughItsTextForm_WithTheFlagsAndWithoutASection()
    {
        var text = "Area|The area is inspected\nGas|Atmosphere tested [note]\nHoused keeping [optional]\nPeople|Briefed [optional] [note]\n\n   \n";
        var items = PermitLook.ParseChecklist(text);
        Assert.Equal(4, items.Count);
        Assert.Equal(("Area", "The area is inspected", true, false), (items[0].Section, items[0].Text, items[0].Mandatory, items[0].NoteRequired));
        Assert.Equal(("Gas", "Atmosphere tested", true, true), (items[1].Section, items[1].Text, items[1].Mandatory, items[1].NoteRequired));
        Assert.Equal(("", "Housed keeping", false, false), (items[2].Section, items[2].Text, items[2].Mandatory, items[2].NoteRequired));
        Assert.Equal(("People", "Briefed", false, true), (items[3].Section, items[3].Text, items[3].Mandatory, items[3].NoteRequired));

        var saved = items.Select((i, n) => new PermitChecklistItem { Position = n + 1, Section = i.Section, Text = i.Text, Mandatory = i.Mandatory, NoteRequired = i.NoteRequired });
        Assert.Equal(items, PermitLook.ParseChecklist(PermitLook.ChecklistText(saved)));
        Assert.Empty(PermitLook.ParseChecklist(""));
    }
}
