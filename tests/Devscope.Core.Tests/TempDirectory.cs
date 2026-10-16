using System.IO.Compression;

namespace Devscope.Core.Tests;

/// <summary>Disposable scratch folder plus helpers to build nested Java-style archives.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devscope-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Builds a zip in memory. Values are either strings (file content) or byte[] (e.g. a nested archive).</summary>
    public static byte[] Zip(IReadOnlyDictionary<string, object> entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                var bytes = content switch
                {
                    string text => System.Text.Encoding.UTF8.GetBytes(text),
                    byte[] raw => raw,
                    _ => throw new ArgumentException("Unsupported content", nameof(entries)),
                };
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    public string WriteFile(string relativePath, byte[] content)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string WriteFile(string relativePath, string content) =>
        WriteFile(relativePath, System.Text.Encoding.UTF8.GetBytes(content));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; temp folder is cleaned by the OS eventually.
        }
    }
}
