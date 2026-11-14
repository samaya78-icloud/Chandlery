using Chandlery.Models;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

/// <summary>The fleet view's rules: where a project sits on the timeline, the window, workload by month and spend. No database needed.</summary>
public class ProjectFleetTests
{
    private static readonly DateTime Today = new(2026, 11, 13);

    private static ProjectListRow Row(string vessel, ProjectStatus status, DateTime? plannedStart, DateTime? plannedEnd, DateTime? actualStart = null, DateTime? actualEnd = null,
        ProjectType type = ProjectType.Drydock, decimal budget = 0, decimal cost = 0)
    {
        var project = new Project { Id = Guid.NewGuid(), Code = "P", Title = "T", Type = type, PlannedStart = plannedStart, PlannedEnd = plannedEnd, CreatedAt = new DateTime(2026, 1, 5) };
        return new ProjectListRow(new ProjectRow(project, status, actualStart, actualEnd, 0, 0, 0), vessel, budget, 0, new ProjectCost(cost, 0, 0), null, null);
    }

    [Fact]
    public void ARealStartBeatsAPlannedOne_ARunningProjectRunsToTodayOrItsPlannedEnd_AndAProjectWithNoDateIsNotPlaced()
    {
        var planned = ProjectFleetRules.SpanOf(Row("A", ProjectStatus.Approved, new DateTime(2027, 2, 1), new DateTime(2027, 2, 20)), Today)!;
        Assert.Equal((new DateTime(2027, 2, 1), new DateTime(2027, 2, 20), false), (planned.Start, planned.End, planned.Actual));

        var running = ProjectFleetRules.SpanOf(Row("A", ProjectStatus.Ongoing, new DateTime(2026, 11, 1), new DateTime(2026, 11, 10), actualStart: new DateTime(2026, 11, 3)), Today)!;
        Assert.Equal((new DateTime(2026, 11, 3), Today, true), (running.Start, running.End, running.Actual));                    // overrunning: drawn to today

        var early = ProjectFleetRules.SpanOf(Row("A", ProjectStatus.Ongoing, null, new DateTime(2026, 11, 30), actualStart: new DateTime(2026, 11, 3)), Today)!;
        Assert.Equal(new DateTime(2026, 11, 30), early.End);                                                                      // still inside its plan: drawn to the planned end

        var done = ProjectFleetRules.SpanOf(Row("A", ProjectStatus.Complete, null, null, actualStart: new DateTime(2026, 9, 1), actualEnd: new DateTime(2026, 9, 20)), Today)!;
        Assert.Equal(new DateTime(2026, 9, 20), done.End);

        Assert.Null(ProjectFleetRules.SpanOf(Row("A", ProjectStatus.Planning, null, new DateTime(2027, 1, 1)), Today));
        Assert.Equal(new DateTime(2027, 3, 1), ProjectFleetRules.SpanOf(Row("A", ProjectStatus.Planning, new DateTime(2027, 3, 1), null), Today)!.End);   // no end: one day
    }

    [Fact]
    public void TheWindowCoversEverySpanAndThisMonth_WithAtLeastSixMonths_AndPositionsFallInsideIt()
    {
        var none = ProjectFleetRules.Window([], Today);
        Assert.Equal((new DateTime(2026, 11, 1), 6), none);

        var spans = ProjectFleetRules.Spans([Row("A", ProjectStatus.Complete, null, null, actualStart: new DateTime(2026, 3, 10), actualEnd: new DateTime(2026, 4, 2)),
                                             Row("B", ProjectStatus.Approved, new DateTime(2027, 5, 20), new DateTime(2027, 6, 5))], Today);
        var (from, months) = ProjectFleetRules.Window(spans, Today);
        Assert.Equal((new DateTime(2026, 3, 1), 16), (from, months));                                                               // March 2026 to June 2027

        Assert.Equal(0, ProjectFleetRules.Position(from, from, months));
        Assert.Equal(1, ProjectFleetRules.Position(from.AddMonths(months), from, months));
        Assert.Equal(1, ProjectFleetRules.Position(from.AddYears(5), from, months));                                                // clamped
    }

    [Fact]
    public void WorkloadCountsAProjectInEveryMonthItTouches_AndSplitsItsDaysByMonth()
    {
        var spans = ProjectFleetRules.Spans([Row("A", ProjectStatus.Complete, null, null, actualStart: new DateTime(2026, 1, 25), actualEnd: new DateTime(2026, 2, 3), type: ProjectType.Drydock),
                                             Row("B", ProjectStatus.Complete, null, null, actualStart: new DateTime(2026, 2, 1), actualEnd: new DateTime(2026, 2, 10), type: ProjectType.NonDrydock)], Today);
        var workload = ProjectFleetRules.Workload(spans, new DateTime(2026, 1, 1), 3);

        Assert.Equal((1, 1, 7), (workload[0].Projects, workload[0].Drydocks, workload[0].Days));                                    // 25 to 31 January
        Assert.Equal((2, 1, 13), (workload[1].Projects, workload[1].Drydocks, workload[1].Days));                                   // 3 days of A and 10 of B
        Assert.Equal((0, 0, 0), (workload[2].Projects, workload[2].Drydocks, workload[2].Days));
    }

    [Fact]
    public void SpendAddsUpByVesselAndByYear_AndOverBudgetIsOnlyTheExcessOfProjectsThatHaveABudget()
    {
        var rows = new[]
        {
            Row("Falcon", ProjectStatus.Closed, new DateTime(2026, 2, 1), null, budget: 50_000m, cost: 62_000m),     // over by 12,000
            Row("Falcon", ProjectStatus.Ongoing, new DateTime(2026, 9, 1), null, budget: 20_000m, cost: 5_000m),
            Row("Osprey", ProjectStatus.Closed, new DateTime(2025, 6, 1), null, budget: 0m, cost: 9_000m),          // no budget: not "over"
        };
        var byVessel = ProjectFleetRules.SpendByVessel(rows);
        Assert.Equal(["Falcon", "Osprey"], byVessel.Select(s => s.Vessel));                                                         // most spent first
        Assert.Equal((2, 70_000m, 67_000m, 12_000m), (byVessel[0].Projects, byVessel[0].Budget, byVessel[0].Cost, byVessel[0].Over));
        Assert.Equal((1, 0m, 9_000m, 0m), (byVessel[1].Projects, byVessel[1].Budget, byVessel[1].Cost, byVessel[1].Over));

        var byYear = ProjectFleetRules.SpendByYear(rows);
        Assert.Equal([2026, 2025], byYear.Select(y => y.Year));
        Assert.Equal((67_000m, 12_000m), (byYear[0].Spend.Cost, byYear[0].Spend.Over));
    }
}
