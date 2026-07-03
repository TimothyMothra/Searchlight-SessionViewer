using Searchlight.Models;

namespace Searchlight.Services;

/// <summary>
/// Live <see cref="ISessionDataSource"/> backed by the user's real
/// <c>~/.claude/projects</c> tree (Claude Code). The bulk <see cref="LoadAll"/>
/// pass is cheap: per-project <c>sessions-index.json</c> entries, merged with any
/// un-indexed <c>&lt;uuid&gt;.jsonl</c> transcripts found on disk (sessions newer
/// than the last index write). Heavy per-session transcript head-parsing (model,
/// CLI version) is deferred to <see cref="EnrichWithEvents"/>. All access is
/// read-only. Checkpoints, status snapshots, and todos have no Claude Code
/// equivalent yet and return empty.
/// </summary>
public sealed class ClaudeSessionDataSource : ISessionDataSource
{
    /// <summary>Client identifier stamped into <see cref="WorkspaceMetadata.ClientName"/>.</summary>
    public const string ClientName = "claude/code";

    private readonly ClaudeSessionIndexReader _indexReader;
    private readonly ClaudeJsonlHeadReader _headReader;
    private readonly string _projectsRoot;
    private readonly object _gate = new();

    // Null values are cached deliberately: "known session, unknown workspace"
    // must not re-trigger a full store scan on every lookup.
    private Dictionary<string, string?> _cwdBySessionId = new(StringComparer.OrdinalIgnoreCase);

    // Parsed transcript heads keyed by session id + file length (see
    // EnrichWithEvents); avoids re-scanning unchanged transcripts.
    private readonly Dictionary<string, (long Length, ClaudeHeadInfo Head)> _headCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the data source over the given readers.
    /// <paramref name="projectsRoot"/> overrides <c>~/.claude/projects</c> (tests).
    /// </summary>
    public ClaudeSessionDataSource(
        ClaudeSessionIndexReader indexReader,
        ClaudeJsonlHeadReader headReader,
        string? projectsRoot = null)
    {
        _indexReader = indexReader;
        _headReader = headReader;
        _projectsRoot = projectsRoot ?? ClaudePaths.Projects;
    }

    /// <inheritdoc />
    /// <remarks>The returned list is ordered newest-first by folder write time.</remarks>
    public IReadOnlyList<SessionInfo> LoadAll()
    {
        if (!Directory.Exists(_projectsRoot))
        {
            return [];
        }

        var sessions = new List<SessionInfo>();
        var cwdMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (string projectDir in SafeEnumerateDirectories(_projectsRoot))
        {
            LoadProject(projectDir, sessions, cwdMap);
        }

        lock (_gate)
        {
            _cwdBySessionId = cwdMap;
        }

        EagerlyNameRecentSessions(sessions);

        return sessions;
    }

    /// <summary>
    /// Bounded upper limit on transcript head-parses performed during a bulk
    /// load, purely to give un-indexed sessions their friendly name up front.
    /// </summary>
    public const int MaxEagerEnrichments = 50;

    /// <summary>
    /// Sessions missing from every index have no display name and would show a
    /// raw UUID at the top of the list (they're typically the newest). Head-parse
    /// just those, newest first, capped so a huge un-indexed store can't stall
    /// the bulk load.
    /// </summary>
    private void EagerlyNameRecentSessions(List<SessionInfo> sessions)
    {
        sessions.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));

        int enriched = 0;
        for (int i = 0; i < sessions.Count && enriched < MaxEagerEnrichments; i++)
        {
            if (string.IsNullOrWhiteSpace(sessions[i].Workspace?.Name))
            {
                sessions[i] = EnrichWithEvents(sessions[i]);
                enriched++;
            }
        }
    }

    /// <inheritdoc />
    public SessionInfo EnrichWithEvents(SessionInfo session)
    {
        // Model is only ever discovered by head-parsing; its presence marks the
        // transcript as already parsed (index-seeded Start records have no model).
        if (session.Start?.Model is not null)
        {
            return session;
        }

        string jsonlPath = ClaudePaths.SessionJsonl(session.FolderPath, session.Id);

        // Active sessions may legitimately have no model yet (no assistant
        // message), which would re-scan the transcript on every selection.
        // Cache the parsed head keyed by file length: re-parse only when the
        // transcript has actually grown.
        long length = SafeFileLength(jsonlPath);
        ClaudeHeadInfo? head;
        lock (_gate)
        {
            if (_headCache.TryGetValue(session.Id, out (long Length, ClaudeHeadInfo Head) cached)
                && cached.Length == length)
            {
                head = cached.Head;
            }
            else
            {
                head = null;
            }
        }

        head ??= _headReader.Read(jsonlPath);
        if (head is null)
        {
            return session;
        }

        lock (_gate)
        {
            _headCache[session.Id] = (length, head);
        }

        // Sessions in an undecodable store folder learn their workspace here —
        // keep resume working for them too (a cached null is upgraded).
        if (head.Cwd is not null)
        {
            lock (_gate)
            {
                if (!_cwdBySessionId.TryGetValue(session.Id, out string? known) || known is null)
                {
                    _cwdBySessionId[session.Id] = head.Cwd;
                }
            }
        }

        var start = new SessionStartInfo
        {
            Producer = "claude-code",
            CopilotVersion = head.Version,
            StartTime = session.Start?.StartTime ?? head.StartTime,
            Cwd = session.Start?.Cwd ?? head.Cwd,
            Model = head.Model,
            FirstUserPrompt = session.Start?.FirstUserPrompt ?? head.FirstUserPrompt,
        };

        // Prefer the transcript's AI-generated title when the index gave no
        // summary — it's what Claude Code's own resume picker shows. Brand-new
        // sessions have no title yet, so fall back to the first prompt (the
        // same chain the index path uses) rather than showing a raw GUID.
        WorkspaceMetadata? workspace = session.Workspace;
        string? name = head.AiTitle ?? Truncate(head.FirstUserPrompt, 80);
        if (name is not null && string.IsNullOrWhiteSpace(workspace?.Name))
        {
            workspace = (workspace ?? new WorkspaceMetadata { Id = session.Id })
                with { Name = name };
        }

        return session with
        {
            Start = start,
            Branch = session.Branch ?? head.GitBranch,
            Workspace = workspace,
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<CheckpointInfo> ReadCheckpoints(SessionInfo session) => [];

    /// <inheritdoc />
    public IReadOnlyList<SnapshotInfo> LoadSnapshots(string sessionId) => [];

    /// <inheritdoc />
    public IReadOnlyList<SessionTodo> ReadTodos(SessionInfo session) => [];

    /// <summary>
    /// Resolves the workspace directory a session was recorded against, for
    /// resume launchers that must <c>cd</c> there before <c>claude --resume</c>.
    /// Cache-only — callers run on the UI thread, so an unknown id returns null
    /// (resume without a <c>cd</c>) rather than triggering a synchronous store
    /// rescan. The cache is populated by <see cref="LoadAll"/>, which always
    /// precedes any resume in the UI flow.
    /// </summary>
    public string? TryGetProjectCwd(string sessionId)
    {
        lock (_gate)
        {
            return _cwdBySessionId.TryGetValue(sessionId, out string? cwd) ? cwd : null;
        }
    }

    /// <summary>
    /// True when the given session id was loaded from the Claude Code store.
    /// Distinct from <see cref="TryGetProjectCwd"/> returning null, which also
    /// happens for known sessions whose workspace could not be resolved. Used
    /// by hosts that show the combined Claude + Copilot list to route a resume
    /// to the right CLI. Cache-only, like <see cref="TryGetProjectCwd"/>.
    /// </summary>
    public bool OwnsSession(string sessionId)
    {
        lock (_gate)
        {
            return _cwdBySessionId.ContainsKey(sessionId);
        }
    }

    private void LoadProject(
        string projectDir, List<SessionInfo> sessions, Dictionary<string, string?> cwdMap)
    {
        IReadOnlyList<ClaudeSessionEntry> indexed = _indexReader.Read(projectDir);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Workspace path: the index records it verbatim; otherwise best-effort
        // decode of the folder name. The store folder itself is never a usable
        // cwd — when neither source resolves, the workspace stays null and
        // EnrichWithEvents recovers it from the transcript head.
        string? projectPath =
            indexed.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.ProjectPath))?.ProjectPath
            ?? ClaudeProjectDirName.TryDecode(Path.GetFileName(projectDir));

        foreach (ClaudeSessionEntry entry in indexed)
        {
            seen.Add(entry.SessionId);
            string? cwd = entry.ProjectPath ?? projectPath;
            cwdMap[entry.SessionId] = cwd;
            sessions.Add(FromIndexEntry(entry, projectDir, cwd));
        }

        // Sessions created since the index was last written exist only as .jsonl
        // files — surface them too, enriched lazily on selection.
        foreach (string jsonl in SafeEnumerateFiles(projectDir, "*.jsonl"))
        {
            string id = Path.GetFileNameWithoutExtension(jsonl);
            if (!seen.Add(id))
            {
                continue;
            }

            cwdMap[id] = projectPath;
            sessions.Add(FromJsonlFile(jsonl, id, projectDir, projectPath));
        }
    }

    private static SessionInfo FromIndexEntry(
        ClaudeSessionEntry entry, string projectDir, string? cwd)
    {
        DateTimeOffset modified = entry.Modified
            ?? SafeLastWriteTime(entry.FullPath)
            ?? DateTimeOffset.MinValue;

        return new SessionInfo
        {
            Id = entry.SessionId,
            FolderName = Path.GetFileName(projectDir),
            FolderPath = projectDir,
            Kind = entry.IsSidechain ? SessionKind.Chat : SessionKind.Project,
            LastWriteTime = modified,
            Workspace = new WorkspaceMetadata
            {
                Id = entry.SessionId,
                Cwd = cwd,
                ClientName = ClientName,
                // Un-summarized sessions would otherwise display a raw UUID.
                Name = entry.Summary ?? Truncate(entry.FirstPrompt, 80),
                CreatedAt = entry.Created,
                UpdatedAt = entry.Modified,
            },
            Start = string.IsNullOrWhiteSpace(entry.FirstPrompt)
                ? null
                : new SessionStartInfo
                {
                    Producer = "claude-code",
                    StartTime = entry.Created,
                    Cwd = cwd,
                    FirstUserPrompt = entry.FirstPrompt,
                },
            Branch = entry.GitBranch,
            JournalActivity = entry.MessageCount > 0
                ? $"{entry.MessageCount} messages"
                : null,
        };
    }

    private static SessionInfo FromJsonlFile(
        string jsonlPath, string id, string projectDir, string? cwd)
    {
        return new SessionInfo
        {
            Id = id,
            FolderName = Path.GetFileName(projectDir),
            FolderPath = projectDir,
            Kind = SessionKind.Project,
            LastWriteTime = SafeLastWriteTime(jsonlPath) ?? DateTimeOffset.MinValue,
            Workspace = new WorkspaceMetadata
            {
                Id = id,
                Cwd = cwd,
                ClientName = ClientName,
            },
        };
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static DateTimeOffset? SafeLastWriteTime(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern);
        }
        catch (Exception)
        {
            return [];
        }
    }
}

/// <summary>
/// Best-effort decoder for Claude Code project folder names, which encode the
/// workspace path with every separator (and some punctuation) replaced by
/// <c>-</c> (e.g. <c>-Users-jane-Source-my-app</c> for
/// <c>/Users/jane/Source/my-app</c>). The encoding is lossy — a dash may be a
/// path separator or a literal dash — so decoding walks the real filesystem,
/// preferring existing directories, and returns null when no candidate exists.
/// </summary>
public static class ClaudeProjectDirName
{
    /// <summary>Decodes <paramref name="folderName"/> to an existing path, or null.</summary>
    public static string? TryDecode(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName) || !folderName.StartsWith('-'))
        {
            return null;
        }

        string[] parts = folderName.TrimStart('-').Split('-');
        return Walk(Path.DirectorySeparatorChar.ToString(), parts, 0);
    }

    private static string? Walk(string current, string[] parts, int index)
    {
        if (index >= parts.Length)
        {
            return current;
        }

        // Greedily try the longest dash-joined segment first so literal dashes
        // in directory names ("my-app") win over deeper nesting ("my/app").
        for (int end = parts.Length; end > index; end--)
        {
            string segment = string.Join('-', parts[index..end]);
            string candidate = Path.Combine(current, segment);
            if (Directory.Exists(candidate))
            {
                string? result = Walk(candidate, parts, end);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }
}
