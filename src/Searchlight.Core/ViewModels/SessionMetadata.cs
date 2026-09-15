using System.Globalization;
using System.Windows.Input;
using Searchlight.Models;

namespace Searchlight.ViewModels;

public sealed record SessionMetadataField(string Property, string Label, string Value, ICommand? CopyCommand = null)
{
    public bool CanCopy => CopyCommand is not null;
}
public sealed record SessionMetadataGroup(string Title, string Source, IReadOnlyList<SessionMetadataField> Fields);

/// <summary>Explicit display allowlist of parsed native Copilot metadata; no I/O or reflection.</summary>
public static class SessionMetadata
{
    public static IReadOnlyList<SessionMetadataGroup> Create(SessionInfo? session, ICommand? copyIdCommand = null)
    {
        if (session is null) return [];
        WorkspaceMetadata? workspace = session.Workspace;
        SessionStartInfo? start = session.Start;

        // ASSUMPTION: optional fields vary by client/version. Missing values are unknown,
        // not false/zero; filesystem flags are meaningful only after summary enrichment.
        return
        [
            // ASSUMPTION: the overview restores the primary work context; raw source
            // metadata stays available below it without repeating the prompt previews.
            new("Overview", "Dates from workspace.yaml; model, reasoning, and prompts from bounded event previews.",
            [
                // ASSUMPTION: copying uses the existing selected-session command, not
                // a separate clipboard path or a captured ID that could become stale.
                new(nameof(session.Id), "Session ID", session.Id, copyIdCommand),
                Field(nameof(session.Cwd), "Folder", session.Cwd),
                Field(nameof(session.Branch), "Branch", session.Branch),
                Field(nameof(session.Model), "Model", session.Model),
                Field(nameof(session.ReasoningEffort), "Reasoning", session.ReasoningEffort),
                Field(nameof(session.CopilotVersion), "Version", session.CopilotVersion),
                // Preserve unknown workspace dates instead of substituting filesystem timestamps.
                Field(nameof(WorkspaceMetadata.CreatedAt), "Created", workspace?.CreatedAt),
                Field(nameof(WorkspaceMetadata.UpdatedAt), "Updated", workspace?.UpdatedAt),
                Field(nameof(SessionStartInfo.FirstUserPrompt), "First prompt", start?.FirstUserPrompt),
                Field(nameof(SessionStartInfo.LastUserPrompt), "Last prompt", start?.LastUserPrompt),
            ]),
            new("Workspace metadata", "workspace.yaml; optional fields depend on the Copilot client/version.",
            [
                Field(nameof(WorkspaceMetadata.Id), "Workspace ID", workspace?.Id),
                Field(nameof(WorkspaceMetadata.Name), "Recorded name", workspace?.Name),
                Field(nameof(WorkspaceMetadata.UserNamed), "User-named", workspace?.UserNamed),
                Field(nameof(WorkspaceMetadata.ClientName), "Client", workspace?.ClientName),
                Field(nameof(WorkspaceMetadata.Cwd), "Working directory", workspace?.Cwd),
                Field(nameof(WorkspaceMetadata.GitRoot), "Git root", workspace?.GitRoot),
                Field(nameof(WorkspaceMetadata.Repository), "Repository", workspace?.Repository),
                Field(nameof(WorkspaceMetadata.HostType), "Repository host type", workspace?.HostType),
                Field(nameof(WorkspaceMetadata.Branch), "Branch", workspace?.Branch),
                Field(nameof(WorkspaceMetadata.SummaryCount), "Recorded summaries", workspace?.SummaryCount),
                Field(nameof(WorkspaceMetadata.RemoteSteerable), "Remotely steerable", workspace?.RemoteSteerable),
                Field(nameof(WorkspaceMetadata.McTaskId), "Mission Control task ID", workspace?.McTaskId),
                Field(nameof(WorkspaceMetadata.McSessionId), "Mission Control session ID", workspace?.McSessionId),
            ]),
            new("Event metadata", "events.jsonl start event and bounded head preview; not live runtime state.",
            [
                Field(nameof(SessionStartInfo.StartTime), "Recorded start time", start?.StartTime),
                Field(nameof(SessionStartInfo.CopilotVersion), "Copilot version", start?.CopilotVersion),
                Field(nameof(SessionStartInfo.Producer), "Event producer", start?.Producer),
                Field(nameof(SessionStartInfo.Model), "Model (head preview)", start?.Model),
                Field(nameof(SessionStartInfo.ReasoningEffort), "Reasoning (head preview)", start?.ReasoningEffort),
                Field(nameof(SessionStartInfo.ContextTier), "Context tier at start", start?.ContextTier),
                Field(nameof(SessionStartInfo.Cwd), "Working directory at start", start?.Cwd),
                Field(nameof(SessionStartInfo.GitRoot), "Git root at start", start?.GitRoot),
                Field(nameof(SessionStartInfo.Repository), "Repository at start", start?.Repository),
                Field(nameof(SessionStartInfo.Branch), "Branch at start", start?.Branch),
                Field(nameof(SessionStartInfo.AlreadyInUse), "Already in use at start", start?.AlreadyInUse),
            ]),
            new("Session storage", "Native session-state folder; kind is inferred from the folder name.",
            [
                Field(nameof(session.FolderName), "Folder name", session.FolderName),
                Field(nameof(session.FolderPath), "Session data folder", session.FolderPath),
                Field(nameof(session.Kind), "Kind (inferred)", session.Kind),
                Field(nameof(session.LastWriteTime), "Folder last modified", session.LastWriteTime),
                Field(nameof(session.IsInUse), "In-use lock present", session.IsEnriched ? session.IsInUse : null),
                Field(nameof(session.HasPlan), "Plan file present", session.IsEnriched ? session.HasPlan : null),
                Field(nameof(session.HasEvents), "Event log present", session.IsEnriched ? session.HasEvents : null),
                Field(nameof(session.HasSessionDb), "Session database present", session.IsEnriched ? session.HasSessionDb : null),
                Field(nameof(session.HasCheckpoints), "Checkpoint folder has entries", session.IsEnriched ? session.HasCheckpoints : null),
            ]),
        ];
    }

    private static SessionMetadataField Field(string property, string label, object? value) =>
        new(property, label, value switch
        {
            null => "\u2014",
            string text when string.IsNullOrWhiteSpace(text) => "\u2014",
            bool flag => flag ? "Yes" : "No",
            DateTimeOffset timestamp => timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "\u2014",
        });
}
