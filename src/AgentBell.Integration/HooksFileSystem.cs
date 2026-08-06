namespace AgentBell.Integration;

/// <summary>Provides the narrow file operations needed for atomic hooks.json updates.</summary>
internal class HooksFileSystem
{
    internal virtual bool FileExists(string path) => File.Exists(path);

    internal virtual bool DirectoryExists(string path) => Directory.Exists(path);

    internal virtual IEnumerable<string> EnumerateFiles(string path, string searchPattern) =>
        Directory.EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly);

    internal virtual long GetFileLength(string path) => new FileInfo(path).Length;

    internal virtual Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(path, cancellationToken);

    internal virtual void CreateDirectory(string path) => Directory.CreateDirectory(path);

    internal virtual void CopyFile(string source, string destination, bool overwrite) =>
        File.Copy(source, destination, overwrite);

    internal virtual FileStream CreateWriteThroughFile(string path) =>
        new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);

    internal virtual void ReplaceFile(string source, string destination) =>
        File.Replace(source, destination, null, ignoreMetadataErrors: true);

    internal virtual void MoveFile(string source, string destination) => File.Move(source, destination);

    internal virtual void DeleteFile(string path) => File.Delete(path);
}
