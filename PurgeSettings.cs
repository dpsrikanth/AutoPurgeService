using System.Collections.Generic;

namespace AutoPurgeService
{
    public class PurgeSettings
    {
        public const string SectionName = "PurgeSettings";

        public double IntervalMinutes { get; set; } = 60;

        // Local time of day (e.g. "02:00") to run once daily. When set, IntervalMinutes is ignored.
        public TimeOnly? DailyRunTime { get; set; }

        public bool WhatIf { get; set; } = false;
        public List<PurgeRule> Rules { get; set; } = new();
    }

    public class PurgeRule
    {
        public string FolderPath { get; set; } = string.Empty;
        public List<string> Extensions { get; set; } = new();
        public double AgeInDays { get; set; } = 7;
        public AgeType AgeType { get; set; } = AgeType.LastWriteTime;
        public bool Recursive { get; set; } = false;
        public bool DeleteReadOnly { get; set; } = false;
        public bool DeleteEmptyFolders { get; set; } = false;
        public string DisplayName => string.IsNullOrEmpty(FolderPath) ? "Unnamed Rule" : FolderPath;
    }

    public enum AgeType
    {
        LastWriteTime,
        LastAccessTime,
        CreationTime
    }
}
