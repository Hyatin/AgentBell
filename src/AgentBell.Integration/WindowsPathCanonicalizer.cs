using System.Runtime.InteropServices;
using System.Text;

namespace AgentBell.Integration;

/// <summary>Normalizes Windows paths, including existing 8.3 path segments, for stable comparisons.</summary>
internal static class WindowsPathCanonicalizer
{
    internal static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        var suffix = new Stack<string>();
        var existingPath = fullPath;
        while (!File.Exists(existingPath) && !Directory.Exists(existingPath))
        {
            var name = Path.GetFileName(existingPath);
            var parent = Path.GetDirectoryName(existingPath);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parent))
            {
                return fullPath;
            }

            suffix.Push(name);
            existingPath = parent;
        }

        var canonical = TryExpandLongPath(existingPath) ?? existingPath;
        while (suffix.Count > 0)
        {
            canonical = Path.Combine(canonical, suffix.Pop());
        }

        return Path.GetFullPath(canonical);
    }

    internal static bool AreEquivalent(string first, string second)
    {
        try
        {
            return string.Equals(
                Canonicalize(first),
                Canonicalize(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static string? TryExpandLongPath(string path)
    {
        var required = GetLongPathName(path, null, 0);
        if (required == 0)
        {
            return null;
        }

        var buffer = new StringBuilder(checked((int)required));
        var written = GetLongPathName(path, buffer, required);
        return written == 0 || written >= required ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder? longPath, uint bufferLength);
}
