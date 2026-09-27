using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace AutoPurgeService
{
    public class Worker : BackgroundService
    {
        private const double DefaultIntervalMinutes = 60;

        // IntervalMinutes above this (about 49.7 days, the longest single Task.Delay) is treated as invalid.
        private const double MaxIntervalMinutes = 71582;

        // Waits are done in steps of at most this long, re-checking the clock after each, so a long wait
        // (e.g. until tomorrow's DailyRunTime) can't drift if the machine is paused or its clock adjusted.
        private static readonly TimeSpan MaxWaitStep = TimeSpan.FromHours(1);

        // Deletions beyond this many per rule per cycle are logged at Debug, so a large first purge
        // doesn't flood the Event Log. The rule summary still reports the full count.
        internal const int MaxLoggedDeletionsPerRule = 100;

        // Rules may purge inside these folders (e.g. C:\Windows\Temp) but may not target them directly
        // or target a folder that contains them (e.g. C:\Windows or C:\).
        private static readonly string[] ProtectedFolders = new[]
            {
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.System,
                Environment.SpecialFolder.SystemX86,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.CommonDocuments,
                Environment.SpecialFolder.UserProfile
            }
            .Select(folder => Environment.GetFolderPath(folder))
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => Path.TrimEndingDirectorySeparator(path))
            .ToArray();

        // Lists a single folder. Hidden and system entries are included, and errors are thrown rather than
        // ignored so that EnumerateFiles can log which folder couldn't be read.
        private static readonly EnumerationOptions SingleFolderOptions = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        };

        private readonly ILogger<Worker> _logger;
        private readonly IOptionsMonitor<PurgeSettings> _settingsMonitor;
        private readonly IConfiguration _configuration;

        // When the last purge ran, persisted so a restart doesn't trigger an extra run.
        private DateTimeOffset? _lastRunUtc;

        // When the last cycle was attempted, including attempts where the settings couldn't be read.
        private DateTimeOffset? _lastAttemptUtc;

        // Next to the executable, so uninstalling the service is still just deleting its folder.
        internal string StateFilePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "last-run.txt");

        public Worker(ILogger<Worker> logger, IOptionsMonitor<PurgeSettings> settingsMonitor, IConfiguration configuration)
        {
            _logger = logger;
            _settingsMonitor = settingsMonitor;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Auto Purge Service started.");
            _lastRunUtc = LoadLastRun();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await WaitForNextRunAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogInformation("Starting purge cycle at: {time}", DateTimeOffset.Now);

                bool settingsRead = false;
                try
                {
                    // Read settings inside the try: a value that can't be bound (e.g. "IntervalMinutes": "sixty")
                    // throws here, and that should skip this cycle rather than stop the service.
                    var settings = _settingsMonitor.CurrentValue;
                    settingsRead = true;
                    ReportConfigurationProblems();
                    ExecutePurge(settings, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Purge cycle interrupted because the service is stopping.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred during the purge cycle.");
                }

                _lastAttemptUtc = DateTimeOffset.UtcNow;

                // Unreadable settings mean nothing was purged, so that attempt doesn't count as a run;
                // GetNextRunUtc retries it after the default interval instead.
                if (settingsRead)
                {
                    _lastRunUtc = _lastAttemptUtc;
                    SaveLastRun(_lastAttemptUtc.Value);
                }

                _logger.LogInformation("Purge cycle completed.");
            }

            _logger.LogInformation("Auto Purge Service is stopping.");
        }

        // Runs a single purge now for "--once", ignoring the schedule. It isn't recorded as the last run,
        // so testing from the command line doesn't shift the service's schedule. Returns whether the purge ran.
        internal bool RunOnce(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Running a single purge (--once) at: {time}", DateTimeOffset.Now);
            try
            {
                var settings = _settingsMonitor.CurrentValue;
                ReportConfigurationProblems();
                ExecutePurge(settings, cancellationToken);
                _logger.LogInformation("Single purge completed.");
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Single purge stopped before finishing.");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The single purge failed.");
                return false;
            }
        }

        // Waits until the next run is due. The due time is recalculated whenever the configuration changes,
        // so a new schedule takes effect without waiting out the old one.
        private async Task WaitForNextRunAsync(CancellationToken stoppingToken)
        {
            var nextRunUtc = GetNextRunUtc();
            LogNextRun("Next run at", nextRunUtc);

            while (true)
            {
                var remaining = nextRunUtc - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return;
                }

                var configChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var subscription = _settingsMonitor.OnChange(_ => configChanged.TrySetResult());
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delay = Task.Delay(remaining < MaxWaitStep ? remaining : MaxWaitStep, delayCts.Token);

                if (await Task.WhenAny(delay, configChanged.Task) == delay)
                {
                    await delay; // Throws if the service is stopping.
                    continue;
                }

                delayCts.Cancel();
                var newNextRunUtc = GetNextRunUtc();
                if (newNextRunUtc != nextRunUtc)
                {
                    nextRunUtc = newNextRunUtc;
                    if (nextRunUtc <= DateTimeOffset.UtcNow)
                    {
                        _logger.LogInformation("Schedule changed. A run is due now.");
                    }
                    LogNextRun("Schedule changed. Next run at", nextRunUtc);
                }
            }
        }

        private void LogNextRun(string prefix, DateTimeOffset nextRunUtc)
        {
            var remaining = nextRunUtc - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                _logger.LogInformation("{prefix} {next:yyyy-MM-dd HH:mm} (in {minutes:F0} minutes).",
                    prefix, nextRunUtc.ToLocalTime(), Math.Ceiling(remaining.TotalMinutes));
            }
        }

        // With DailyRunTime set, a run is due at the first occurrence of that local time after the last run;
        // otherwise IntervalMinutes after the last run. With no run recorded, a run is due now. So after a
        // restart the service runs straight away only if it never ran or a scheduled run was missed meanwhile.
        private DateTimeOffset GetNextRunUtc()
        {
            var nowUtc = DateTimeOffset.UtcNow;

            PurgeSettings settings;
            try
            {
                settings = _settingsMonitor.CurrentValue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not read the schedule ({error}). Retrying in {default} minutes.", ex.Message, DefaultIntervalMinutes);
                return (_lastAttemptUtc ?? nowUtc).AddMinutes(DefaultIntervalMinutes);
            }

            if (_lastRunUtc is not { } lastRunUtc)
            {
                return nowUtc;
            }

            if (settings.DailyRunTime is { } dailyRunTime)
            {
                return NextDailyRun(lastRunUtc, dailyRunTime, TimeZoneInfo.Local);
            }

            return lastRunUtc.AddMinutes(GetIntervalMinutes(settings));
        }

        // The first moment after afterUtc when the clock in zone reads runTime. If a daylight saving change
        // skips runTime that day, the run happens an hour later; if it repeats it, the run happens once.
        internal static DateTimeOffset NextDailyRun(DateTimeOffset afterUtc, TimeOnly runTime, TimeZoneInfo zone)
        {
            var candidate = TimeZoneInfo.ConvertTime(afterUtc, zone).Date + runTime.ToTimeSpan();
            while (true)
            {
                var localTime = zone.IsInvalidTime(candidate) ? candidate.AddHours(1) : candidate;
                var runUtc = new DateTimeOffset(localTime, zone.GetUtcOffset(localTime)).ToUniversalTime();
                if (runUtc > afterUtc)
                {
                    return runUtc;
                }
                candidate = candidate.AddDays(1);
            }
        }

        private DateTimeOffset? LoadLastRun()
        {
            try
            {
                if (File.Exists(StateFilePath))
                {
                    var text = File.ReadAllText(StateFilePath).Trim();
                    // A far-future value (corrupt file, or a clock that was wrong) would otherwise block runs indefinitely.
                    if (DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastRunUtc)
                        && lastRunUtc <= DateTimeOffset.UtcNow.AddDays(1))
                    {
                        _logger.LogInformation("Last purge ran at {time:yyyy-MM-dd HH:mm}.", lastRunUtc.ToLocalTime());
                        return lastRunUtc;
                    }
                    _logger.LogWarning("Ignoring unreadable last-run record '{text}' in '{path}'.", text, StateFilePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not read the last-run record in '{path}'. {error}", StateFilePath, ex.Message);
            }

            _logger.LogInformation("No previous purge recorded; running now.");
            return null;
        }

        private void SaveLastRun(DateTimeOffset lastRunUtc)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
                File.WriteAllText(StateFilePath, lastRunUtc.ToString("O", CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not record the last run in '{path}', so a restart will run the purge again straight away. {error}",
                    StateFilePath, ex.Message);
            }
        }

        private double GetIntervalMinutes(PurgeSettings settings)
        {
            var intervalMinutes = settings.IntervalMinutes;

            // Written as a negated range check so NaN is rejected too.
            if (!(intervalMinutes > 0 && intervalMinutes <= MaxIntervalMinutes))
            {
                _logger.LogWarning("IntervalMinutes configuration is invalid ({val}). It must be greater than 0 and at most {max}. Defaulting to {default} minutes.",
                    intervalMinutes, MaxIntervalMinutes, DefaultIntervalMinutes);
                return DefaultIntervalMinutes;
            }

            return intervalMinutes;
        }

        // The configuration binder silently drops a rule containing a value it can't convert (e.g. "AgeType": "Modified")
        // and silently ignores misspelled setting names (e.g. "Recursve"). Binding each part again on its own surfaces both.
        internal void ReportConfigurationProblems()
        {
            var section = _configuration.GetSection(PurgeSettings.SectionName);

            // Rules are checked one by one below; problems inside a rule never make this top-level bind throw.
            ReportUnknownSettings<PurgeSettings>(section);

            foreach (var ruleSection in section.GetSection(nameof(PurgeSettings.Rules)).GetChildren())
            {
                try
                {
                    ruleSection.Get<PurgeRule>();
                }
                catch (Exception ex)
                {
                    _logger.LogError("The rule at '{path}' (FolderPath '{folder}') could not be read and will not run: {error}",
                        ruleSection.Path, ruleSection[nameof(PurgeRule.FolderPath)], ex.Message);
                    continue;
                }

                ReportUnknownSettings<PurgeRule>(ruleSection);
            }
        }

        private void ReportUnknownSettings<T>(IConfigurationSection section)
        {
            try
            {
                section.Get<T>(options => options.ErrorOnUnknownConfiguration = true);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("Unrecognized setting under '{path}' will be ignored: {error}", section.Path, ex.Message);
            }
        }

        internal void ExecutePurge(PurgeSettings settings, CancellationToken cancellationToken)
        {
            if (settings.Rules == null || settings.Rules.Count == 0)
            {
                _logger.LogWarning("No purge rules configured. Skipping purge cycle.");
                return;
            }

            if (settings.WhatIf)
            {
                _logger.LogWarning("WhatIf mode is on: matching files will be logged but not deleted.");
            }

            bool? lastAccessUpdatesDisabled = null;

            foreach (var rule in settings.Rules)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogInformation("Processing rule for folder: '{folder}' (Age: {days} days, AgeType: {ageType}, Recursive: {recursive})",
                    rule.DisplayName, rule.AgeInDays, rule.AgeType, rule.Recursive);

                if (string.IsNullOrWhiteSpace(rule.FolderPath))
                {
                    _logger.LogWarning("Rule FolderPath is empty. Skipping rule.");
                    continue;
                }

                var unsafeReason = GetUnsafePathReason(rule.FolderPath);
                if (unsafeReason != null)
                {
                    _logger.LogWarning("Rule FolderPath '{folder}' is not allowed: {reason} Skipping rule.", rule.FolderPath, unsafeReason);
                    continue;
                }

                // Negated so NaN is rejected too.
                if (!(rule.AgeInDays >= 0))
                {
                    _logger.LogWarning("Rule AgeInDays ({days}) for '{folder}' must be 0 or greater. Skipping rule.", rule.AgeInDays, rule.FolderPath);
                    continue;
                }

                // Matching every file has to be asked for with "*", so a missing or emptied Extensions list
                // can't silently turn a rule into "delete everything".
                bool matchAll = rule.Extensions?.Any(e => e?.Trim() is "*" or "*.*") == true;
                var normalizedExtensions = NormalizeExtensions(rule.Extensions);
                if (!matchAll && normalizedExtensions.Count == 0)
                {
                    _logger.LogWarning("Rule for '{folder}' has no Extensions. Use [\"*\"] to match all files. Skipping rule.", rule.FolderPath);
                    continue;
                }

                if (rule.AgeType == AgeType.LastAccessTime && (lastAccessUpdatesDisabled ??= IsLastAccessUpdatesDisabled()))
                {
                    _logger.LogWarning("Rule for '{folder}' uses LastAccessTime, but NTFS last-access updates are disabled on this machine. " +
                        "Access times may be out of date, so files that are still being read could be deleted. Consider AgeType LastWriteTime.", rule.FolderPath);
                }

                if (!Directory.Exists(rule.FolderPath))
                {
                    _logger.LogWarning("Directory '{folder}' does not exist. Skipping rule.", rule.FolderPath);
                    continue;
                }

                try
                {
                    PurgeFolder(rule, matchAll, normalizedExtensions, settings.WhatIf, cancellationToken);
                }
                catch (Exception ruleEx) when (ruleEx is not OperationCanceledException)
                {
                    _logger.LogError(ruleEx, "Error processing rule for folder '{folder}'.", rule.FolderPath);
                }
            }
        }

        private void PurgeFolder(PurgeRule rule, bool matchAll, HashSet<string> normalizedExtensions, bool whatIf, CancellationToken cancellationToken)
        {
            var subfolders = new List<string>();
            int processedCount = 0;
            int deletedCount = 0;
            int readOnlyCount = 0;
            int errorCount = 0;

            foreach (var filePath in EnumerateFiles(rule.FolderPath, rule.Recursive, subfolders, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                processedCount++;

                // Check extension
                if (!matchAll)
                {
                    var fileExt = Path.GetExtension(filePath);
                    if (!normalizedExtensions.Contains(fileExt))
                    {
                        continue;
                    }
                }

                // Check age
                try
                {
                    // UTC, so ages aren't off by an hour around daylight saving changes.
                    var fileInfo = new FileInfo(filePath);
                    DateTime fileDateUtc = rule.AgeType switch
                    {
                        AgeType.LastAccessTime => fileInfo.LastAccessTimeUtc,
                        AgeType.CreationTime => fileInfo.CreationTimeUtc,
                        _ => fileInfo.LastWriteTimeUtc
                    };

                    var age = DateTime.UtcNow - fileDateUtc;
                    if (age.TotalDays < rule.AgeInDays)
                    {
                        continue;
                    }

                    if (fileInfo.IsReadOnly && !rule.DeleteReadOnly)
                    {
                        readOnlyCount++;
                        _logger.LogDebug("Skipping read-only file '{file}'. Set DeleteReadOnly to delete read-only files.", filePath);
                        continue;
                    }

                    var level = deletedCount < MaxLoggedDeletionsPerRule ? LogLevel.Information : LogLevel.Debug;
                    _logger.Log(level, "{action} file: '{file}' (Last modified/accessed/created: {date}, Age: {age:F2} days)",
                        whatIf ? "WhatIf: would delete" : "Deleting", filePath, fileDateUtc.ToLocalTime(), age.TotalDays);

                    if (!whatIf)
                    {
                        if (fileInfo.IsReadOnly)
                        {
                            fileInfo.IsReadOnly = false;
                        }
                        File.Delete(filePath);
                    }
                    deletedCount++;
                }
                catch (Exception fileEx)
                {
                    errorCount++;
                    _logger.LogError(fileEx, "Failed to process or delete file '{file}'.", filePath);
                }
            }

            _logger.LogInformation("Finished rule for '{folder}'. Inspected {processed} files, {action} {deleted} files, skipped {readOnly} read-only files. Failed deletions: {errors}.",
                rule.DisplayName, processedCount, whatIf ? "would delete" : "deleted", deletedCount, readOnlyCount, errorCount);

            if (deletedCount > MaxLoggedDeletionsPerRule)
            {
                _logger.LogInformation("Only the first {max} files for '{folder}' were listed individually; the other {more} were logged at Debug level.",
                    MaxLoggedDeletionsPerRule, rule.DisplayName, deletedCount - MaxLoggedDeletionsPerRule);
            }

            if (rule.DeleteEmptyFolders)
            {
                RemoveEmptyFolders(rule, subfolders, whatIf, cancellationToken);
            }
        }

        // Walks the tree one folder at a time, so a folder that can't be read is logged and skipped without
        // ending the rest of the rule. Junctions and symbolic links to folders are never followed, since they
        // could lead the purge outside FolderPath. Each subfolder walked is added to subfolders, parents first.
        private IEnumerable<string> EnumerateFiles(string rootFolder, bool recursive, List<string> subfolders, CancellationToken cancellationToken)
        {
            var pending = new Queue<string>();
            pending.Enqueue(rootFolder);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = pending.Dequeue();

                IEnumerator<FileSystemInfo> entries;
                try
                {
                    entries = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", SingleFolderOptions).GetEnumerator();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Could not read folder '{folder}'; skipping it. {error}", folder, ex.Message);
                    continue;
                }

                using (entries)
                {
                    while (true)
                    {
                        FileSystemInfo entry;
                        try
                        {
                            if (!entries.MoveNext())
                            {
                                break;
                            }
                            entry = entries.Current;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.LogWarning("Could not finish reading folder '{folder}'; skipping the rest of it. {error}", folder, ex.Message);
                            break;
                        }

                        if (entry is not DirectoryInfo)
                        {
                            yield return entry.FullName;
                        }
                        else if (recursive)
                        {
                            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                _logger.LogDebug("Not following junction or symbolic link '{folder}'.", entry.FullName);
                                continue;
                            }

                            pending.Enqueue(entry.FullName);
                            subfolders.Add(entry.FullName);
                        }
                    }
                }
            }
        }

        // Removes subfolders (never FolderPath itself) that are empty and were created at least AgeInDays ago.
        // Creation time is used because a folder's last-write time changes whenever a file inside it is deleted.
        // Children come after parents in subfolders, so walking it backwards lets a parent emptied by removing
        // its children be removed in the same pass.
        private void RemoveEmptyFolders(PurgeRule rule, List<string> subfolders, bool whatIf, CancellationToken cancellationToken)
        {
            int removedCount = 0;

            for (int i = subfolders.Count - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = subfolders[i];

                try
                {
                    var age = DateTime.UtcNow - Directory.GetCreationTimeUtc(folder);
                    if (age.TotalDays < rule.AgeInDays || Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        continue;
                    }

                    _logger.LogInformation("{action} empty folder: '{folder}'", whatIf ? "WhatIf: would remove" : "Removing", folder);
                    if (!whatIf)
                    {
                        Directory.Delete(folder);
                    }
                    removedCount++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Could not remove folder '{folder}'. {error}", folder, ex.Message);
                }
            }

            _logger.LogInformation("{action} {count} empty folders under '{folder}'.",
                whatIf ? "Would remove" : "Removed", removedCount, rule.DisplayName);
        }

        private static bool IsLastAccessUpdatesDisabled()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                // The setting behind 'fsutil behavior query disablelastaccess'. Its low bits hold fsutil's value
                // (0/2 = updates enabled, 1/3 = disabled, with 2/3 meaning system managed), so an odd value means disabled.
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
                return key?.GetValue("NtfsDisableLastAccessUpdate") is int value && (value & 1) == 1;
            }
            catch (Exception)
            {
                // If the setting can't be read, don't warn.
                return false;
            }
        }

        private static HashSet<string> NormalizeExtensions(IEnumerable<string>? extensions)
        {
            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (extensions == null) return normalized;

            foreach (var ext in extensions)
            {
                if (string.IsNullOrWhiteSpace(ext)) continue;

                // Wildcard-only entries ("*", "*.*") reduce to "" or "." and are handled by matchAll instead.
                var clean = ext.Trim().Replace("*", "");
                if (clean.Length == 0 || clean == ".") continue;

                if (!clean.StartsWith('.'))
                {
                    clean = "." + clean;
                }
                normalized.Add(clean);
            }
            return normalized;
        }

        internal static string? GetUnsafePathReason(string folderPath)
        {
            // A relative path would resolve against the service's working directory (C:\Windows\System32).
            if (!Path.IsPathFullyQualified(folderPath))
            {
                return "it must be an absolute path.";
            }

            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullPath) ?? string.Empty);
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return "it is a drive or share root.";
            }

            foreach (var protectedFolder in ProtectedFolders)
            {
                if (string.Equals(protectedFolder, fullPath, StringComparison.OrdinalIgnoreCase) ||
                    protectedFolder.StartsWith(fullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return $"it is or contains the system folder '{protectedFolder}'.";
                }
            }

            return null;
        }
    }
}
