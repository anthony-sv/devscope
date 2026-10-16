using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Devscope.Core.Workspaces;

/// <summary>Loads and saves workspaces.</summary>
public interface IWorkspaceStore
{
    Task<IReadOnlyList<Workspace>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(IReadOnlyList<Workspace> workspaces, CancellationToken cancellationToken);
}

/// <summary>Stores workspaces in a JSON file (by default under <c>%APPDATA%\Devscope</c>).</summary>
public sealed partial class JsonWorkspaceStore(string filePath, ILogger<JsonWorkspaceStore> logger) : IWorkspaceStore
{
    /// <summary>Default location of the workspace file.</summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Devscope", "workspaces.json");

    /// <inheritdoc />
    public async Task<IReadOnlyList<Workspace>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            var document = await JsonSerializer.DeserializeAsync(stream, WorkspaceJsonContext.Default.WorkspaceDocument, cancellationToken)
                .ConfigureAwait(false);
            return document?.Workspaces ?? [];
        }
        catch (JsonException ex)
        {
            // Keep the broken file for the user to inspect instead of silently overwriting it later.
            var backup = filePath + ".corrupt";
            File.Copy(filePath, backup, overwrite: true);
            LogCorrupt(ex, filePath, backup);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(IReadOnlyList<Workspace> workspaces, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        // Write to a temp file then swap, so a crash mid-write never loses the existing workspaces.
        var temp = filePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            var document = new WorkspaceDocument { Workspaces = [.. workspaces] };
            await JsonSerializer.SerializeAsync(stream, document, WorkspaceJsonContext.Default.WorkspaceDocument, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temp, filePath, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Workspace file {Path} is corrupt; a copy was kept at {Backup}")]
    private partial void LogCorrupt(Exception exception, string path, string backup);
}

/// <summary>On-disk shape; versioned so the format can evolve.</summary>
internal sealed class WorkspaceDocument
{
    public int Version { get; set; } = 1;

    public List<Workspace> Workspaces { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkspaceDocument))]
internal sealed partial class WorkspaceJsonContext : JsonSerializerContext;
