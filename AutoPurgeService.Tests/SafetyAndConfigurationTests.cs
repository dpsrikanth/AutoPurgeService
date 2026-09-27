using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutoPurgeService.Tests;

public class UnsafePathTests
{
    private static string Special(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

    public static TheoryData<string> RejectedPaths => new()
    {
        @"C:\",
        @"C:\\",
        @"C:",
        @"\\server\share",
        @"\\server\share\",
        @"relative\logs",
        Special(Environment.SpecialFolder.Windows),
        Special(Environment.SpecialFolder.System) + @"\",
        Special(Environment.SpecialFolder.ProgramFiles),
        Special(Environment.SpecialFolder.CommonApplicationData),
        Path.GetDirectoryName(Path.GetDirectoryName(Special(Environment.SpecialFolder.CommonDocuments)))!, // C:\Users
        Special(Environment.SpecialFolder.UserProfile),
    };

    public static TheoryData<string> AllowedPaths => new()
    {
        Path.Combine(Special(Environment.SpecialFolder.Windows), "Temp"),
        Path.Combine(Special(Environment.SpecialFolder.System), "LogFiles"),
        @"C:\inetpub\logs\LogFiles",
        @"D:\Logs",
        @"\\server\share\logs",
    };

    [Theory]
    [MemberData(nameof(RejectedPaths))]
    public void RejectsRootsSystemFoldersAndRelativePaths(string path)
        => Assert.NotNull(Worker.GetUnsafePathReason(path));

    [Theory]
    [MemberData(nameof(AllowedPaths))]
    public void AllowsOrdinaryFoldersIncludingSubfoldersOfSystemFolders(string path)
        => Assert.Null(Worker.GetUnsafePathReason(path));
}

public class ConfigurationReportTests
{
    [Fact]
    public void ReportsRulesTheBinderDropsAndUnrecognizedSettingNames()
    {
        var logger = Report(new()
        {
            ["PurgeSettings:Interval"] = "5",
            ["PurgeSettings:Rules:0:FolderPath"] = @"D:\Logs",
            ["PurgeSettings:Rules:0:AgeType"] = "Modified",
            ["PurgeSettings:Rules:1:FolderPath"] = @"D:\Temp",
            ["PurgeSettings:Rules:1:Recursve"] = "true",
        });

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error
            && e.Message.Contains("PurgeSettings:Rules:0") && e.Message.Contains("Modified") && e.Message.Contains("will not run"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("PurgeSettings:Rules:1") && e.Message.Contains("Recursve"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("under 'PurgeSettings'") && e.Message.Contains("Interval"));
    }

    [Fact]
    public void ValidConfigurationReportsNothing()
    {
        var logger = Report(new()
        {
            ["PurgeSettings:IntervalMinutes"] = "60",
            ["PurgeSettings:DailyRunTime"] = "02:00",
            ["PurgeSettings:WhatIf"] = "false",
            ["PurgeSettings:Rules:0:FolderPath"] = @"D:\Logs",
            ["PurgeSettings:Rules:0:Extensions:0"] = ".log",
            ["PurgeSettings:Rules:0:AgeInDays"] = "7",
            ["PurgeSettings:Rules:0:AgeType"] = "lastwritetime",
            ["PurgeSettings:Rules:0:Recursive"] = "true",
            ["PurgeSettings:Rules:0:DeleteReadOnly"] = "false",
            ["PurgeSettings:Rules:0:DeleteEmptyFolders"] = "true",
        });

        Assert.Empty(logger.Entries);
    }

    private static ListLogger<Worker> Report(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var logger = new ListLogger<Worker>();
        new Worker(logger, new TestOptionsMonitor(new PurgeSettings()), configuration).ReportConfigurationProblems();
        return logger;
    }
}
