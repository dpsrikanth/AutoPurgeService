using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutoPurgeService.Tests;

// Runs the whole worker loop, with the last-run record kept in a per-test temporary folder.
public sealed class SchedulingTests : IDisposable
{
    private readonly string _stateFolder = Directory.CreateTempSubdirectory("autopurge-state-").FullName;
    private readonly ListLogger<Worker> _logger = new();

    private string StateFile => Path.Combine(_stateFolder, "last-run.txt");

    public void Dispose() => Directory.Delete(_stateFolder, recursive: true);

    [Fact]
    public async Task FirstStartRunsImmediatelyAndRecordsTheRun()
    {
        var worker = CreateWorker(new PurgeSettings { DailyRunTime = LocalTimeFromNow(TimeSpan.FromHours(3)) });

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => _logger.Has(LogLevel.Information, "Next run at"));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, _logger.Count(LogLevel.Information, "Starting purge cycle"));
        Assert.True(DateTimeOffset.UtcNow - ReadLastRun() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RestartDoesNotRunAgainBeforeTheNextDailyRunTime()
    {
        WriteLastRun(DateTimeOffset.UtcNow.AddMinutes(-5));
        var worker = CreateWorker(new PurgeSettings { DailyRunTime = LocalTimeFromNow(TimeSpan.FromHours(3)) });

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => _logger.Has(LogLevel.Information, "Next run at"));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, _logger.Count(LogLevel.Information, "Starting purge cycle"));
    }

    [Fact]
    public async Task RestartCatchesUpARunMissedWhileStopped()
    {
        WriteLastRun(DateTimeOffset.UtcNow.AddDays(-2));
        var worker = CreateWorker(new PurgeSettings { DailyRunTime = LocalTimeFromNow(TimeSpan.FromHours(3)) });

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => _logger.Has(LogLevel.Information, "Next run at"));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, _logger.Count(LogLevel.Information, "Starting purge cycle"));
    }

    [Fact]
    public async Task ChangingDailyRunTimeReschedulesWithoutARestart()
    {
        WriteLastRun(DateTimeOffset.UtcNow.AddHours(-2));
        var monitor = new TestOptionsMonitor(new PurgeSettings { DailyRunTime = LocalTimeFromNow(TimeSpan.FromHours(3)) });
        var worker = CreateWorker(monitor);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => _logger.Has(LogLevel.Information, "Next run at"));
        Assert.Equal(0, _logger.Count(LogLevel.Information, "Starting purge cycle"));

        // A run time that has passed since the last run makes a run due now.
        monitor.Change(new PurgeSettings { DailyRunTime = LocalTimeFromNow(TimeSpan.FromHours(-1)) });
        await WaitUntil(() => _logger.Count(LogLevel.Information, "Starting purge cycle") == 1);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(_logger.Has(LogLevel.Information, "Schedule changed. A run is due now."));
    }

    [Fact]
    public async Task IntervalChangeTakesEffectWithoutWaitingOutTheOldInterval()
    {
        var monitor = new TestOptionsMonitor(new PurgeSettings { IntervalMinutes = 60 });
        var worker = CreateWorker(monitor);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => _logger.Has(LogLevel.Information, "(in 60 minutes)"));

        monitor.Change(new PurgeSettings { IntervalMinutes = 0.001 });
        await WaitUntil(() => _logger.Count(LogLevel.Information, "Starting purge cycle") >= 2);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopsPromptlyWhileWaitingForTheNextRun()
    {
        var worker = CreateWorker(new PurgeSettings { IntervalMinutes = 60 });

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => _logger.Has(LogLevel.Information, "(in 60 minutes)"));

        var stop = worker.StopAsync(CancellationToken.None);
        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.True(_logger.Has(LogLevel.Information, "Auto Purge Service is stopping."));
    }

    private Worker CreateWorker(PurgeSettings settings) => CreateWorker(new TestOptionsMonitor(settings));

    private Worker CreateWorker(TestOptionsMonitor monitor)
        => new(_logger, monitor, new ConfigurationBuilder().Build()) { StateFilePath = StateFile };

    private void WriteLastRun(DateTimeOffset lastRunUtc)
        => File.WriteAllText(StateFile, lastRunUtc.ToString("O", CultureInfo.InvariantCulture));

    private DateTimeOffset ReadLastRun()
        => DateTimeOffset.ParseExact(File.ReadAllText(StateFile), "O", CultureInfo.InvariantCulture);

    private static TimeOnly LocalTimeFromNow(TimeSpan offset) => TimeOnly.FromDateTime(DateTime.Now + offset);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the worker.");
            await Task.Delay(20);
        }
    }
}

public class NextDailyRunTests
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

    [Theory]
    [InlineData("2026-09-27T01:00:00Z", "2026-09-27T02:00:00Z")] // later today
    [InlineData("2026-09-27T02:00:00Z", "2026-09-28T02:00:00Z")] // exactly at the run time: that run is done
    [InlineData("2026-09-27T03:00:00Z", "2026-09-28T02:00:00Z")] // already past today
    public void FindsTheNextOccurrence(string after, string expected)
        => Assert.Equal(DateTimeOffset.Parse(expected), Worker.NextDailyRun(DateTimeOffset.Parse(after), new TimeOnly(2, 0), TimeZoneInfo.Utc));

    [Fact]
    public void UsesTheLocalClockOfTheZone()
    {
        // 06:00 IST on 27 Sep is past 02:00 IST, so the next run is 02:00 IST on 28 Sep, which is 20:30 UTC on 27 Sep.
        var next = Worker.NextDailyRun(DateTimeOffset.Parse("2026-09-27T00:30:00Z"), new TimeOnly(2, 0), India);

        Assert.Equal(DateTimeOffset.Parse("2026-09-27T20:30:00Z"), next);
    }

    [Fact]
    public void ARunTimeSkippedBySpringForwardRunsAnHourLater()
    {
        // US clocks jump from 02:00 to 03:00 on 8 Mar 2026, so 02:30 doesn't exist; 03:30 PDT is 10:30 UTC.
        var next = Worker.NextDailyRun(DateTimeOffset.Parse("2026-03-08T08:00:00Z"), new TimeOnly(2, 30), Pacific);

        Assert.Equal(DateTimeOffset.Parse("2026-03-08T10:30:00Z"), next);
    }

    [Fact]
    public void ARunTimeRepeatedByFallBackRunsOnce()
    {
        // US clocks go from 02:00 back to 01:00 on 1 Nov 2026, so 01:30 happens twice.
        var first = Worker.NextDailyRun(DateTimeOffset.Parse("2026-11-01T07:00:00Z"), new TimeOnly(1, 30), Pacific);
        var second = Worker.NextDailyRun(first, new TimeOnly(1, 30), Pacific);

        Assert.Equal(new DateTime(2026, 11, 1), TimeZoneInfo.ConvertTime(first, Pacific).Date);
        Assert.Equal(new DateTime(2026, 11, 2), TimeZoneInfo.ConvertTime(second, Pacific).Date);
    }
}
