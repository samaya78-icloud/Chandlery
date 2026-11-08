using System.Diagnostics;
using System.Text;
using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

public class PmsImportParsingTests
{
    [Theory]
    [InlineData("3 months", 3, PmsIntervalUnit.Months, null)]
    [InlineData("3M", 3, PmsIntervalUnit.Months, null)]
    [InlineData("1 year", 1, PmsIntervalUnit.Years, null)]
    [InlineData("Annual", 1, PmsIntervalUnit.Years, null)]
    [InlineData("Quarterly", 3, PmsIntervalUnit.Months, null)]
    [InlineData("half-yearly", 6, PmsIntervalUnit.Months, null)]
    [InlineData("Weekly", 1, PmsIntervalUnit.Weeks, null)]
    [InlineData("2 weeks", 2, PmsIntervalUnit.Weeks, null)]
    [InlineData("14 days", 14, PmsIntervalUnit.Days, null)]
    [InlineData("5 yearly", 5, PmsIntervalUnit.Years, null)]
    [InlineData("500 hrs", null, PmsIntervalUnit.Months, 500)]
    [InlineData("4000h", null, PmsIntervalUnit.Months, 4000)]
    [InlineData("2,000 running hours", null, PmsIntervalUnit.Months, null)]       // a thousands separator is not guessed: unreadable is better than wrong
    [InlineData("6 months / 4000 hrs", 6, PmsIntervalUnit.Months, 4000)]
    [InlineData("6M or 4000 HRS", 6, PmsIntervalUnit.Months, 4000)]
    [InlineData("500 hrs, 3 months", 3, PmsIntervalUnit.Months, 500)]
    public void IntervalsAreReadInTheWaysPeopleWriteThem(string text, int? calendar, PmsIntervalUnit unit, int? hours)
    {
        var p = PmsIntervals.Parse(text);
        if (calendar is null && hours is null) { Assert.False(p.Ok); return; }
        Assert.True(p.Ok);
        Assert.Equal((calendar, hours), (p.Calendar, p.Hours));
        if (calendar is not null) Assert.Equal(unit, p.Unit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("when needed")]
    [InlineData("0 months")]
    [InlineData("3 fortnights")]
    [InlineData("3 months / 6 months")]
    [InlineData("-5 days")]
    public void IntervalsThatCannotBeReadAreRefused(string text) => Assert.False(PmsIntervals.Parse(text).Ok);

    [Theory]
    [InlineData("2026-03-05", 2026, 3, 5)]
    [InlineData("05/03/2026", 2026, 3, 5)]
    [InlineData("5/3/2026", 2026, 3, 5)]
    [InlineData("05-03-2026", 2026, 3, 5)]
    [InlineData("05.03.2026", 2026, 3, 5)]
    [InlineData("5 Mar 2026", 2026, 3, 5)]
    [InlineData("05-Mar-26", 2026, 3, 5)]
    [InlineData("31/12/2025", 2025, 12, 31)]
    [InlineData("46086", 2026, 3, 5)]                                              // an Excel date serial
    public void DatesAreReadDayFirst(string text, int y, int m, int d) => Assert.Equal(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc), PmsDates.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("32/01/2026")]
    [InlineData("13/13/2026")]
    public void DatesThatCannotBeReadGiveNothing(string text) => Assert.Null(PmsDates.Parse(text));

    [Fact]
    public void ACsvIsReadWithQuotesBomAndOtherDelimiters()
    {
        var csv = "﻿Location;Job;Interval\r\n\"Engine room\";\"Check, clean \"\"filters\"\"\";3 months\r\n\r\nDeck;Grease;Weekly;ignored\r\n";
        var table = PmsImportFile.ReadCsv(csv);
        Assert.Equal(["Location", "Job", "Interval"], table.Headers);
        Assert.Equal(["Engine room", "Check, clean \"filters\"", "3 months"], table.Rows[0]);
        Assert.Equal(2, table.Rows.Count);                                                    // the blank line is skipped
        Assert.Equal(["Deck", "Grease", "Weekly", "ignored"], table.Rows[1]);

        var padded = PmsImportFile.ReadCsv("A,B,C\n1,2\n");
        Assert.Equal(["1", "2", ""], padded.Rows[0]);                                         // short rows are padded
        Assert.Empty(PmsImportFile.ReadCsv("").Headers);
    }

    [Fact]
    public void TheColumnsAreGuessedFromTheHeadings_WithoutTakingOneColumnTwice()
    {
        var map = PmsImportColumns.Suggest(["Location", "System", "Equipment", "Job code", "Job", "Interval", "Last done", "Hours at last done", "Critical", "Department"]);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], new[] { ImportField.Location, ImportField.System, ImportField.Equipment, ImportField.JobCode, ImportField.JobTitle, ImportField.Interval,
            ImportField.LastDoneDate, ImportField.LastDoneHours, ImportField.Critical, ImportField.Department }.Select(map.Of));
        Assert.Equal(-1, map.Of(ImportField.Instructions));

        var other = PmsImportColumns.Suggest(["Section", "Machinery", "Task No", "Maintenance task", "Frequency", "Last completed", "Notes"]);
        Assert.Equal((0, 1, 2, 3, 4, 5, 6), (other.Of(ImportField.System), other.Of(ImportField.Equipment), other.Of(ImportField.JobCode), other.Of(ImportField.JobTitle),
            other.Of(ImportField.Interval), other.Of(ImportField.LastDoneDate), other.Of(ImportField.Instructions)));
    }

    private static ImportPlan Plan(string csv, IReadOnlyList<JobTemplate>? templates = null, ISet<string>? existingJobs = null, ISet<string>? existingPaths = null)
    {
        var table = PmsImportFile.ReadCsv(csv);
        return PmsImportPlanner.Plan(table, PmsImportColumns.Suggest(table.Headers), templates ?? [], existingJobs ?? new HashSet<string>(), existingPaths ?? new HashSet<string>(), new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void EachRowIsCheckedAndTheProblemsAreSaidInPlainWords()
    {
        var plan = Plan("""
            Location,Equipment,Job code,Job,Interval,Last done,Hours at last done
            Engine room,Main engine,ME-1,Check injectors,6 months,12/05/2026,
            Engine room,Main engine,ME-2,Oil sample,sometimes,12/05/2026,
            Engine room,Main engine,,No code here,1 month,,
            Engine room,Main engine,ME-4,Future job,1 month,01/01/2030,
            Engine room,Main engine,ME-5,Bad date,1 month,yesterday,
            Engine room,Main engine,ME-1,Duplicate of the first,1 month,,
            ,,ME-7,No machinery,1 month,,
            Engine room,Main engine,ME-8,Hours job,500 hrs,01/10/2026,
            Engine room,Main engine,ME-9,Hours without date,500 hrs,,1000
            """);
        Assert.Equal((9, 3, 6), (plan.Rows.Count, plan.Valid, plan.Invalid));
        string Why(int line) => string.Join(" ", plan.Rows.Single(r => r.Line == line).Errors);
        Assert.Contains("could not be read", Why(3));
        Assert.Contains("No job code", Why(4));
        Assert.Contains("in the future", Why(5));
        Assert.Contains("could not be read", Why(6));
        Assert.Contains("twice", Why(7));
        Assert.Contains("No machinery", Why(8));
        Assert.Contains("hours date is unknown", string.Join(" ", plan.Rows.Single(r => r.Line == 9).Warnings));        // an hours job with no hours at the last completion
        Assert.Contains("ignored", string.Join(" ", plan.Rows.Single(r => r.Line == 10).Warnings));                    // hours without a date
        Assert.Equal(2, plan.HoursJobs);
    }

    [Fact]
    public void LibraryTemplatesAreSuggested_BySameCode_SameTitle_OrAVerySimilarTitle()
    {
        var templates = new[]
        {
            new JobTemplate { Id = Guid.NewGuid(), Code = "ME-101", Title = "Overhaul fuel injectors", IsActive = true },
            new JobTemplate { Id = Guid.NewGuid(), Code = "GE-200", Title = "Change generator engine oil and filters", IsActive = true },
            new JobTemplate { Id = Guid.NewGuid(), Code = "OLD-1", Title = "Retired job", IsActive = false },
        };
        var plan = Plan("""
            Equipment,Job code,Job,Interval
            Main engine,ME-101,Whatever the vessel calls it,6 months
            Main engine,X-2,Fuel injectors overhaul,6 months
            Generator,X-3,Change oil and filters on the generator engine now,500 hrs
            Generator,X-4,Clean the galley,1 month
            Generator,OLD-1,Retired job,1 month
            """, templates);
        MatchKind Kind(int line) => plan.Rows.Single(r => r.Line == line).MatchKind;
        Assert.Equal(MatchKind.Code, Kind(2));
        Assert.Equal(MatchKind.Title, Kind(3));                                                                  // same words, different order
        Assert.Equal(MatchKind.Similar, Kind(4));                                                                // the same words and one more
        Assert.Equal(MatchKind.None, Kind(5));
        Assert.Equal(MatchKind.None, Kind(6));                                                                   // a retired template is never suggested
        Assert.Equal(3, plan.WithLibraryMatch);
    }

    [Fact]
    public void ANumberInATitleCountsAsPartOfIt()
    {
        var templates = new[] { new JobTemplate { Id = Guid.NewGuid(), Code = "FP-1", Title = "Test fire pump 1", IsActive = true } };
        var plan = Plan("""
            Equipment,Job code,Job,Interval
            Pump,A-1,Test fire pump 1,1 month
            Pump,A-2,Test fire pump 2,1 month
            """, templates);
        Assert.Equal([MatchKind.Title, MatchKind.None], plan.Rows.Select(r => r.MatchKind));
    }

    [Fact]
    public void WhatIsAlreadyOnThePlanIsRecognised_AndTheSampleFileIsClean()
    {
        var plan = Plan("Equipment,Job code,Job,Interval\nMain engine,ME-1,Check,1 month\nMain engine,ME-2,Oil,1 month\n",
            existingJobs: new HashSet<string> { "main engine|me-1" }, existingPaths: new HashSet<string> { "main engine" });
        Assert.Equal((2, 1, 0), (plan.Valid, plan.AlreadyOnPlan, plan.NewMachinery));
        Assert.Contains("will be skipped", plan.Rows[0].Warnings.Single());

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "wwwroot", "samples", "pms-plan-sample.csv"))) dir = dir.Parent;
        var sample = Plan(File.ReadAllText(Path.Combine(dir!.FullName, "wwwroot", "samples", "pms-plan-sample.csv")));
        Assert.Equal((15, 0), (sample.Valid, sample.Invalid));
        Assert.Equal(0, sample.Rows.Sum(r => r.Errors.Count));
    }
}

/// <summary>Importing a plan onto a real office and a real vessel.</summary>
public class PmsImportTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private PmsService _shore = null!, _vessel = null!;
    private PmsImportService _import = null!;
    private PmsReportService _reports = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new PmsService(_s.ShoreFactory);
        _vessel = new PmsService(_s.VesselFactory);
        _import = new PmsImportService(_s.ShoreFactory);
        _reports = new PmsReportService(_s.ShoreFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Pms, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnShore<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.ShoreFactory.CreateDbContextAsync(); return await read(db); }
    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    private static string D(int daysAgo) => Today.AddDays(-daysAgo).ToString("dd/MM/yyyy");

    private async Task<(ImportPlan Plan, ImportTable Table)> PreviewAsync(string csv)
    {
        var table = PmsImportFile.ReadCsv(csv);
        return (await _import.PreviewAsync(_s.CompanyId, _s.VesselId, table, PmsImportColumns.Suggest(table.Headers)), table);
    }

    [Fact]
    public async Task APlanIsImported_AsMachineryTreeJobsAndReadOnlyHistory_AndCanBeImportedAgain()
    {
        var cls = await _shore.SaveMachineryClassAsync(_s.CompanyId, null, "Main engine", null);
        var template = await _shore.SaveJobTemplateAsync(_s.CompanyId, null, new TemplateInput("ME-001", "Overhaul fuel injectors", "Remove\nTest", PmsJobType.Overhaul, CalendarInterval: 6, HoursInterval: 4000, IsCritical: true, MachineryClassId: cls.Id));
        await _s.SettleUntilQuietAsync();

        var csv = $"""
            Location,System,Equipment,Job code,Job,Interval,Last done,Hours at last done,Critical
            Engine room,Main propulsion,Main engine,ME-001,Injector overhaul (own wording),3 months / 1000 hrs,{D(20)},18250,No
            Engine room,Main propulsion,Main engine,ME-002,Take lube oil sample,3 months,{D(40)},,Yes
            Engine room,Auxiliary,Generator No. 1,GE-001,Change engine oil,500 hrs,{D(10)},9540,No
            Deck,Mooring,Windlass,WL-001,Grease bearings,Weekly,{D(2)},,No
            Deck,Mooring,Windlass,WL-002,Brake lining,sometimes,,,No
            """;
        var (plan, table) = await PreviewAsync(csv);
        Assert.Equal((4, 1, 3, 1, 2), (plan.Valid, plan.Invalid, plan.NewMachinery, plan.WithLibraryMatch, plan.HoursJobs));       // three machinery paths, ME-001 matches the library, two jobs run on hours

        var result = await _import.ApplyAsync(_s.CompanyId, _s.VesselId, plan.Rows, r => r.Match is not null, _s.User);
        Assert.Equal((4, 1, 3, 4, 0), (result.JobsAdded, result.FromLibrary, result.OwnJobs, result.HistoryRecorded, result.Skipped));
        Assert.Equal(8, result.MachineryAdded);                                                                                        // every level of every path, shared parents once

        var items = await OnShore(db => db.MachineryItems.Where(m => m.VesselId == _s.VesselId).ToListAsync());
        string Path(MachineryItem i) => i.ParentId is { } p ? Path(items.Single(x => x.Id == p)) + " › " + i.Name : i.Name;
        Assert.Contains(items, i => Path(i) == "Engine room › Main propulsion › Main engine" && i.HasHourMeter);                       // the library job runs on hours, so the meter is switched on
        Assert.Contains(items, i => Path(i) == "Engine room › Auxiliary › Generator No. 1" && i.HasHourMeter);
        Assert.Contains(items, i => Path(i) == "Deck › Mooring › Windlass" && !i.HasHourMeter);
        Assert.Equal(1, items.Count(i => i.Name == "Engine room"));

        var jobs = await OnShore(db => db.PlannedJobs.Where(j => j.VesselId == _s.VesselId).ToListAsync());
        var fromLibrary = jobs.Single(j => j.Code == "ME-001");
        Assert.Equal((template.Id, 1, 6, 4000, "Overhaul fuel injectors", true), (fromLibrary.TemplateId, fromLibrary.TemplateVersion!.Value, fromLibrary.CalendarInterval!.Value, fromLibrary.HoursInterval!.Value, fromLibrary.Title, fromLibrary.IsCritical));
        var own = jobs.Single(j => j.Code == "ME-002");
        Assert.Equal((null, 3, true, SyncStages.Office), (own.TemplateId, own.CalendarInterval, own.IsCritical, own.OwnerStage));
        Assert.Equal(Today.AddDays(-2).AddDays(7), jobs.Single(j => j.Code == "WL-001").NextDueDate);                                  // counted from the imported last done date
        Assert.Equal(Today.AddDays(-40).AddMonths(3), own.NextDueDate);

        // The last time each was done is history: read-only, not counted as work done on this system.
        var history = await OnShore(db => db.JobCompletions.Where(c => c.VesselId == _s.VesselId).ToListAsync());
        Assert.All(history, c => Assert.True(c.IsImported));
        Assert.Equal(18250m, history.Single(c => c.PlannedJobId == fromLibrary.Id).HoursAtDone);
        Assert.Empty(await _reports.DoneAsync(_s.CompanyId, Today.AddDays(-90), Today));
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            (await db.JobCompletions.FirstAsync()).Comments = "rewritten";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        // The same file again adds nothing.
        var (again, _) = await PreviewAsync(csv);
        Assert.Equal(4, again.AlreadyOnPlan);
        var second = await _import.ApplyAsync(_s.CompanyId, _s.VesselId, again.Rows, r => r.Match is not null, _s.User);
        Assert.Equal((0, 0, 0, 4), (second.JobsAdded, second.MachineryAdded, second.HistoryRecorded, second.Skipped));

        // And it all reaches the vessel with the same schedules.
        await _s.SettleUntilQuietAsync();
        Assert.Equal(4, (await OnVessel(db => db.PlannedJobs.ToListAsync())).Count);
        foreach (var j in jobs)
            Assert.Equal(j.NextDueDate, (await OnVessel(db => db.PlannedJobs.SingleAsync(x => x.Id == j.Id))).NextDueDate);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
        _ = table;
    }

    [Fact]
    public async Task ExistingMachineryIsReused_AndAnHoursJobOnMachineryThatCannotGetAMeterIsLeftOutWithAReason()
    {
        var room = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Engine room"), _s.User);
        var engine = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", ParentId: room.Id), _s.User);          // no meter yet, the office's own
        await _s.SettleUntilQuietAsync();
        var vesselOwn = await _vessel.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Emergency generator"), _s.User);                   // the vessel's, so the office cannot give it a meter
        await _s.SettleUntilQuietAsync();

        var csv = """
            Equipment,Job code,Job,Interval
            Main engine,A-1,Hours job on the office's machinery,500 hrs
            Emergency generator,B-1,Hours job on the vessel's machinery,500 hrs
            Emergency generator,B-2,Calendar job there,1 month
            """;
        var table = PmsImportFile.ReadCsv(csv);
        // Engine room and Main engine are one path in the file's "Equipment" column only; use the hierarchy the vessel already has.
        var plan = PmsImportPlanner.Plan(table, PmsImportColumns.Suggest(table.Headers), [], new HashSet<string>(), new HashSet<string>(), Today);
        var result = await _import.ApplyAsync(_s.CompanyId, _s.VesselId, plan.Rows, _ => false, _s.User);

        Assert.Equal(2, result.JobsAdded);                                                                                                // A-1 (a new item, since its path is just "Main engine") and B-2
        var problem = Assert.Single(result.Problems);
        Assert.Contains("B-1", problem);
        Assert.Contains("no hour meter", problem);
        Assert.Equal(2, (await OnShore(db => db.MachineryItems.CountAsync(m => m.Name == "Main engine"))));                              // a top-level "Main engine" is a different path from the nested one
        _ = (engine, vesselOwn);
    }

    [Fact]
    public async Task AFileOfThreeThousandJobs_IsPreviewedAndImported_AndReachesTheVessel()
    {
        var templates = new List<JobTemplate>();
        for (var i = 0; i < 40; i++)
            templates.Add(await _shore.SaveJobTemplateAsync(_s.CompanyId, null, new TemplateInput($"J{i:0000}", $"Library job {i} inspection routine", CalendarInterval: 6, MachineryClassId: null)));
        await _s.SettleUntilQuietAsync();

        var sb = new StringBuilder("Location,System,Equipment,Job code,Job,Interval,Last done,Hours at last done\n");
        for (var i = 0; i < 3000; i++)
        {
            var interval = (i % 5) switch { 0 => "1 month", 1 => "3 months", 2 => "6 months", 3 => "Annual", _ => "500 hrs" };
            var title = i < 40 ? $"Library job {i} inspection routine" : $"Routine task number {i} on the machinery";
            sb.Append($"Deck {i % 6},System {i % 25},Equipment {i % 300},J{i:0000},{title},{interval},{D(i % 200)},{(i % 5 == 4 ? 1000 + i : "")}\n");
        }

        var clock = Stopwatch.StartNew();
        var table = PmsImportFile.ReadCsv(sb.ToString());
        var plan = await _import.PreviewAsync(_s.CompanyId, _s.VesselId, table, PmsImportColumns.Suggest(table.Headers));
        var previewTime = clock.Elapsed;
        Assert.Equal((3000, 0), (plan.Valid, plan.Invalid));
        Assert.Equal(40, plan.WithLibraryMatch);                                                                                           // mapped to the library where possible
        Assert.True(previewTime < TimeSpan.FromSeconds(30), $"preview took {previewTime}");

        clock.Restart();
        var result = await _import.ApplyAsync(_s.CompanyId, _s.VesselId, plan.Rows, r => r.Match is not null && r.MatchKind is MatchKind.Code, _s.User);
        var importTime = clock.Elapsed;
        Assert.Equal((3000, 40, 2960, 3000), (result.JobsAdded, result.FromLibrary, result.OwnJobs, result.HistoryRecorded));
        Assert.True(importTime < TimeSpan.FromMinutes(3), $"import took {importTime}");
        Assert.Equal(3000, await OnShore(db => db.PlannedJobs.CountAsync()));
        // Every job with a calendar interval has a due date at once; the jobs that run only on hours (592 of the 600, the other 8 being library jobs with a calendar interval)
        // get theirs when running-hours readings come in.
        Assert.Equal(592, await OnShore(db => db.PlannedJobs.CountAsync(j => j.NextDueDate == null)));
        Assert.Equal(0, await OnShore(db => db.PlannedJobs.CountAsync(j => j.NextDueDate == null && j.CalendarInterval != null)));

        await _s.SettleUntilAsync(async () => await OnVessel(db => db.PlannedJobs.CountAsync()) == 3000 && await OnVessel(db => db.JobCompletions.CountAsync()) == 3000, seconds: 600);
        Assert.Equal(3000, await OnVessel(db => db.PlannedJobs.CountAsync()));
        var sample = await OnShore(db => db.PlannedJobs.OrderBy(j => j.Code).Take(40).ToListAsync());
        foreach (var j in sample)
            Assert.Equal(j.NextDueDate, (await OnVessel(db => db.PlannedJobs.SingleAsync(x => x.Id == j.Id))).NextDueDate);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }
}
