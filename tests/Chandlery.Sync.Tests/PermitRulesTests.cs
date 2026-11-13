using Chandlery.Models;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>Permit to work, the pure rules: state and validity from the events and the clock, gas, the issue check, the entry log, roles and conflicts.</summary>
public class PermitRulesTests
{
    private static DateTime At(int h, int m = 0) => new(2026, 11, 13, h, m, 0, DateTimeKind.Utc);

    private static readonly PermitTerms Entry = new(8, true, 30, 20.9m, 22.0m, 1m, 1m, 25m, true, true, true, true, false);
    private static readonly PermitTerms Cold = new(8, false, 30, 20.9m, 22.0m, 1m, 1m, 25m, false, false, false, false, false);

    private static PermitEvent Ev(PermitEventKind kind, DateTime at, DateTime? until = null) => new() { Kind = kind, At = at, Until = until };

    private static PermitGasReading Reading(DateTime at, decimal o2 = 20.9m, decimal lel = 0m, bool ok = true) => new() { At = at, Oxygen = o2, FlammablePercentLel = lel, WithinLimits = ok };

    private static PermitAnswer Answer(int pos, PermitAnswerKind a, bool mandatory = true, bool noteRequired = false, string note = "") =>
        new() { Position = pos, Text = "Item " + pos, Answer = a, Mandatory = mandatory, NoteRequired = noteRequired, Note = note };

    // ---- state ----

    [Fact]
    public void AnEmptyPermitIsRequested_AndTheEventsMoveItOnInOrder()
    {
        var until = At(16);
        Assert.Equal(PermitState.Requested, PermitRules.State([], until, At(8)));
        var issued = Ev(PermitEventKind.Issued, At(8));
        Assert.Equal(PermitState.Issued, PermitRules.State([issued], until, At(9)));
        var started = Ev(PermitEventKind.Started, At(9));
        Assert.Equal(PermitState.Active, PermitRules.State([issued, started], until, At(10)));
        var suspended = Ev(PermitEventKind.Suspended, At(10));
        Assert.Equal(PermitState.Suspended, PermitRules.State([issued, started, suspended], until, At(11)));
        Assert.Equal(PermitState.Active, PermitRules.State([issued, started, suspended, Ev(PermitEventKind.Resumed, At(11))], until, At(12)));
    }

    [Fact]
    public void TheOrderOfTheListDoesNotMatter_OnlyTheTimes()
        => Assert.Equal(PermitState.Active, PermitRules.State([Ev(PermitEventKind.Started, At(9)), Ev(PermitEventKind.Issued, At(8))], At(16), At(10)));

    [Fact]
    public void ClosingEndsItForGood_AndWithdrawnAndCancelledToo()
    {
        var open = new[] { Ev(PermitEventKind.Issued, At(8)), Ev(PermitEventKind.Started, At(9)) };
        Assert.Equal(PermitState.Completed, PermitRules.State([.. open, Ev(PermitEventKind.Completed, At(12))], At(16), At(20)));   // not "expired" afterwards
        Assert.Equal(PermitState.Cancelled, PermitRules.State([.. open, Ev(PermitEventKind.Cancelled, At(12))], At(16), At(20)));
        Assert.Equal(PermitState.Withdrawn, PermitRules.State([Ev(PermitEventKind.Withdrawn, At(8))], At(16), At(20)));
    }

    [Fact]
    public void PastItsWindowAnOpenPermitIsExpiredNotClosed_ButARequestNeverIssuedIsNot()
    {
        var issued = Ev(PermitEventKind.Issued, At(8));
        Assert.Equal(PermitState.Active, PermitRules.State([issued, Ev(PermitEventKind.Started, At(9))], At(16), At(16)));                // the last minute is still inside
        Assert.Equal(PermitState.Expired, PermitRules.State([issued, Ev(PermitEventKind.Started, At(9))], At(16), At(16, 1)));
        Assert.Equal(PermitState.Expired, PermitRules.State([issued, Ev(PermitEventKind.Started, At(9)), Ev(PermitEventKind.Suspended, At(10))], At(16), At(17)));
        Assert.Equal(PermitState.Requested, PermitRules.State([], At(16), At(20)));
        Assert.True(PermitRules.IsOpen(PermitState.Expired));
        Assert.False(PermitRules.IsOpen(PermitState.Completed));
    }

    [Fact]
    public void AnExtensionMovesTheEnd_ByTheLatestOne()
    {
        var events = new[] { Ev(PermitEventKind.Issued, At(8)), Ev(PermitEventKind.Started, At(9)), Ev(PermitEventKind.Extended, At(15), At(20)), Ev(PermitEventKind.Extended, At(19), At(23)) };
        Assert.Equal(At(23), PermitRules.Until(At(16), events));
        Assert.Equal(PermitState.Active, PermitRules.State(events, At(16), At(22)));
        Assert.Equal(PermitState.Expired, PermitRules.State(events, At(16), At(23, 1)));
        Assert.Equal(At(16), PermitRules.Until(At(16), []));
    }

    // ---- gas ----

    [Fact]
    public void AReadingIsCheckedAgainstEachLimit_AndExactlyOnTheLimitIsAllowed()
    {
        Assert.Empty(PermitRules.GasFailures(Entry, 20.9m, 0m, null, null));
        Assert.Empty(PermitRules.GasFailures(Entry, 22.0m, 1m, 1m, 25m));                                   // on every limit
        Assert.Contains("Oxygen", Assert.Single(PermitRules.GasFailures(Entry, 20.8m, 0m, null, null)));
        Assert.Contains("Oxygen", Assert.Single(PermitRules.GasFailures(Entry, 22.1m, 0m, null, null)));
        Assert.Contains("Flammable", Assert.Single(PermitRules.GasFailures(Entry, 20.9m, 1.1m, null, null)));
        Assert.Contains("Hydrogen sulphide", Assert.Single(PermitRules.GasFailures(Entry, 20.9m, 0m, 1.1m, null)));
        Assert.Contains("Carbon monoxide", Assert.Single(PermitRules.GasFailures(Entry, 20.9m, 0m, null, 26m)));
        Assert.Equal(2, PermitRules.GasFailures(Entry, null, null, null, null).Count);                     // oxygen and flammable must be recorded
    }

    [Fact]
    public void TheGasStatusFollowsTheLastReadingAndItsInterval()
    {
        Assert.Equal(PermitGasStatus.NotRequired, PermitRules.GasStatus(Cold, [], At(9)));
        Assert.Equal(PermitGasStatus.Missing, PermitRules.GasStatus(Entry, [], At(9)));
        var good = Reading(At(9));
        Assert.Equal(PermitGasStatus.Ok, PermitRules.GasStatus(Entry, [good], At(9, 30)));                 // exactly at the interval: still in date
        Assert.Equal(PermitGasStatus.Overdue, PermitRules.GasStatus(Entry, [good], At(9, 31)));
        Assert.Equal(At(9, 30), PermitRules.NextGasDue(Entry, good));
        Assert.Null(PermitRules.NextGasDue(Cold, good));
        Assert.Null(PermitRules.NextGasDue(Entry, null));
        Assert.Equal(PermitGasStatus.OutOfLimits, PermitRules.GasStatus(Entry, [good, Reading(At(9, 20), lel: 5m, ok: false)], At(9, 25)));
        Assert.Equal(PermitGasStatus.Ok, PermitRules.GasStatus(Entry, [Reading(At(9, 20), lel: 5m, ok: false), Reading(At(9, 25))], At(9, 30)));   // a clean reading afterwards clears it
    }

    // ---- validity ----

    [Fact]
    public void APermitIsValidOnlyWhileActiveInsideItsWindowWithGasInDate()
    {
        var events = new[] { Ev(PermitEventKind.Issued, At(8)), Ev(PermitEventKind.Started, At(9)) };
        var reading = new[] { Reading(At(9)) };
        Assert.True(PermitRules.Validity(Entry, events, reading, At(9), At(16), At(9, 20)).Valid);
        Assert.Equal("Gas reading overdue", PermitRules.Validity(Entry, events, reading, At(9), At(16), At(10)).Reason);
        Assert.Equal("No gas reading", PermitRules.Validity(Entry, events, [], At(9), At(16), At(9, 5)).Reason);
        Assert.Equal("Last gas reading outside limits", PermitRules.Validity(Entry, events, [Reading(At(9), lel: 9m, ok: false)], At(9), At(16), At(9, 5)).Reason);
        Assert.Equal("Expired and not closed", PermitRules.Validity(Cold, events, [], At(9), At(16), At(17)).Reason);
        Assert.Equal("Not issued yet", PermitRules.Validity(Cold, [], [], At(9), At(16), At(9)).Reason);
        Assert.Equal("Suspended", PermitRules.Validity(Cold, [.. events, Ev(PermitEventKind.Suspended, At(10))], [], At(9), At(16), At(11)).Reason);
        Assert.True(PermitRules.Validity(Cold, events, [], At(9), At(16), At(12)).Valid);                  // no gas test needed, none asked
        Assert.StartsWith("Not valid until", PermitRules.Validity(Cold, events, [], At(10), At(16), At(9, 30)).Reason);   // active before its window opens
    }

    // ---- issuing ----

    private static PermitIssueFacts Facts(PermitTerms? terms = null, IReadOnlyList<PermitAnswer>? answers = null, IReadOnlyList<PermitGasReading>? readings = null,
        IReadOnlyList<PermitEvent>? events = null, Guid? requester = null, Guid? issuer = null, bool mayIssue = true, Guid? attendant = null, IReadOnlyList<Guid?>? persons = null,
        bool riskLinked = true, bool riskSigned = true, DateTime? from = null, DateTime? until = null)
        => new(terms ?? Cold, answers ?? [], readings ?? [], events ?? [], requester ?? Guid.NewGuid(), issuer ?? Guid.NewGuid(), mayIssue, attendant, persons ?? [],
            riskLinked, riskSigned, from ?? At(8), until ?? At(14), At(8));

    [Fact]
    public void AValidPermitHasNothingMissing()
        => Assert.Empty(PermitRules.IssueCheck(Facts(answers: [Answer(1, PermitAnswerKind.Yes), Answer(2, PermitAnswerKind.NotApplicable)])));

    [Fact]
    public void AnOptionalItemThatWantsANoteBlocksNothingWhileItIsUnanswered_AndMissingIdsAreNotTheSamePerson()
    {
        Assert.Empty(PermitRules.IssueCheck(Facts(answers: [Answer(1, PermitAnswerKind.Unanswered, mandatory: false, noteRequired: true)])));
        Assert.Equal(["Needs a note: Item 1"], PermitRules.IssueCheck(Facts(answers: [Answer(1, PermitAnswerKind.Yes, mandatory: false, noteRequired: true)])));
        // Nobody named as requester or issuer is not "the same person".
        var none = new PermitIssueFacts(Cold, [], [], [], null, null, true, null, [], true, true, At(8), At(14), At(8));
        Assert.Empty(PermitRules.IssueCheck(none));
    }

    [Fact]
    public void EachMandatoryItemMustBeAnswered_NotNo_AndWithItsNoteWhereOneIsAsked()
    {
        var missing = PermitRules.IssueCheck(Facts(answers:
        [
            Answer(1, PermitAnswerKind.Unanswered), Answer(2, PermitAnswerKind.No), Answer(3, PermitAnswerKind.Yes, noteRequired: true),
            Answer(4, PermitAnswerKind.Unanswered, mandatory: false), Answer(5, PermitAnswerKind.No, mandatory: false), Answer(6, PermitAnswerKind.Yes, noteRequired: true, note: "Valve 4 shut"),
        ]));
        Assert.Equal(["Not answered: Item 1", "Answered no: Item 2", "Needs a note: Item 3"], missing);          // optional items never block
    }

    [Fact]
    public void TheIssuerMustBeAuthorised_AndNotTheRequesterUnlessTheTypeAllowsIt()
    {
        var same = Guid.NewGuid();
        Assert.Contains("not authorised", Assert.Single(PermitRules.IssueCheck(Facts(mayIssue: false))));
        Assert.Contains("cannot be the person who requested", Assert.Single(PermitRules.IssueCheck(Facts(requester: same, issuer: same))));
        Assert.Empty(PermitRules.IssueCheck(Facts(Cold with { SameRequesterAndIssuer = true }, requester: same, issuer: same)));
    }

    [Fact]
    public void AnAttendantIsNeededWhereTheTypeSaysSo_AndIsNotOneOfThoseInside()
    {
        var a = Guid.NewGuid();
        var gas = new[] { Reading(At(8)) };
        var terms = Entry with { RiskAssessmentRequired = false, MasterApprovalRequired = false };
        Assert.Equal(["An attendant is not named"], PermitRules.IssueCheck(Facts(terms, readings: gas)));
        Assert.Equal(["The attendant cannot be one of the people doing the work"], PermitRules.IssueCheck(Facts(terms, readings: gas, attendant: a, persons: [Guid.NewGuid(), a])));
        Assert.Empty(PermitRules.IssueCheck(Facts(terms, readings: gas, attendant: a, persons: [Guid.NewGuid(), null])));
    }

    [Fact]
    public void TheGasReadingMustBeThereWithinLimitsAndInDate()
    {
        var terms = Entry with { AttendantRequired = false, RiskAssessmentRequired = false, MasterApprovalRequired = false };
        Assert.Equal(["A gas reading is needed"], PermitRules.IssueCheck(Facts(terms)));
        Assert.Equal(["The last gas reading is outside the limits"], PermitRules.IssueCheck(Facts(terms, readings: [Reading(At(8), lel: 4m, ok: false)])));
        Assert.Equal(["The last gas reading is out of date"], PermitRules.IssueCheck(Facts(terms, readings: [Reading(At(7))])));      // 08:00 now, 07:00 reading, 30 minute interval
        Assert.Empty(PermitRules.IssueCheck(Facts(terms, readings: [Reading(At(7, 30))])));
    }

    [Fact]
    public void TheRiskAssessmentAndTheMastersApprovalAreRequiredWhereTheTypeSaysSo()
    {
        var terms = Cold with { RiskAssessmentRequired = true, MasterApprovalRequired = true };
        Assert.Equal(["A risk assessment is not linked", "The Master has not approved it"], PermitRules.IssueCheck(Facts(terms, riskLinked: false)));
        Assert.Equal(["The linked risk assessment is not signed", "The Master has not approved it"], PermitRules.IssueCheck(Facts(terms, riskSigned: false)));
        Assert.Empty(PermitRules.IssueCheck(Facts(terms, events: [Ev(PermitEventKind.MasterApproved, At(7))])));
    }

    [Fact]
    public void TheWindowMustRunForwardAndNotPastTheMaximum()
    {
        Assert.Equal(["The permit ends before it starts"], PermitRules.IssueCheck(Facts(from: At(10), until: At(10))));
        Assert.Equal(["The permit is longer than the 8 hours this kind allows"], PermitRules.IssueCheck(Facts(from: At(8), until: At(16, 1))));
        Assert.Empty(PermitRules.IssueCheck(Facts(from: At(8), until: At(16))));                            // exactly the maximum
    }

    [Fact]
    public void AnExtensionNeedsAnEarlierReadingNoOlderThanTheIntervalAndALaterEnd()
    {
        var now = At(15);
        Assert.Empty(PermitRules.ExtensionCheck(Entry, [Reading(At(14, 45))], At(16), At(20), now));
        Assert.Equal(["A new gas reading within limits is needed"], PermitRules.ExtensionCheck(Entry, [], At(16), At(20), now));
        Assert.Equal(["A new gas reading within limits is needed"], PermitRules.ExtensionCheck(Entry, [Reading(At(14, 50), lel: 3m, ok: false)], At(16), At(20), now));
        Assert.Equal(["The last gas reading is out of date: take a new one"], PermitRules.ExtensionCheck(Entry, [Reading(At(14, 0))], At(16), At(20), now));
        Assert.Empty(PermitRules.ExtensionCheck(Entry, [Reading(At(14, 30))], At(16), At(20), now));                       // exactly the interval old: still in date
        Assert.Equal(["The last gas reading is out of date: take a new one"], PermitRules.ExtensionCheck(Entry, [Reading(At(14, 29))], At(16), At(20), now));
        Assert.Equal(["The new end must be after the current end"], PermitRules.ExtensionCheck(Cold, [], At(16), At(16), now));
        Assert.Equal(["A period cannot run longer than 8 hours"], PermitRules.ExtensionCheck(Cold, [], At(16), At(23, 1), now));
        Assert.Empty(PermitRules.ExtensionCheck(Cold, [], At(16), At(23), now));                            // no gas test, so no reading asked
    }

    // ---- the entry log ----

    [Fact]
    public void WhoIsInsideIsTheEntriesWithNoExit_AndClosingIsRefusedWhileAnyoneIs()
    {
        var entries = new[]
        {
            new PermitEntry { PersonName = "Ana", In = At(9), Out = At(10) },
            new PermitEntry { PersonName = "Ben", In = At(9, 30) },
            new PermitEntry { PersonName = "Cy", In = At(9, 10) },
        };
        Assert.Equal(["Cy", "Ben"], PermitRules.Inside(entries).Select(e => e.PersonName));
        Assert.Equal(["Cy is still inside", "Ben is still inside"], PermitRules.CloseBlockers(entries));
        entries[1].Out = At(11);
        entries[2].Out = At(11);
        Assert.Empty(PermitRules.CloseBlockers(entries));
        Assert.Empty(PermitRules.CloseBlockers([]));
    }

    // ---- roles ----

    [Theory]
    [InlineData("Chief Officer", true)]
    [InlineData("2nd Engineer", true)]
    [InlineData("Master", true)]
    [InlineData("Electro-Technical Officer", true)]
    [InlineData("Able Seaman", false)]
    [InlineData("Oiler", false)]
    [InlineData("", false)]
    public void OfficersAreRecognisedByRank(string rank, bool officer) => Assert.Equal(officer, PermitRules.IsOfficer(rank));

    [Fact]
    public void TheRoleNeededToRequestIsMetByRank_AndOnlyTheMasterMeetsTheMastersRole()
    {
        Assert.True(PermitRules.Meets(PermitRole.AnyCrew, false, "Cook"));
        Assert.False(PermitRules.Meets(PermitRole.Officer, false, "Cook"));
        Assert.True(PermitRules.Meets(PermitRole.Officer, false, "Third Officer"));
        Assert.True(PermitRules.Meets(PermitRole.Officer, true, ""));
        Assert.False(PermitRules.Meets(PermitRole.Master, false, "Chief Officer"));
        Assert.True(PermitRules.Meets(PermitRole.Master, true, "Master"));
    }

    // ---- simultaneous operations ----

    [Fact]
    public void ConflictingPermitsAreFound_EitherWayRound_AndByLocationWhereThePairSaysSo()
    {
        Guid hot = Guid.NewGuid(), space = Guid.NewGuid(), aloft = Guid.NewGuid();
        var active = new[] { new PermitOnBoard(Guid.NewGuid(), space, "Cargo hold 2"), new PermitOnBoard(Guid.NewGuid(), aloft, "Mast") };
        var pairs = new[]
        {
            new PermitConflictPair { PermitTypeAId = hot, PermitTypeBId = space, SameLocationOnly = false, Message = "Hot work with an open space" },
            new PermitConflictPair { PermitTypeAId = hot, PermitTypeBId = aloft, SameLocationOnly = true, Message = "Hot work under work aloft" },
        };
        var found = PermitRules.Conflicts(hot, "Engine room", active, pairs);
        Assert.Equal(["Hot work with an open space"], found.Select(c => c.Message));                        // aloft is somewhere else
        Assert.Equal(["Hot work with an open space", "Hot work under work aloft"], PermitRules.Conflicts(hot, " mast ", active, pairs).Select(c => c.Message));
        Assert.Single(PermitRules.Conflicts(space, "Anywhere", [new PermitOnBoard(Guid.NewGuid(), hot, "Deck")], pairs));      // the pair works from either side
        Assert.Empty(PermitRules.Conflicts(aloft, "Mast", [new PermitOnBoard(Guid.NewGuid(), aloft, "Mast")], pairs));          // two of one kind do not conflict unless paired
    }

    [Fact]
    public void TheTermsAreCopiedFromTheTypeAndComeBackUnchangedFromJson()
    {
        var type = new PermitType { MaxValidityHours = 6, GasTestRequired = true, GasRepeatMinutes = 20, OxygenMin = 20.5m, OxygenMax = 21.5m, FlammableMaxPercentLel = 2m, H2sMaxPpm = 3m, CoMaxPpm = 30m,
            AttendantRequired = true, EntryLogRequired = true, RiskAssessmentRequired = true, MasterApprovalRequired = true, SameRequesterAndIssuer = true };
        var terms = PermitRules.TermsOf(type);
        Assert.Equal(terms, PermitRules.ReadTerms(PermitRules.Serialize(terms)));
        Assert.Equal((6, 20, 20.5m, 30m), (terms.MaxValidityHours, terms.GasRepeatMinutes, terms.OxygenMin, terms.CoMaxPpm));
        Assert.True(terms.SameRequesterAndIssuer && terms.MasterApprovalRequired && terms.EntryLogRequired);
    }
}
