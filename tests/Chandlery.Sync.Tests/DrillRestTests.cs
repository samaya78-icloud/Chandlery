using Chandlery.Models;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>How a drill's duration lands on hours of work: the pieces it makes and what it adds to a recorded day.</summary>
public class DrillRestRulesTests
{
    private static DateTime At(int d, int h, int m) => new(2026, 11, d, h, m, 0, DateTimeKind.Utc);

    [Fact]
    public void ADrillBecomesWorkWidenedToTheQuarterHour()
    {
        var piece = Assert.Single(DrillRestRules.Pieces(At(12, 10, 7), At(12, 10, 52)));
        Assert.Equal((At(12, 0, 0), new WorkSpan(10 * 60, 10 * 60 + 60)), (piece.Day, piece.Span));          // 10:07-10:52 is 10:00-11:00
    }

    [Fact]
    public void ADrillAcrossMidnightIsEnteredOnBothDays_AndOneEndingAtMidnightOnlyOnTheFirst()
    {
        var two = DrillRestRules.Pieces(At(12, 23, 30), At(13, 0, 30));
        Assert.Equal([(At(12, 0, 0), new WorkSpan(23 * 60 + 30, 1440)), (At(13, 0, 0), new WorkSpan(0, 30))], two);
        var one = Assert.Single(DrillRestRules.Pieces(At(12, 23, 0), At(13, 0, 0)));
        Assert.Equal(new WorkSpan(23 * 60, 1440), one.Span);
    }

    [Fact]
    public void ADrillThatEndsBeforeItStartsMakesNothing() => Assert.Empty(DrillRestRules.Pieces(At(12, 10, 0), At(12, 9, 0)));

    [Fact]
    public void ADayThatIsNotRecordedGetsNoOffer_AndWorkAlreadyCoveringTheDrillAddsNothing()
    {
        var span = new WorkSpan(10 * 60, 11 * 60);
        Assert.Null(DrillRestRules.Merge(null, span));                                                      // a drill does not invent a record
        Assert.Null(DrillRestRules.Merge("08:00-12:00", span));
        Assert.Null(DrillRestRules.Merge("10:00-11:00", span));                                             // exactly covered
    }

    [Fact]
    public void ADrillDuringRestIsAddedToTheWorkAndTouchingPeriodsJoin()
    {
        var span = new WorkSpan(10 * 60, 11 * 60);
        Assert.Equal("06:00-08:00,10:00-11:00", DrillRestRules.Merge("06:00-08:00", span));
        Assert.Equal("06:00-11:00", DrillRestRules.Merge("06:00-10:00", span));                             // touching: joined
        Assert.Equal("09:00-11:30", DrillRestRules.Merge("09:00-10:30", new WorkSpan(10 * 60, 11 * 60 + 30)));   // part covered: extended
        Assert.Equal("10:00-11:00", DrillRestRules.Merge("", span));                                        // recorded as a day of rest
    }
}
