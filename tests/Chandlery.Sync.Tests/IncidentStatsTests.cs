using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The figures: what counts as lost time and as recordable, the rates per million hours, and how a fleet is added up from its vessels.</summary>
public class IncidentStatsTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();
    private static readonly DateTime Jan = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IncidentPerson Hurt(int lost = 0, int light = 0, IncidentTreatment treatment = IncidentTreatment.None, IncidentPersonRole role = IncidentPersonRole.Injured)
        => new() { Role = role, DaysLost = lost, RestrictedDays = light, Treatment = treatment, Name = "N" };

    private static IncidentCase Case(Guid vessel, DateTime occurred, IncidentKind kind = IncidentKind.NearMiss, IncidentStatus status = IncidentStatus.Reported, DateTime? closedAt = null,
        bool overdue = false, bool urgent = false, string band = "Low", DateTime? submitted = null, IncidentPerson? person = null)
        => new(new Incident { Id = Guid.NewGuid(), VesselId = vessel, Kind = kind, OccurredAt = occurred, SubmittedAt = submitted ?? occurred.AddHours(2), IsUrgent = urgent, Band = band, Persons = person is null ? [] : [person] }, status, closedAt, overdue);

    private static MonthHours H(DateTime month, decimal hours, string source = "Typed") => new(month, hours, source);

    // ---- definitions ----

    [Fact]
    public void LostTimeIsAnInjuredPersonWithDaysLost_NobodyElse()
    {
        Assert.True(IncidentStats.IsLostTime(Hurt(lost: 1)));
        Assert.False(IncidentStats.IsLostTime(Hurt(lost: 0)));
        Assert.False(IncidentStats.IsLostTime(Hurt(lost: 9, role: IncidentPersonRole.Witness)));
        Assert.False(IncidentStats.IsLostTime(Hurt(lost: 9, role: IncidentPersonRole.Involved)));
    }

    [Theory]
    [InlineData(0, 0, IncidentTreatment.None, false)]
    [InlineData(0, 0, IncidentTreatment.FirstAid, false)]           // first aid alone is not recordable
    [InlineData(0, 0, IncidentTreatment.MedicalTreatment, true)]
    [InlineData(0, 0, IncidentTreatment.Hospital, true)]
    [InlineData(0, 0, IncidentTreatment.Evacuation, true)]
    [InlineData(0, 1, IncidentTreatment.FirstAid, true)]            // light duty makes it recordable
    [InlineData(1, 0, IncidentTreatment.None, true)]                // a day lost makes it recordable
    public void ARecordableCaseNeedsMoreThanFirstAid(int lost, int light, IncidentTreatment treatment, bool expected)
        => Assert.Equal(expected, IncidentStats.IsRecordable(Hurt(lost, light, treatment)));

    [Fact]
    public void ANonInjuredPersonIsNeverRecordable() => Assert.False(IncidentStats.IsRecordable(Hurt(5, 5, IncidentTreatment.Hospital, IncidentPersonRole.Witness)));

    [Fact]
    public void TheRateIsCasesPerMillionHours_AndNothingWithoutHours()
    {
        Assert.Equal(4.0, IncidentStats.Rate(2, 500_000));
        Assert.Equal(0.0, IncidentStats.Rate(0, 100_000));
        Assert.Null(IncidentStats.Rate(3, 0));
        Assert.Null(IncidentStats.Rate(3, -5));
    }

    // ---- one vessel ----

    [Fact]
    public void AVesselIsCountedOverItsPeriod_FromTheReportsThatWereSubmittedAndNotWithdrawn()
    {
        var cases = new[]
        {
            Case(A, Jan.AddDays(3)),                                                                                                           // near miss in January
            Case(A, Jan.AddMonths(1).AddDays(5), IncidentKind.InjuryIllness, person: Hurt(lost: 4, treatment: IncidentTreatment.Hospital)),     // lost time, February
            Case(A, Jan.AddMonths(2).AddDays(1), IncidentKind.InjuryIllness, person: Hurt(treatment: IncidentTreatment.FirstAid)),              // first aid, March
            Case(A, Jan.AddMonths(2).AddDays(2), status: IncidentStatus.Withdrawn),                                                             // left out
            Case(A, Jan.AddMonths(3).AddDays(1)),                                                                                               // April: outside a 3-month period
            Case(A, Jan.AddDays(-3)),                                                                                                           // December: before it
        };
        cases[0].Incident.SubmittedAt = null;                                                                                                  // a draft: left out
        var row = IncidentStats.ForVessel(A, "MV One", cases, [], Jan, 3);

        Assert.Equal((2, 0, 2, 1, 4, 1), (row.Reports, row.NearMisses, row.InjuryReports, row.LostTimeInjuries, row.LostDays, row.Recordable));
        Assert.Null(row.Ltif);                                                                                                                  // no hours known
        Assert.Equal(0, row.MonthsWithHours);
        Assert.False(row.HoursComplete);
    }

    [Fact]
    public void RatesAreWorkedOutOnlyOverTheMonthsWhoseHoursAreKnown()
    {
        var cases = new[]
        {
            Case(A, Jan.AddDays(2), IncidentKind.InjuryIllness, person: Hurt(lost: 3)),                         // January: hours known
            Case(A, Jan.AddMonths(1).AddDays(2), IncidentKind.InjuryIllness, person: Hurt(lost: 8)),            // February: hours not known, so not in the rate
        };
        var row = IncidentStats.ForVessel(A, "MV One", cases, [H(Jan, 250_000m)], Jan, 2);
        Assert.Equal(2, row.LostTimeInjuries);                                                                  // the count has both
        Assert.Equal(1, row.LostTimeForRate);                                                                   // the rate has the one with hours
        Assert.Equal(4.0, row.Ltif);                                                                            // 1 per 250,000 hours
        Assert.Equal((1, 2, false), (row.MonthsWithHours, row.Months, row.HoursComplete));
        Assert.Equal(250_000m, row.Hours);
    }

    [Fact]
    public void HoursOutsideThePeriodAreLeftOut_AndEachMonthWithHoursIsCounted()
    {
        var row = IncidentStats.ForVessel(A, "MV One", [], [H(Jan, 1000m), H(Jan.AddMonths(1), 2000m, "Hours of rest"), H(Jan.AddMonths(5), 9999m)], Jan, 3);
        Assert.Equal(3000m, row.Hours);                                                                         // the month outside the period is left out
        Assert.Equal(2, row.MonthsWithHours);
    }

    [Fact]
    public void NearMissesPerInjuryReport_AndNoneWhenNobodyWasHurt()
    {
        var calm = IncidentStats.ForVessel(A, "MV One", [Case(A, Jan.AddDays(1)), Case(A, Jan.AddDays(2))], [], Jan, 1);
        Assert.Null(calm.NearMissesPerInjury);
        var mixed = IncidentStats.ForVessel(A, "MV One", [Case(A, Jan.AddDays(1)), Case(A, Jan.AddDays(2)), Case(A, Jan.AddDays(3)), Case(A, Jan.AddDays(4), IncidentKind.InjuryIllness, person: Hurt(1))], [], Jan, 1);
        Assert.Equal(3.0, mixed.NearMissesPerInjury);
    }

    [Fact]
    public void DaysToCloseIsTheAverageFromSubmittedToClosed_ForClosedReportsInThePeriodOnly()
    {
        var a = Case(A, Jan.AddDays(1), status: IncidentStatus.Closed, submitted: Jan.AddDays(1), closedAt: Jan.AddDays(11));
        var b = Case(A, Jan.AddDays(2), status: IncidentStatus.Closed, submitted: Jan.AddDays(2), closedAt: Jan.AddDays(22));
        var open = Case(A, Jan.AddDays(3));
        var row = IncidentStats.ForVessel(A, "MV One", [a, b, open], [], Jan, 1);
        Assert.Equal(15.0, row.MeanDaysToClose);                                                                // 10 and 20
        Assert.Null(IncidentStats.ForVessel(A, "MV One", [open], [], Jan, 1).MeanDaysToClose);
    }

    [Fact]
    public void OpenUrgentAndOverdueAreCountedFromTheCases_AndTheLastReportIsTheLatestEver()
    {
        var cases = new[]
        {
            Case(A, Jan.AddDays(1), status: IncidentStatus.Investigating, overdue: true, urgent: true),
            Case(A, Jan.AddDays(2), status: IncidentStatus.Closed, closedAt: Jan.AddDays(3)),
            Case(A, Jan.AddMonths(9), status: IncidentStatus.Reported),                                          // after the period: not counted, but it is the last report
        };
        var row = IncidentStats.ForVessel(A, "MV One", cases, [], Jan, 1);
        Assert.Equal((2, 1, 1, 1), (row.Reports, row.Open, row.Urgent, row.Overdue));
        Assert.Equal(cases[2].Incident.SubmittedAt, row.LastReported);
    }

    [Fact]
    public void ByKindAndByBandCountThePeriod()
    {
        var row = IncidentStats.ForVessel(A, "MV One", [Case(A, Jan.AddDays(1), band: "High"), Case(A, Jan.AddDays(2), IncidentKind.Pollution, band: "High"), Case(A, Jan.AddDays(3), band: "Low")], [], Jan, 1);
        Assert.Equal((2, 1, 0), (row.ByKind[IncidentKind.NearMiss], row.ByKind[IncidentKind.Pollution], row.ByKind[IncidentKind.Security]));
        Assert.Equal((2, 1), (row.ByBand["High"], row.ByBand["Low"]));
    }

    // ---- the fleet ----

    [Fact]
    public void TheFleetRateIsWorkedOutFromTheAddedCasesAndHours_NotAnAverageOfRates()
    {
        var one = IncidentStats.ForVessel(A, "A", [Case(A, Jan.AddDays(1), IncidentKind.InjuryIllness, person: Hurt(5))], [H(Jan, 100_000m)], Jan, 1);       // 10.0
        var two = IncidentStats.ForVessel(B, "B", [], [H(Jan, 900_000m)], Jan, 1);                                                                           // 0.0
        Assert.Equal(10.0, one.Ltif);
        Assert.Equal(0.0, two.Ltif);
        var fleet = IncidentStats.Combine("Fleet", [one, two], 1);
        Assert.Equal(1.0, fleet.Ltif);                                                                                                                       // 1 case in a million hours, not the average 5.0
        Assert.Equal((1, 1_000_000m, 5), (fleet.LostTimeInjuries, fleet.Hours, fleet.LostDays));
    }

    [Fact]
    public void TheFleetSumsTheCountsAndTakesTheLatestReportAndTheWeakestHoursCoverage()
    {
        var one = IncidentStats.ForVessel(A, "A", [Case(A, Jan.AddDays(1), IncidentKind.InjuryIllness, person: Hurt(lost: 1)), Case(A, Jan.AddDays(2))], [H(Jan, 1m), H(Jan.AddMonths(1), 1m)], Jan, 2);
        var two = IncidentStats.ForVessel(B, "B", [Case(B, Jan.AddDays(1), IncidentKind.InjuryIllness, person: Hurt(lost: 1), status: IncidentStatus.Closed, closedAt: Jan.AddDays(11), submitted: Jan.AddDays(1))], [H(Jan, 1m)], Jan, 2);
        var fleet = IncidentStats.Combine("Fleet", [one, two], 2);
        Assert.Equal((3, 1, 2, 2), (fleet.Reports, fleet.NearMisses, fleet.InjuryReports, fleet.LostTimeInjuries));
        Assert.Equal(1, fleet.MonthsWithHours);                                                                                                              // as many as the vessel that knows least
        Assert.Equal(0.5, fleet.NearMissesPerInjury);
        Assert.Equal(10.0, fleet.MeanDaysToClose);
        Assert.Equal(two.LastReported > one.LastReported ? two.LastReported : one.LastReported, fleet.LastReported);
        Assert.Equal(3, fleet.ByKind.Values.Sum());
    }

    [Fact]
    public void AnEmptyFleetIsAllZeros_WithNoRates()
    {
        var fleet = IncidentStats.Combine("Fleet", [], 6);
        Assert.Equal((0, 0m), (fleet.Reports, fleet.Hours));
        Assert.Null(fleet.Ltif);
    }

    // ---- trend and quiet vessels ----

    [Fact]
    public void TheTrendIsOneRowPerMonth_EvenWhereNothingHappened()
    {
        var cases = new[] { Case(A, Jan.AddDays(1)), Case(A, Jan.AddDays(2), IncidentKind.InjuryIllness, person: Hurt(1)), Case(A, Jan.AddDays(3), IncidentKind.Pollution), Case(A, Jan.AddMonths(2).AddDays(1)), Case(A, Jan.AddMonths(2).AddDays(2), status: IncidentStatus.Withdrawn) };
        var trend = IncidentStats.Trend(cases, Jan, 3);
        Assert.Equal([Jan, Jan.AddMonths(1), Jan.AddMonths(2)], trend.Select(t => t.Month));
        Assert.Equal((3, 1, 1, 1), (trend[0].Reports, trend[0].NearMisses, trend[0].Injuries, trend[0].Other));
        Assert.Equal(0, trend[1].Reports);
        Assert.Equal(1, trend[2].Reports);
    }

    [Fact]
    public void AVesselThatHasReportedNothingForAWhileIsListed_OrdersByHowLongAgo()
    {
        var today = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
        var vessels = new[] { (A, "Active"), (B, "Quiet"), (Guid.NewGuid(), "Never") };
        var cases = new[]
        {
            Case(A, today.AddDays(-20), submitted: today.AddDays(-20)),
            Case(B, today.AddMonths(-6), submitted: today.AddMonths(-6)),
            Case(B, today.AddMonths(-8), submitted: today.AddMonths(-8)),
        };
        var quiet = IncidentStats.Quiet(vessels, cases, today, 3);
        Assert.Equal(["Never", "Quiet"], quiet.Select(q => q.Vessel));
        Assert.Null(quiet[0].LastReported);
        Assert.Equal(today.AddMonths(-6), quiet[1].LastReported);
        Assert.Equal(["Never"], IncidentStats.Quiet(vessels, cases, today, 12).Select(q => q.Vessel));                                       // a longer window: the vessel reported within it
    }

    // ---- months and hours ----

    [Fact]
    public void MonthsRunFromTheFirstOfTheMonth()
    {
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), IncidentStats.MonthOf(new DateTime(2026, 3, 17, 14, 5, 0, DateTimeKind.Utc)));
        Assert.Equal([new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)], IncidentStats.MonthsFrom(new DateTime(2026, 11, 20), 3));
        Assert.Empty(IncidentStats.MonthsFrom(Jan, 0));
        Assert.Equal(7.5m, IncidentStats.HoursFromMinutes(450));
    }

    // ---- the files ----

    [Fact]
    public void TheStatisticsCsvHasOneRowPerVesselAndTheFleet_WithEmptyCellsWhereNoRateCanBeWorkedOut()
    {
        var one = IncidentStats.ForVessel(A, "MV One", [Case(A, Jan.AddDays(1), IncidentKind.InjuryIllness, person: Hurt(lost: 2))], [H(Jan, 500_000m)], Jan, 1);
        var none = IncidentStats.ForVessel(B, "MV Two", [], [], Jan, 1);
        var lines = IncidentReports.StatisticsCsv([IncidentStats.Combine("Fleet", [one, none], 1), one, none]).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(4, lines.Count);
        Assert.StartsWith("\"Vessel\",\"Reports\"", lines[0]);
        Assert.Contains("\"MV One\",\"1\",\"0\",\"1\",\"1\",\"2\",\"1\",\"500000\",\"1\",\"1\",\"2.00\",\"2.00\"", lines[2]);
        Assert.Contains("\"MV Two\",\"0\",\"0\",\"0\",\"0\",\"0\",\"0\",\"0\",\"0\",\"1\",\"\",\"\",\"\"", lines[3]);
    }

    [Fact]
    public void TheRegisterCsvListsEachReportWithItsDaysLostAndDueDate()
    {
        var incident = new Incident { Id = Guid.NewGuid(), Code = "INC-X-1", Kind = IncidentKind.InjuryIllness, OccurredAt = new DateTime(2026, 3, 4, 9, 30, 0, DateTimeKind.Utc), SubmittedAt = new DateTime(2026, 3, 4, 11, 0, 0, DateTimeKind.Utc), Place = "Galley", Band = "High", IsUrgent = true, Persons = [Hurt(lost: 6), Hurt(role: IncidentPersonRole.Witness)] };
        var csv = IncidentReports.RegisterCsv([new IncidentRow(incident, IncidentStatus.Investigating, true, false, "A. Reporter", 2, "MV One", new DateTime(2026, 3, 18))]);
        var data = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].TrimEnd('\r');
        Assert.Equal("\"INC-X-1\",\"MV One\",\"Injury or illness\",\"2026-03-04 09:30\",\"Galley\",\"High\",\"yes\",\"Investigating\",\"2026-03-04\",\"\",\"A. Reporter\",\"1\",\"6\",\"2026-03-18\",\"yes\",\"2\"", data);
    }
}
