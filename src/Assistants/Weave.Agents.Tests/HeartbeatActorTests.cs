using Weave.Agents.Heartbeat;

namespace Weave.Agents.Tests;

public sealed class HeartbeatActorTests
{
    private readonly HeartbeatSchedule _schedule = new();

    [Theory]
    [InlineData("*/5 * * * *", 5)]
    [InlineData("*/10 * * * *", 10)]
    [InlineData("*/30 * * * *", 30)]
    [InlineData("*/60 * * * *", 60)]
    [InlineData("*/1 * * * *", 1)]
    public void ParseCronMinutes_WithStepPattern_ReturnsInterval(string cron, int expected)
    {
        _schedule.ParseMinutes(cron).ShouldBe(expected);
    }

    [Theory]
    [InlineData("0 * * * *", 30)]
    [InlineData("15 * * * *", 30)]
    [InlineData("0 0 * * *", 30)]
    public void ParseCronMinutes_WithFixedMinute_FallsBackTo30(string cron, int expected)
    {
        _schedule.ParseMinutes(cron).ShouldBe(expected);
    }

    [Fact]
    public void ParseCronMinutes_WithEmptyString_FallsBackTo30()
    {
        _schedule.ParseMinutes("").ShouldBe(30);
    }

    [Fact]
    public void ParseCronMinutes_WithInvalidStep_FallsBackTo30()
    {
        _schedule.ParseMinutes("*/abc * * * *").ShouldBe(30);
    }

    [Fact]
    public void ParseCronMinutes_WithSingleAsterisk_FallsBackTo30()
    {
        _schedule.ParseMinutes("* * * * *").ShouldBe(30);
    }
}
