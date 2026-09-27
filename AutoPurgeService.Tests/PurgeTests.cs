using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoPurgeService.Tests;

// Runs ExecutePurge against real files in a per-test temporary folder.
public sealed class PurgeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("autopurge-tests-").FullName;
    private readonly ListLogger<Worker> _logger = new();
    private readonly List<string> _junctions = new();

    public void Dispose()
    {
        // A recursive delete removes junctions via DeleteVolumeMountPoint, which needs admin rights;
        // a non-recursive delete removes just the link.
        foreach (var junction in _junctions)
        {
            Directory.Delete(junction);
        }
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DeletesOnlyOldFilesWithAMatchingExtension()
    {
        var oldLog = CreateFile(@"logs\old.log");
        var newLog = CreateFile(@"logs\new.log", ageInDays: 0);
        var oldTxt = CreateFile(@"logs\old.txt");

        Purge(Rule("logs", ".log"));

        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(newLog));
        Assert.True(File.Exists(oldTxt));
    }

    [Theory]
    [InlineData("log")]
    [InlineData("*.log")]
    [InlineData(".LOG")]
    [InlineData(" .log ")]
    public void ExtensionsAreNormalized(string extension)
    {
        var oldLog = CreateFile(@"logs\old.log");

        Purge(Rule("logs", extension));

        Assert.False(File.Exists(oldLog));
    }

    [Fact]
    public void RuleWithoutExtensionsIsSkipped()
    {
        var file = CreateFile(@"logs\old.txt");

        Purge(Rule("logs"));

        Assert.True(File.Exists(file));
        Assert.True(_logger.Has(LogLevel.Warning, "has no Extensions"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.*")]
    [InlineData(" * ")]
    public void ExplicitWildcardMatchesEveryFile(string wildcard)
    {
        var txt = CreateFile(@"logs\old.txt");
        var noExtension = CreateFile(@"logs\old");

        Purge(Rule("logs", wildcard));

        Assert.False(File.Exists(txt));
        Assert.False(File.Exists(noExtension));
    }

    [Fact]
    public void NegativeAgeIsSkipped()
    {
        var file = CreateFile(@"logs\new.log", ageInDays: 0);
        var rule = Rule("logs", ".log");
        rule.AgeInDays = -1;

        Purge(rule);

        Assert.True(File.Exists(file));
        Assert.True(_logger.Has(LogLevel.Warning, "must be 0 or greater"));
    }

    [Fact]
    public void AgeUsesTheConfiguredTimestamp()
    {
        // Old last-write time, but created just now.
        var file = CreateFile(@"logs\old.log");
        var rule = Rule("logs", ".log");
        rule.AgeType = AgeType.CreationTime;

        Purge(rule);

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void WhatIfDeletesNothing()
    {
        var file = CreateFile(@"logs\old.log");

        Purge(Rule("logs", ".log"), whatIf: true);

        Assert.True(File.Exists(file));
        Assert.True(_logger.Has(LogLevel.Information, "WhatIf: would delete"));
    }

    [Fact]
    public void NonRecursiveRuleLeavesSubfoldersAlone()
    {
        var nested = CreateFile(@"logs\sub\old.log");

        Purge(Rule("logs", ".log"));

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void RecursiveRuleDeletesInSubfolders()
    {
        var nested = CreateFile(@"logs\sub\deeper\old.log");
        var rule = Rule("logs", ".log");
        rule.Recursive = true;

        Purge(rule);

        Assert.False(File.Exists(nested));
    }

    [Fact]
    public void RecursiveRuleDoesNotFollowJunctions()
    {
        var outside = CreateFile(@"outside\keep.log");
        CreateFile(@"purge\old.log");
        CreateJunction(Path.Combine(_root, @"purge\link"), Path.Combine(_root, "outside"));
        var rule = Rule("purge", "*");
        rule.Recursive = true;

        Purge(rule);

        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(_root, @"purge\old.log")));
    }

    [Fact]
    public void ReadOnlyFilesAreSkippedByDefaultWithoutErrors()
    {
        var file = CreateFile(@"logs\old.log", readOnly: true);

        Purge(Rule("logs", ".log"));

        Assert.True(File.Exists(file));
        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.True(_logger.Has(LogLevel.Information, "skipped 1 read-only files"));
    }

    [Fact]
    public void ReadOnlyFilesAreDeletedWhenDeleteReadOnlyIsSet()
    {
        var file = CreateFile(@"logs\old.log", readOnly: true);
        var rule = Rule("logs", ".log");
        rule.DeleteReadOnly = true;

        Purge(rule);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void EmptyFoldersAreKeptByDefault()
    {
        CreateFile(@"logs\a\old.log");
        SetFolderAge(@"logs\a", 10);
        var rule = Rule("logs", ".log");
        rule.Recursive = true;

        Purge(rule);

        Assert.True(Directory.Exists(Path.Combine(_root, @"logs\a")));
    }

    [Fact]
    public void DeleteEmptyFoldersRemovesOldEmptySubfoldersButNotNewOnesOrTheRoot()
    {
        CreateFile(@"logs\a\b\old.log");
        SetFolderAge(@"logs\a", 10);
        SetFolderAge(@"logs\a\b", 10);
        Directory.CreateDirectory(Path.Combine(_root, @"logs\fresh"));
        CreateFile(@"logs\kept\new.log", ageInDays: 0);
        SetFolderAge(@"logs\kept", 10);
        var rule = Rule("logs", ".log");
        rule.Recursive = true;
        rule.DeleteEmptyFolders = true;

        Purge(rule);

        Assert.False(Directory.Exists(Path.Combine(_root, @"logs\a")));
        Assert.True(Directory.Exists(Path.Combine(_root, @"logs\fresh")));
        Assert.True(Directory.Exists(Path.Combine(_root, @"logs\kept")));
        Assert.True(Directory.Exists(Path.Combine(_root, "logs")));
    }

    [Fact]
    public void CanceledPurgeStopsWithoutDeleting()
    {
        var file = CreateFile(@"logs\old.log");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Purge(Rule("logs", ".log"), cancellationToken: cts.Token));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void StoppingMidPurgeStopsBeforeTheNextFile()
    {
        for (int i = 0; i < 10; i++)
        {
            CreateFile($@"logs\old{i}.log");
        }
        using var cts = new CancellationTokenSource();
        _logger.OnLog = message => { if (message.StartsWith("Deleting file")) cts.Cancel(); };

        Assert.Throws<OperationCanceledException>(() => Purge(Rule("logs", ".log"), cancellationToken: cts.Token));

        // The file being deleted when the stop arrived is finished; the rest are untouched.
        Assert.Equal(9, Directory.GetFiles(Path.Combine(_root, "logs")).Length);
    }

    [Fact]
    public void OnlyTheFirstDeletionsPerRuleAreLoggedAtInformation()
    {
        const int extra = 5;
        for (int i = 0; i < Worker.MaxLoggedDeletionsPerRule + extra; i++)
        {
            CreateFile($@"logs\old{i}.log");
        }

        Purge(Rule("logs", ".log"));

        Assert.Equal(Worker.MaxLoggedDeletionsPerRule, _logger.Count(LogLevel.Information, "Deleting file"));
        Assert.Equal(extra, _logger.Count(LogLevel.Debug, "Deleting file"));
        Assert.True(_logger.Has(LogLevel.Information, $"the other {extra} were logged at Debug level"));
    }

    [Fact]
    public void RunOncePurgesWithoutRecordingALastRun()
    {
        var file = CreateFile(@"logs\old.log");
        var stateFile = Path.Combine(_root, "last-run.txt");
        var settings = new PurgeSettings { Rules = { Rule("logs", ".log") } };
        var worker = new Worker(_logger, new TestOptionsMonitor(settings), new ConfigurationBuilder().Build()) { StateFilePath = stateFile };

        Assert.True(worker.RunOnce(CancellationToken.None));

        Assert.False(File.Exists(file));
        Assert.False(File.Exists(stateFile));
    }

    [Fact]
    public void RunOnceReportsFailureWhenSettingsCannotBeRead()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PurgeSettings:IntervalMinutes"] = "sixty" })
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .Configure<PurgeSettings>(configuration.GetSection(PurgeSettings.SectionName))
            .BuildServiceProvider();
        var worker = new Worker(_logger, services.GetRequiredService<IOptionsMonitor<PurgeSettings>>(), configuration);

        Assert.False(worker.RunOnce(CancellationToken.None));
        Assert.True(_logger.Has(LogLevel.Error, "The single purge failed."));
    }

    private void Purge(PurgeRule rule, bool whatIf = false, CancellationToken cancellationToken = default)
    {
        var worker = new Worker(_logger, new TestOptionsMonitor(new PurgeSettings()), new ConfigurationBuilder().Build());
        worker.ExecutePurge(new PurgeSettings { WhatIf = whatIf, Rules = { rule } }, cancellationToken);
    }

    private PurgeRule Rule(string relativeFolder, params string[] extensions) => new()
    {
        FolderPath = Path.Combine(_root, relativeFolder),
        Extensions = extensions.ToList(),
        AgeInDays = 1
    };

    private string CreateFile(string relativePath, double ageInDays = 10, bool readOnly = false)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageInDays));
        if (readOnly)
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }
        return path;
    }

    private void SetFolderAge(string relativePath, double ageInDays)
        => Directory.SetCreationTimeUtc(Path.Combine(_root, relativePath), DateTime.UtcNow.AddDays(-ageInDays));

    // Junctions (unlike directory symlinks) can be created without admin rights.
    private void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        _junctions.Add(link);
    }
}
