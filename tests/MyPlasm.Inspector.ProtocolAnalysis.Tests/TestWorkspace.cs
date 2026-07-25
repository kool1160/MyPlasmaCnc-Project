namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "MyPlasmProtocolAnalysisTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathFor(string relativePath) => Path.Combine(Root, relativePath);

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
