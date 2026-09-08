using Searchlight.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Searchlight.Services;

/// <summary>
/// Reads and parses a session's <c>workspace.yaml</c> into
/// <see cref="WorkspaceMetadata"/>. Read-only and null-safe: a missing or
/// malformed file yields <c>null</c> rather than throwing.
/// </summary>
public sealed class WorkspaceYamlReader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses the <c>workspace.yaml</c> in <paramref name="folderPath"/>, or
    /// returns <c>null</c> when it is absent or cannot be read.
    /// </summary>
    public WorkspaceMetadata? Read(string folderPath)
    {
        string path = CopilotPaths.WorkspaceYaml(folderPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // Workspace files are small summaries, not transcripts. Read outside
            // the parser lock so bounded folder readers can overlap filesystem I/O;
            // never use the shared YamlDotNet deserializer concurrently.
            string yaml = File.ReadAllText(path);
            Dto? dto;
            lock (Deserializer)
            {
                dto = Deserializer.Deserialize<Dto>(yaml);
            }
            if (dto is null)
            {
                return null;
            }

            return new WorkspaceMetadata
            {
                Id = dto.Id,
                Cwd = dto.Cwd,
                ClientName = dto.ClientName,
                Name = dto.Name,
                UserNamed = dto.UserNamed,
                SummaryCount = dto.SummaryCount,
                CreatedAt = ParseTimestamp(dto.CreatedAt),
                UpdatedAt = ParseTimestamp(dto.UpdatedAt),
                RemoteSteerable = dto.RemoteSteerable,
                McTaskId = dto.McTaskId,
                McSessionId = dto.McSessionId,
            };
        }
        catch (Exception)
        {
            // Best-effort: a partially-written or malformed file is treated as absent.
            return null;
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : null;

    /// <summary>YamlDotNet target using underscored naming convention.</summary>
    private sealed class Dto
    {
        public string? Id { get; set; }
        public string? Cwd { get; set; }
        public string? ClientName { get; set; }
        public string? Name { get; set; }
        public bool UserNamed { get; set; }
        public int SummaryCount { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
        public bool RemoteSteerable { get; set; }
        public string? McTaskId { get; set; }
        public string? McSessionId { get; set; }
    }
}
