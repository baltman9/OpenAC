namespace AcDream.Headless.Tests;

/// <summary>
/// Reads a session's status file while the session may still be running.
/// A live session holds its status file open for its whole lifetime, so
/// the ordinary <see cref="File.ReadAllText(string)"/> -- which asks for
/// the file to stay unwritten while it reads -- is refused. Sharing the
/// file for writing and deletion is how anything watches a running
/// session: a launcher, a person tailing it, or a test.
/// </summary>
internal static class LiveStatusFile
{
    internal static string ReadAllText(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    internal static string[] ReadAllLines(string path) =>
        ReadAllText(path)
            .Split('\n')
            .Select(static line => line.TrimEnd('\r'))
            .Where(static line => line.Length > 0)
            .ToArray();
}
