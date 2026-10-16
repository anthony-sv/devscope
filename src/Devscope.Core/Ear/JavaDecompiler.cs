using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Devscope.Core.Ear;

/// <summary>A usable Java runtime plus decompiler jar.</summary>
public sealed record DecompilerSetup(string JavaPath, string JarPath)
{
    public string Description => $"{System.IO.Path.GetFileNameWithoutExtension(JarPath)} on {JavaPath}";
}

/// <summary>Turns compiled classes back into Java source.</summary>
public interface IJavaDecompiler
{
    /// <summary>
    /// Decompiles <paramref name="mainClassFile"/>. <paramref name="classFiles"/> holds the main class and its
    /// inner classes (<c>Foo$Bar.class</c>) by file name, so nested types appear in the output.
    /// </summary>
    Task<string> DecompileAsync(DecompilerSetup setup, string mainClassFile, IReadOnlyDictionary<string, byte[]> classFiles, CancellationToken cancellationToken);
}

/// <summary>Finds java.exe and the decompiler jar from explicit settings or well-known locations.</summary>
public static class DecompilerLocator
{
    /// <summary>File name pattern of the supported decompiler.</summary>
    public const string JarPattern = "vineflower*.jar";

    /// <summary>Resolves a setup, or returns null with an explanation in <paramref name="message"/>.</summary>
    /// <param name="javaOverride">Explicit java.exe path; empty to search JAVA_HOME and PATH.</param>
    /// <param name="jarOverride">Explicit jar path; empty to search <paramref name="searchFolders"/>.</param>
    /// <param name="searchFolders">Folders searched for <see cref="JarPattern"/>, typically next to the exe.</param>
    /// <param name="message">Human-readable status.</param>
    public static DecompilerSetup? Locate(string? javaOverride, string? jarOverride, IEnumerable<string> searchFolders, out string message)
    {
        ArgumentNullException.ThrowIfNull(searchFolders);

        var java = FindJava(javaOverride);
        if (java is null)
        {
            message = string.IsNullOrWhiteSpace(javaOverride)
                ? "Java not found (looked at JAVA_HOME and PATH). Point to java.exe (Java 17+)."
                : $"java.exe not found at '{javaOverride}'.";
            return null;
        }

        var jar = FindJar(jarOverride, searchFolders);
        if (jar is null)
        {
            message = string.IsNullOrWhiteSpace(jarOverride)
                ? "Vineflower jar not found next to Devscope.exe or in its tools folder. Point to vineflower.jar."
                : $"Decompiler jar not found at '{jarOverride}'.";
            return null;
        }

        var setup = new DecompilerSetup(java, jar);
        message = $"Ready: {setup.Description}";
        return setup;
    }

    private static string? FindJava(string? javaOverride)
    {
        if (!string.IsNullOrWhiteSpace(javaOverride))
        {
            var path = Unquote(javaOverride);
            return File.Exists(path) ? path : null;
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var candidate = Path.Combine(Unquote(javaHome), "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Path.Combine(Unquote(dir), executable))
            .FirstOrDefault(File.Exists);
    }

    private static string? FindJar(string? jarOverride, IEnumerable<string> searchFolders)
    {
        if (!string.IsNullOrWhiteSpace(jarOverride))
        {
            var path = Unquote(jarOverride);
            return File.Exists(path) ? path : null;
        }

        return searchFolders
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, JarPattern))
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase) // newest version name first
            .FirstOrDefault();
    }

    private static string Unquote(string path) => path.Trim().Trim('"');
}

/// <summary>
/// Runs the Vineflower decompiler (<see href="https://vineflower.org"/>) as a local child process.
/// Class bytes are written to a private temp folder that is deleted afterwards; nothing leaves the machine.
/// </summary>
public sealed class VineflowerDecompiler : IJavaDecompiler
{
    /// <summary>Upper bound for one decompilation, so a pathological class cannot hang the UI.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public async Task<string> DecompileAsync(DecompilerSetup setup, string mainClassFile, IReadOnlyDictionary<string, byte[]> classFiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(classFiles);

        var work = Path.Combine(Path.GetTempPath(), "Devscope", "decompile", Guid.NewGuid().ToString("N"));
        var input = Directory.CreateDirectory(Path.Combine(work, "in")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(work, "out")).FullName;
        try
        {
            foreach (var (name, bytes) in classFiles)
            {
                // File names only: never let an archive entry name escape the temp folder.
                await File.WriteAllBytesAsync(Path.Combine(input, Path.GetFileName(name)), bytes, cancellationToken).ConfigureAwait(false);
            }

            var stderr = await RunAsync(setup, input, output, cancellationToken).ConfigureAwait(false);

            var expected = Path.GetFileNameWithoutExtension(mainClassFile) + ".java";
            var files = Directory.EnumerateFiles(output, "*.java", SearchOption.AllDirectories).ToList();
            var source = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), expected, StringComparison.Ordinal))
                ?? files.FirstOrDefault()
                ?? throw new InvalidOperationException($"The decompiler produced no output. {stderr}".Trim());

            return await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static async Task<string> RunAsync(DecompilerSetup setup, string input, string output, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(setup.JavaPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-jar", setup.JarPath, "--silent", "--folder", input, output })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start Java ({setup.JavaPath}): {ex.Message}", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        // Drain both pipes concurrently so a chatty process cannot block on a full buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"Decompiling took longer than {Timeout.TotalSeconds:N0}s and was stopped.");
        }

        await stdoutTask.ConfigureAwait(false);
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            var reason = stderr.Contains("UnsupportedClassVersionError", StringComparison.Ordinal)
                ? "Vineflower needs Java 17 or newer."
                : FirstLines(stderr, 6);
            throw new InvalidOperationException($"Decompiler exited with code {process.ExitCode}. {reason}".Trim());
        }

        return FirstLines(stderr, 6);
    }

    private static string FirstLines(string text, int count) =>
        string.Join(Environment.NewLine, text.Split('\n').Take(count).Select(l => l.TrimEnd('\r')));

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A virus scanner can briefly hold the files; the OS cleans %TEMP% eventually.
            Debug.WriteLine($"Could not delete {folder}: {ex.Message}");
        }
    }
}
