namespace SoFresh.Core.Tests;

internal sealed class FixtureWorkspace : IDisposable
{
    private static readonly string FixtureParent = Path.Combine(Path.GetTempPath(), "SoFresh.Tests");
    private bool disposed;

    public FixtureWorkspace()
    {
        Root = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string CreateTextFile(string relativePath, string content)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string CreateBinaryFile(string relativePath, int byteCount, byte value)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat(value, byteCount).Select(static item => (byte)item).ToArray());
        return path;
    }

    public string Resolve(string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(Root, relativePath));
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The fixture cannot escape its temporary root.");
        }

        return candidate;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        var normalizedRoot = Path.GetFullPath(Root);
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(FixtureParent))
            + Path.DirectorySeparatorChar;
        if (!normalizedRoot.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to clean up a fixture outside the test directory.");
        }

        if (Directory.Exists(normalizedRoot))
        {
            Directory.Delete(normalizedRoot, recursive: true);
        }
    }
}
