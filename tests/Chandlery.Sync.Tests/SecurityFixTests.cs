using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>A clock that moves only when told to.</summary>
public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public class LoginThrottleTests
{
    private static (LoginThrottle Throttle, FakeTimeProvider Time) Make()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        return (new LoginThrottle(time), time);
    }

    [Fact]
    public void AfterFiveWrongPasswords_TheNameIsRefused_EvenWithTheRightOne_UntilTheLockEnds()
    {
        var (t, time) = Make();
        for (var i = 0; i < 4; i++) t.RecordFailure("SF-C-001", "10.0.0.5");
        Assert.False(t.IsLocked("SF-C-001", "10.0.0.5"));
        t.RecordFailure("SF-C-001", "10.0.0.5");
        Assert.True(t.IsLocked("sf-c-001 ", "10.0.0.9"));                       // the name is locked wherever it is tried from, case and spaces aside
        Assert.False(t.IsLocked("SF-C-002", "10.0.0.9"));                       // other people are not affected

        time.Advance(LoginThrottle.LockFor + TimeSpan.FromSeconds(1));
        Assert.False(t.IsLocked("SF-C-001", "10.0.0.9"));
    }

    [Fact]
    public void FailuresOlderThanTheWindowDoNotAddUp_AndASuccessClearsTheCount()
    {
        var (t, time) = Make();
        for (var i = 0; i < 4; i++) t.RecordFailure("a@x.com", "");
        time.Advance(LoginThrottle.Window + TimeSpan.FromMinutes(1));
        t.RecordFailure("a@x.com", "");                                          // the count started again
        Assert.False(t.IsLocked("a@x.com", ""));

        for (var i = 0; i < 3; i++) t.RecordFailure("a@x.com", "");
        t.RecordSuccess("a@x.com");
        for (var i = 0; i < 4; i++) t.RecordFailure("a@x.com", "");
        Assert.False(t.IsLocked("a@x.com", ""));
    }

    [Fact]
    public void OneAddressTryingManyNames_IsRefusedToo()
    {
        var (t, _) = Make();
        for (var i = 0; i < LoginThrottle.MaxFailuresPerAddress; i++) t.RecordFailure("name" + i, "10.9.9.9");
        Assert.True(t.IsLocked("a-new-name", "10.9.9.9"));
        Assert.False(t.IsLocked("a-new-name", "10.1.1.1"));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/worklist/fleet?x=1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("https://evil.example/login", false)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("evil.example", false)]
    [InlineData("/ok\r\nSet-Cookie: x=1", false)]
    public void OnlyAPathOnThisSiteCanBeReturnedTo(string? url, bool local) => Assert.Equal(local, LoginThrottle.IsLocalPath(url));
}

public class CrewCryptoTests
{
    private static CrewCrypto Make() => new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void ARoundTripWorks_AndDamagedOrForeignValuesFailWithAClearMessage()
    {
        var crypto = Make();
        var stored = crypto.Encrypt("P1234567");
        Assert.Equal("P1234567", crypto.Decrypt(stored));

        foreach (var bad in new[] { "v1:", "v1:AAAA", "v1:%%%not base64%%%", stored[..^6], new CrewCrypto(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).Encrypt("P1234567") })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => crypto.Decrypt(bad));
            Assert.Contains("ID number", ex.Message);
        }
    }
}
