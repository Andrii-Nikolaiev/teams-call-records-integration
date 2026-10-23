using TeamsCallRecords.Infrastructure.Config;

namespace TeamsCallRecords.Infrastructure.Tests;

public class ProcessingOptionsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(5, 60)]
    [InlineData(50, 60)]
    public void RetryDelayFor_FollowsSchedule(int attempt, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), new ProcessingOptions().RetryDelayFor(attempt));

    [Fact]
    public void RetryDelayFor_ZeroAttempt_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessingOptions().RetryDelayFor(0));

    [Fact]
    public void RetrySchedule_AllowsSpaces() =>
        Assert.Equal(new[] { 1, 5, 15 }, new ProcessingOptions { RetrySchedule = " 1, 5 ,15 " }.RetryScheduleMinutes());

    [Theory]
    [InlineData("")]
    [InlineData("1,,5")]
    [InlineData("0,5")]
    [InlineData("-1")]
    [InlineData("a")]
    public void RetrySchedule_Invalid_FailsValidation(string schedule) =>
        Assert.NotEmpty(new ProcessingOptions { RetrySchedule = schedule }.Validate());

    [Fact]
    public void Defaults_AreValid() => Assert.Empty(new ProcessingOptions().Validate());
}
