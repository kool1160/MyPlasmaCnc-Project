using MyPlasm.Inspector.App;

namespace MyPlasm.Inspector.Tests;

public sealed class StartupLogTests
{
    [Fact]
    public void CreateSafeFallsBackWhenLogDirectoryCannotBeCreated()
    {
        StubStartupLogPlatform platform = new()
        {
            CreateDirectoryException = new UnauthorizedAccessException("synthetic directory denial")
        };

        StartupLog? log = null;
        Exception? escaped = Record.Exception(() => log = StartupLog.CreateSafe(platform));

        Assert.Null(escaped);
        Assert.NotNull(log);
        Assert.False(log.IsPersistentFileLoggingAvailable);
        Assert.Equal(StartupLog.FileLoggingUnavailableMessage, log.DiagnosticLocation);
        Assert.Contains("synthetic directory denial", log.PersistentLoggingFailure);
        Assert.Contains(
            log.GetFallbackEntries(),
            entry => entry.Contains("Persistent startup file logging disabled", StringComparison.Ordinal));
        Assert.Contains(
            log.GetFallbackEntries(),
            entry => entry.Contains("Startup logger initialized", StringComparison.Ordinal));
    }

    [Fact]
    public void FirstAppendFailureDisablesPersistentWritesWithoutRetrying()
    {
        StubStartupLogPlatform platform = new()
        {
            AppendException = new IOException("synthetic disk full")
        };
        StartupLog log = StartupLog.CreateSafe(platform);
        int appendCallsAfterFailure = platform.AppendCalls;

        Exception? escaped = Record.Exception(
            () =>
            {
                log.Stage("later stage");
                log.Exception("later exception", new InvalidOperationException("synthetic"));
                log.WriteEnvironment(softwareRenderingActive: true);
            });

        Assert.Null(escaped);
        Assert.False(log.IsPersistentFileLoggingAvailable);
        Assert.Equal(1, appendCallsAfterFailure);
        Assert.Equal(appendCallsAfterFailure, platform.AppendCalls);
        Assert.Contains("synthetic disk full", log.PersistentLoggingFailure);
        Assert.Contains(
            log.GetFallbackEntries(),
            entry => entry.Contains("later stage", StringComparison.Ordinal));
    }

    [Fact]
    public void FailureAfterSuccessfulAppendPreservesFirstFailureAndLaterMessages()
    {
        StubStartupLogPlatform platform = new() { SuccessfulAppendsBeforeFailure = 1 };
        StartupLog log = StartupLog.CreateSafe(platform);

        log.Stage("entry that triggers failure");
        log.Stage("entry after persistent logging was disabled");

        Assert.False(log.IsPersistentFileLoggingAvailable);
        Assert.Equal(2, platform.AppendCalls);
        Assert.Contains("synthetic append failure", log.PersistentLoggingFailure);
        Assert.Contains(
            log.GetFallbackEntries(),
            entry => entry.Contains("entry that triggers failure", StringComparison.Ordinal));
        Assert.Contains(
            log.GetFallbackEntries(),
            entry => entry.Contains("entry after persistent logging was disabled", StringComparison.Ordinal));
    }

    [Fact]
    public void EnvironmentAndPackagedDllProbeFailuresNeverEscape()
    {
        StubStartupLogPlatform platform = new()
        {
            ThrowEnvironmentProbes = true,
            FileExistsException = new IOException("synthetic file lock")
        };
        StartupLog log = StartupLog.CreateSafe(platform);

        Exception? escaped = Record.Exception(() => log.WriteEnvironment(softwareRenderingActive: true));

        Assert.Null(escaped);
        Assert.True(log.IsPersistentFileLoggingAvailable);
        Assert.Contains(
            log.GetFallbackEntries(),
            entry => entry.Contains("Unavailable", StringComparison.Ordinal));

        platform.ThrowEnvironmentProbes = false;
        escaped = Record.Exception(() => log.WriteEnvironment(softwareRenderingActive: true));

        Assert.Null(escaped);
        Assert.Contains(
            log.GetFallbackEntries(),
            entry => entry.Contains("synthetic file lock", StringComparison.Ordinal));

        platform.FileExistsException = null;
        platform.FileExistsResult = true;
        platform.ComputeSha256Exception = new IOException("synthetic hash failure");

        escaped = Record.Exception(() => log.WriteEnvironment(softwareRenderingActive: false));

        Assert.Null(escaped);
        Assert.Contains(
            log.GetFallbackEntries(),
            entry => entry.Contains("synthetic hash failure", StringComparison.Ordinal));
    }

    [Fact]
    public void FallbackBufferIsBoundedAndIndividualEntriesAreTruncated()
    {
        StubStartupLogPlatform platform = new()
        {
            CreateDirectoryException = new IOException("synthetic persistent logging failure")
        };
        StartupLog log = StartupLog.CreateSafe(platform);
        string oversizedMessage = new('X', StartupLog.MaximumFallbackEntryLength * 2);

        for (int index = 0; index < StartupLog.FallbackEntryCapacity + 50; index++)
        {
            log.Stage($"{index:D4}:{oversizedMessage}");
        }

        IReadOnlyList<string> entries = log.GetFallbackEntries();
        Assert.Equal(StartupLog.FallbackEntryCapacity, entries.Count);
        Assert.All(entries, entry => Assert.True(entry.Length <= StartupLog.MaximumFallbackEntryLength));
        Assert.DoesNotContain(entries, entry => entry.Contains("0000:", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Contains("0305:", StringComparison.Ordinal));
        Assert.All(
            entries.Where(entry => entry.Contains('X')),
            entry => Assert.Contains("[diagnostic entry truncated]", entry, StringComparison.Ordinal));
    }

    [Fact]
    public void ExceptionFormattingAndTraceFailuresNeverEscape()
    {
        StubStartupLogPlatform platform = new()
        {
            AppendException = new IOException("synthetic append failure"),
            TraceException = new IOException("synthetic trace failure")
        };
        StartupLog log = StartupLog.CreateSafe(platform);

        Exception? escaped = Record.Exception(
            () => log.Exception("synthetic source", new ThrowingToStringException()));

        Assert.Null(escaped);
        Assert.Contains(
            log.GetFallbackEntries(),
            entry => entry.Contains("Exception details were unavailable", StringComparison.Ordinal));
    }

    private sealed class ThrowingToStringException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("synthetic formatting failure");
    }

    private sealed class StubStartupLogPlatform : IStartupLogPlatform
    {
        public Exception? CreateDirectoryException { get; init; }

        public Exception? AppendException { get; init; }

        public Exception? TraceException { get; init; }

        public Exception? FileExistsException { get; set; }

        public Exception? ComputeSha256Exception { get; set; }

        public int SuccessfulAppendsBeforeFailure { get; init; } = int.MaxValue;

        public bool ThrowEnvironmentProbes { get; set; }

        public bool FileExistsResult { get; set; }

        public int AppendCalls { get; private set; }

        public string GetLocalApplicationDataPath() => "C:\\SyntheticLocalAppData";

        public void CreateDirectory(string path)
        {
            if (CreateDirectoryException is not null)
            {
                throw CreateDirectoryException;
            }
        }

        public void AppendAllText(string path, string entry)
        {
            AppendCalls++;
            if (AppendException is not null)
            {
                throw AppendException;
            }

            if (AppendCalls > SuccessfulAppendsBeforeFailure)
            {
                throw new IOException("synthetic append failure");
            }
        }

        public void WriteTrace(string entry)
        {
            if (TraceException is not null)
            {
                throw TraceException;
            }
        }

        public string GetApplicationVersion() => EnvironmentValue("1.0.0.0");

        public string GetProcessDescription() => EnvironmentValue("X86; 64-bit process: False");

        public string GetOperatingSystemDescription() => EnvironmentValue("Synthetic Windows");

        public string GetRuntimeDescription() => EnvironmentValue(".NET 8");

        public string GetCurrentDirectory() => EnvironmentValue("C:\\SyntheticCurrent");

        public string GetApplicationBaseDirectory() => EnvironmentValue("C:\\SyntheticApp");

        public string GetExecutablePath() => EnvironmentValue("C:\\SyntheticApp\\MyPlasm Inspector.exe");

        public bool FileExists(string path)
        {
            if (FileExistsException is not null)
            {
                throw FileExistsException;
            }

            return FileExistsResult;
        }

        public string ComputeSha256(string path)
        {
            if (ComputeSha256Exception is not null)
            {
                throw ComputeSha256Exception;
            }

            return new string('A', 64);
        }

        public int GetRenderingTier()
        {
            if (ThrowEnvironmentProbes)
            {
                throw new InvalidOperationException("synthetic rendering probe failure");
            }

            return 0;
        }

        private string EnvironmentValue(string value)
        {
            if (ThrowEnvironmentProbes)
            {
                throw new InvalidOperationException("synthetic environment probe failure");
            }

            return value;
        }
    }
}
