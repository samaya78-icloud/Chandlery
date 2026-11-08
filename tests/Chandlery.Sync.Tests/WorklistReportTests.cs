using Chandlery.Models;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

public class WorklistReportTests
{
    private static readonly DateTime Today = new(2026, 11, 8);
    private static readonly Guid Falcon = Guid.NewGuid(), Heron = Guid.NewGuid(), Class = Guid.NewGuid(), Audit = Guid.NewGuid();

    private static WorklistTask Task(Guid vessel, string code, int? targetOffsetDays, WorklistStatus status = WorklistStatus.Open, int createdDaysAgo = 5,
        string stage = SyncStages.Office, Guid? source = null, bool verify = false, WorklistPriority priority = WorklistPriority.Normal) => new()
    {
        Id = Guid.NewGuid(), VesselId = vessel, Code = code, Title = code, Status = status, Priority = priority, OwnerStage = stage, SourceId = source,
        TargetDate = targetOffsetDays is { } d ? Today.AddDays(d) : null, CreatedAt = Today.AddDays(-createdDaysAgo), RequiresVerification = verify,
        CompletedAt = status == WorklistStatus.Completed ? Today : null,
    };

    private static readonly (Guid Id, string Name, Guid? GroupId)[] Vessels = [(Falcon, "MV Sea Falcon", null), (Heron, "MV Grey Heron", null)];

    [Fact]
    public void PerVessel_CountsOpenOverdueDueSoonUrgentVerificationAndNew()
    {
        var tasks = new[]
        {
            Task(Falcon, "A", -3),                                                                    // overdue
            Task(Falcon, "B", 2, priority: WorklistPriority.Urgent),                                   // due this week, urgent
            Task(Falcon, "C", 30),                                                                     // later
            Task(Falcon, "D", -20, WorklistStatus.Completed, verify: true),                            // completed, but still open until the office verifies
            Task(Falcon, "E", null, createdDaysAgo: 2, stage: SyncStages.Vessel),                      // raised onboard two days ago
            Task(Heron, "F", -1, WorklistStatus.Cancelled),                                            // cancelled never counts
        };
        var rows = WorklistReports.PerVessel(tasks, Vessels, Today);

        var falcon = rows.Single(r => r.VesselId == Falcon);
        Assert.Equal((5, 1, 1, 1, 1, 1), (falcon.Open, falcon.Overdue, falcon.DueThisWeek, falcon.Urgent, falcon.AwaitingVerification, falcon.NewFromVessel));
        Assert.Equal(0, rows.Single(r => r.VesselId == Heron).Open);
        Assert.Equal(Falcon, rows[0].VesselId);                                                        // worst first
    }

    [Fact]
    public void Escalations_AreOpenTasksOverdueByMoreThanTheLimit_LongestFirst()
    {
        var tasks = new[] { Task(Falcon, "A", -5), Task(Falcon, "B", -15), Task(Falcon, "C", -40), Task(Falcon, "D", -40, WorklistStatus.Completed), Task(Falcon, "E", 3) };
        Assert.Equal(["C", "B"], WorklistReports.Escalations(tasks, Today, 14).Select(t => t.Code));
        Assert.Equal(["C", "B", "A"], WorklistReports.Escalations(tasks, Today, 0).Select(t => t.Code));
        Assert.Equal(15, WorklistReports.DaysOverdue(tasks[1], Today));
        Assert.Equal(0, WorklistReports.DaysOverdue(tasks[4], Today));
    }

    [Fact]
    public void Ageing_GroupsOpenTasksByTimeSinceTheyWereRaised()
    {
        var tasks = new[] { Task(Falcon, "A", null, createdDaysAgo: 10), Task(Falcon, "B", null, createdDaysAgo: 31), Task(Falcon, "C", null, createdDaysAgo: 120), Task(Falcon, "D", null, createdDaysAgo: 400), Task(Falcon, "E", null, WorklistStatus.Completed, 400) };
        Assert.Equal([1, 1, 1, 1], WorklistReports.Ageing(tasks, Today).Select(a => a.Count));
    }

    [Fact]
    public void CompletionBySource_IgnoresCancelled_AndWorksOutTheRate()
    {
        var sources = new Dictionary<Guid, string> { [Class] = "Class", [Audit] = "Internal audit" };
        var tasks = new[]
        {
            Task(Falcon, "A", -2, source: Class), Task(Falcon, "B", 5, WorklistStatus.Completed, source: Class), Task(Falcon, "C", 5, WorklistStatus.Completed, source: Class),
            Task(Falcon, "D", 5, WorklistStatus.Cancelled, source: Class), Task(Falcon, "E", null, WorklistStatus.Completed, source: Audit), Task(Falcon, "F", null),
        };
        var rows = WorklistReports.CompletionBySource(tasks, sources, Today);
        var cls = rows.Single(r => r.Source == "Class");
        Assert.Equal((3, 2, 1, 1, 67), (cls.Total, cls.Completed, cls.Open, cls.Overdue, cls.RatePercent));
        Assert.Equal(100, rows.Single(r => r.Source == "Internal audit").RatePercent);
        Assert.Equal(0, rows.Single(r => r.Source == "No source").RatePercent);
    }

    [Fact]
    public void CompletionBySource_CountsATaskAwaitingVerificationAsOpen_SoDoneAndOpenAddUp()
    {
        var sources = new Dictionary<Guid, string> { [Class] = "Class" };
        var tasks = new[] { Task(Falcon, "A", null, WorklistStatus.Completed, source: Class, verify: true), Task(Falcon, "B", null, WorklistStatus.Completed, source: Class) };
        var row = WorklistReports.CompletionBySource(tasks, sources, Today).Single();
        Assert.Equal((2, 1, 1, 50), (row.Total, row.Completed, row.Open, row.RatePercent));
    }

    [Fact]
    public void UrgentThingsTravelOnTheUrgentLane()
    {
        Assert.Equal(Marine.Sync.Lane.Urgent, Chandlery.Services.Sync.SyncPublisher.LaneFor(new PurchaseOrder { IsEmergency = true }));
        Assert.Equal(Marine.Sync.Lane.Urgent, Chandlery.Services.Sync.SyncPublisher.LaneFor(Task(Falcon, "A", null, priority: WorklistPriority.Urgent)));
        Assert.Equal(Marine.Sync.Lane.Transaction, Chandlery.Services.Sync.SyncPublisher.LaneFor(Task(Falcon, "B", null)));
        var created = new WorklistEntry { Type = WorklistEntryType.Created, Payload = WorklistProjection.Json(("priority", WorklistPriority.Urgent)) };
        var raised = new WorklistEntry { Type = WorklistEntryType.Priority, Payload = WorklistProjection.Json(("priority", WorklistPriority.Urgent)) };
        var calmed = new WorklistEntry { Type = WorklistEntryType.Priority, Payload = WorklistProjection.Json(("priority", WorklistPriority.Normal)) };
        var comment = new WorklistEntry { Type = WorklistEntryType.Comment, Payload = "{}" };
        Assert.Equal([Marine.Sync.Lane.Urgent, Marine.Sync.Lane.Urgent, Marine.Sync.Lane.Transaction, Marine.Sync.Lane.Transaction],
            new[] { created, raised, calmed, comment }.Select(Chandlery.Services.Sync.SyncPublisher.LaneFor));
        Assert.Equal(Marine.Sync.Lane.Transaction, Chandlery.Services.Sync.SyncPublisher.LaneFor(new WorklistEntry { Type = WorklistEntryType.Priority, Payload = "not json" }));
    }

    [Fact]
    public void Csv_QuotesEveryFieldAndDoublesQuotes()
    {
        var csv = WorklistReports.Csv(["Title", "Note"], [["Fire hose, 15m", "said \"renew\""]]);
        Assert.Equal("\"Title\",\"Note\"\r\n\"Fire hose, 15m\",\"said \"\"renew\"\"\"\r\n".Replace("\r\n", Environment.NewLine), csv);
    }

    [Fact]
    public void InGroup_KeepsTheVesselsOfTheGroupAndAnythingBelowIt()
    {
        var top = new VesselGroup { Id = Guid.NewGuid(), Name = "Tankers", Level = 1 };
        var sub = new VesselGroup { Id = Guid.NewGuid(), Name = "Product", Level = 2, ParentId = top.Id };
        var other = new VesselGroup { Id = Guid.NewGuid(), Name = "Bulkers", Level = 1 };
        var snap = new WorklistSnapshot(
            [Task(Falcon, "A", -1), Task(Heron, "B", -1)], [(Falcon, "MV Sea Falcon", sub.Id), (Heron, "MV Grey Heron", other.Id)], [], [], [top, sub, other]);

        Assert.Equal([Falcon], WorklistReportService.InGroup(snap, top.Id).Vessels.Select(v => v.Id));
        Assert.Equal(["A"], WorklistReportService.InGroup(snap, top.Id).Tasks.Select(t => t.Code));
        Assert.Equal(2, WorklistReportService.InGroup(snap, null).Vessels.Count);
    }
}
