using System.Security.Cryptography;

namespace MyPlasm.Inspector.ProtocolAnalysis;

internal sealed class CampaignEvidenceGuard : IDisposable
{
    private readonly ICampaignComparisonTestHook? _testHook;
    private readonly IReadOnlyList<CampaignEvidenceSet> _sets;
    private bool _disposed;

    private CampaignEvidenceGuard(
        CampaignPathSet paths,
        IReadOnlyList<CampaignEvidenceSet> sets,
        ICampaignComparisonTestHook? testHook)
    {
        Paths = paths;
        _sets = sets;
        _testHook = testHook;
    }

    public CampaignPathSet Paths { get; }

    public IReadOnlyList<CampaignEvidenceSet> Sets => _sets;

    public static async Task<CampaignEvidenceGuard> OpenAsync(
        CampaignPathSet paths,
        ICampaignComparisonTestHook? testHook,
        CancellationToken cancellationToken)
    {
        List<CampaignEvidenceSet> sets = [];
        try
        {
            foreach (string directory in paths.AnalysisDirectories)
            {
                sets.Add(
                    await CampaignEvidenceSet.OpenAsync(
                            directory,
                            paths.OutputDirectory,
                            cancellationToken)
                        .ConfigureAwait(false));
            }

            CampaignEvidenceGuard guard = new(paths, sets, testHook);
            guard.ValidateOutputIdentity(overwrite: true);
            return guard;
        }
        catch
        {
            foreach (CampaignEvidenceSet set in sets)
            {
                set.Dispose();
            }

            throw;
        }
    }

    public void NotifyAfterInitialHashes(CampaignEvidenceSet set) =>
        _testHook?.OnInputCheckpoint(
            CampaignInputCheckpoint.AfterInitialHashes,
            set.AnalysisDirectory);

    public async Task VerifyStableAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        foreach (CampaignEvidenceSet set in _sets)
        {
            await set.VerifyStableAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void ValidateOutputIdentity(bool overwrite)
    {
        ThrowIfDisposed();
        string outputDirectory = Paths.OutputDirectory;
        if (!Directory.Exists(outputDirectory))
        {
            return;
        }

        if (!overwrite &&
            Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new AnalysisOutputException(
                "The comparison output directory is not empty. Use --overwrite to replace campaign report files explicitly.");
        }

        List<FileStream> retainedOutputHandles = [];
        try
        {
            IReadOnlyDictionary<string, string> existingEntries = Directory
                .EnumerateFileSystemEntries(outputDirectory)
                .ToDictionary(
                    path => Path.GetFileName(path)!,
                    path => path,
                    StringComparer.Ordinal);
            foreach (string reportName in CampaignReportWriter.ReportFileNames)
            {
                if (!existingEntries.TryGetValue(reportName, out string? path))
                {
                    continue;
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(path);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    throw new CampaignPathCollisionException(
                        $"Existing output report '{reportName}' could not be inspected safely: {exception.Message}");
                }

                if ((attributes & FileAttributes.Directory) != 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0 ||
                    !File.Exists(path))
                {
                    throw new CampaignPathCollisionException(
                        $"Existing output report '{reportName}' must be a regular file, not a symbolic link, reparse point, directory, or file alias.");
                }

                RejectLinkedFile(path, "Existing output report");
                try
                {
                    using FileStream probe = new(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    throw new CampaignPathCollisionException(
                        $"Existing output report '{reportName}' is locked, aliased, shares a physical file with another report, or resolves to protected input evidence.");
                }

                retainedOutputHandles.Add(new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read));
            }
        }
        finally
        {
            foreach (FileStream handle in retainedOutputHandles)
            {
                handle.Dispose();
            }
        }
    }

    public void OnPublicationCheckpoint(
        CampaignPublicationCheckpoint checkpoint,
        string? reportFileName) =>
        _testHook?.OnPublicationCheckpoint(checkpoint, reportFileName);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (CampaignEvidenceSet set in _sets)
        {
            set.Dispose();
        }
    }

    internal static void RejectLinkedFile(string path, string description)
    {
        FileInfo info = new(path);
        info.Refresh();
        if (!info.Exists)
        {
            throw new CampaignInputValidationException(
                $"{description} does not exist.");
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.LinkTarget is not null)
        {
            throw new CampaignPathCollisionException(
                $"{description} must be a regular file, not a symbolic link, reparse point, or file alias.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class CampaignEvidenceSet : IDisposable
{
    private readonly IReadOnlyDictionary<string, CampaignEvidenceFile> _files;
    private bool _disposed;

    private CampaignEvidenceSet(
        string analysisDirectory,
        IReadOnlyDictionary<string, CampaignEvidenceFile> files)
    {
        AnalysisDirectory = analysisDirectory;
        _files = files;
    }

    public string AnalysisDirectory { get; }

    public IReadOnlyDictionary<string, string> Paths =>
        _files.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.FullPath,
            StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> InitialHashes =>
        _files.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.InitialSha256,
            StringComparer.Ordinal);

    public static async Task<CampaignEvidenceSet> OpenAsync(
        string directory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ValidateExactFileSet(directory);
        SortedDictionary<string, CampaignEvidenceFile> files =
            new(StringComparer.Ordinal);
        try
        {
            foreach (string name in
                     CampaignAnalysisReader.RequiredReportFileNames
                         .Order(StringComparer.Ordinal))
            {
                string fullPath = CampaignPathSafety.Normalize(
                    Path.Combine(directory, name));
                CampaignEvidenceGuard.RejectLinkedFile(
                    fullPath,
                    $"Required input report '{name}'");
                string resolvedPath =
                    CampaignPathSafety.ResolveExistingLinks(fullPath);
                string resolvedOutput =
                    CampaignPathSafety.ResolveExistingLinks(outputDirectory);
                if (CampaignPathSafety.PathsOverlap(
                        resolvedPath,
                        resolvedOutput))
                {
                    throw new CampaignPathCollisionException(
                        $"Required input report '{name}' resolves inside the comparison output directory.");
                }

                FileStream probe;
                try
                {
                    probe = new FileStream(
                        fullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None,
                        bufferSize: 128 * 1024,
                        FileOptions.Asynchronous |
                        FileOptions.SequentialScan);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    throw new CampaignInputValidationException(
                        $"Required input report '{name}' is locked or shares a physical file with another required report.",
                        exception);
                }

                long length;
                string hash;
                await using (probe.ConfigureAwait(false))
                {
                    length = probe.Length;
                    hash = Convert.ToHexString(
                        await SHA256.HashDataAsync(probe, cancellationToken)
                            .ConfigureAwait(false));
                }

                FileStream retained = new(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1,
                    FileOptions.None);
                FileInfo info = new(fullPath);
                info.Refresh();
                CampaignEvidenceFile evidenceFile = new(
                    name,
                    fullPath,
                    resolvedPath,
                    length,
                    info.LastWriteTimeUtc,
                    hash,
                    retained);
                if (!files.TryAdd(name, evidenceFile))
                {
                    retained.Dispose();
                    throw new CampaignInputValidationException(
                        $"Required input report '{name}' is duplicated.");
                }
            }

            return new CampaignEvidenceSet(directory, files);
        }
        catch
        {
            foreach (CampaignEvidenceFile file in files.Values)
            {
                file.Dispose();
            }

            throw;
        }
    }

    public async Task VerifyStableAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateExactFileSet(AnalysisDirectory);
        foreach ((string name, CampaignEvidenceFile snapshot) in _files)
        {
            CampaignEvidenceGuard.RejectLinkedFile(
                snapshot.FullPath,
                $"Required input report '{name}'");
            string resolved =
                CampaignPathSafety.ResolveExistingLinks(snapshot.FullPath);
            FileInfo info = new(snapshot.FullPath);
            info.Refresh();
            if (!string.Equals(
                    resolved,
                    snapshot.ResolvedPath,
                    StringComparison.OrdinalIgnoreCase) ||
                info.Length != snapshot.InitialLength ||
                info.LastWriteTimeUtc != snapshot.InitialLastWriteTimeUtc)
            {
                throw new CampaignInputValidationException(
                    $"Required input report '{name}' changed identity, length, or timestamp during comparison.");
            }

            string currentHash = await CaptureAnalyzer.CalculateSha256Async(
                    snapshot.FullPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    currentHash,
                    snapshot.InitialSha256,
                    StringComparison.Ordinal))
            {
                throw new CampaignInputValidationException(
                    $"Required input report '{name}' changed after initial hashing.");
            }

            try
            {
                using FileStream replacementProbe = new(
                    snapshot.FullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
                throw new CampaignInputValidationException(
                    $"Required input report '{name}' was replaced after its protected handle was opened.");
            }
            catch (CampaignInputValidationException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Expected: the retained read handle denies exclusive access to
                // the same physical file. A replacement path would open here.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (CampaignEvidenceFile file in _files.Values)
        {
            file.Dispose();
        }
    }

    private static void ValidateExactFileSet(string directory)
    {
        string[] actual = Directory
            .EnumerateFileSystemEntries(directory)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
        string[] expected = CampaignAnalysisReader
            .RequiredReportFileNames
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new CampaignInputValidationException(
                "Each analysis directory must contain exactly the six sanitized analyzer outputs and no other files or directories.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class CampaignEvidenceFile : IDisposable
{
    public CampaignEvidenceFile(
        string reportName,
        string fullPath,
        string resolvedPath,
        long initialLength,
        DateTime initialLastWriteTimeUtc,
        string initialSha256,
        FileStream retainedHandle)
    {
        ReportName = reportName;
        FullPath = fullPath;
        ResolvedPath = resolvedPath;
        InitialLength = initialLength;
        InitialLastWriteTimeUtc = initialLastWriteTimeUtc;
        InitialSha256 = initialSha256;
        RetainedHandle = retainedHandle;
    }

    public string ReportName { get; }

    public string FullPath { get; }

    public string ResolvedPath { get; }

    public long InitialLength { get; }

    public DateTime InitialLastWriteTimeUtc { get; }

    public string InitialSha256 { get; }

    private FileStream RetainedHandle { get; }

    public void Dispose() => RetainedHandle.Dispose();
}
