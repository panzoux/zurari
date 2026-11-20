internal sealed record DirectoryColumnSnapshot(List<string> Entries, int DirectoryCount);

internal sealed class DirectoryColumnLoad
{
    DirectoryColumnSnapshot? _initial;
    int _entriesRead;

    public Task<DirectoryColumnSnapshot> Completion { get; }
    public DirectoryColumnSnapshot? Initial => Volatile.Read(ref _initial);
    public int EntriesRead => Volatile.Read(ref _entriesRead);

    public DirectoryColumnLoad(string path, int initialSize, CancellationToken cancellationToken)
    {
        Completion = Task.Run(() => Read(path, initialSize, cancellationToken));
    }

    DirectoryColumnSnapshot Read(string path, int initialSize, CancellationToken cancellationToken)
    {
        var directories = new List<string>();
        var files = new List<string>();
        int publishedDirectories = 0;
        int publishedFiles = 0;

        ReadEntries(directories, isDirectory: true);
        if (!cancellationToken.IsCancellationRequested)
            ReadEntries(files, isDirectory: false);

        // Keep the initial entries at the same indices when the complete listing lands.
        directories.Sort(publishedDirectories, directories.Count - publishedDirectories, StringComparer.OrdinalIgnoreCase);
        files.Sort(publishedFiles, files.Count - publishedFiles, StringComparer.OrdinalIgnoreCase);
        var snapshot = BuildSnapshot();
        if (snapshot.Entries.Count == 0)
            snapshot.Entries.Add("<No file>");
        return snapshot;

        void ReadEntries(List<string> group, bool isDirectory)
        {
            try
            {
                var paths = isDirectory ? Directory.EnumerateDirectories(path) : Directory.EnumerateFiles(path);
                foreach (string entryPath in paths)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    string name = Path.GetFileName(entryPath);
                    group.Add(isDirectory ? name + "/" : name);
                    int count = directories.Count + files.Count;
                    Volatile.Write(ref _entriesRead, count);
                    if (count == initialSize)
                    {
                        directories.Sort(StringComparer.OrdinalIgnoreCase);
                        files.Sort(StringComparer.OrdinalIgnoreCase);
                        publishedDirectories = directories.Count;
                        publishedFiles = files.Count;
                        Volatile.Write(ref _initial, BuildSnapshot());
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }

        DirectoryColumnSnapshot BuildSnapshot()
        {
            var entries = new List<string>(directories.Count + files.Count);
            entries.AddRange(directories);
            entries.AddRange(files);
            return new DirectoryColumnSnapshot(entries, directories.Count);
        }
    }
}
