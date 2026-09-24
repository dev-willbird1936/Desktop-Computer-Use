// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

/// <summary>F56: a workspace member is a pure typed REFERENCE, never an action. There is
/// deliberately no tool/args slot anywhere in this type (unlike HelperStore's WorkflowNodeSpec
/// recipes) -- a member can only ever be "which app" or "which URL", so a suggestion (semantic or
/// otherwise) has no field through which it could ever encode a purchase/delete/shell-command
/// action (F56-T02). Only two kinds exist; anything else is rejected at save time.</summary>
public enum WorkspaceMemberKind { NativeApp, Url }

public sealed record WorkspaceMember(WorkspaceMemberKind Kind, string Reference);

public sealed class WorkspaceRecord
{
    public required string WorkspaceId { get; init; }
    public required IReadOnlyList<WorkspaceMember> Members { get; init; }
    public required DateTimeOffset SavedAt { get; init; }
}

/// <summary>F56-R01/R02: a process-lifetime store of named, ordered, typed-reference workspaces.
/// Saving always replaces membership with exactly what the caller supplied, in the order supplied
/// -- there is no live semantic ranking in this environment to preserve manual edits across (no
/// model API key, same documented constraint as F46/F47/F55), so "preserve manual edits" holds
/// trivially: manual input IS the only input.</summary>
public sealed class WorkspaceStore
{
    private readonly ConcurrentDictionary<string, WorkspaceRecord> _workspaces = new();

    public WorkspaceRecord Save(string workspaceId, IReadOnlyList<WorkspaceMember> members)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("workspace_id is required.");
        if (members.Count == 0)
            throw new ArgumentException("A workspace needs at least one member.");
        foreach (var member in members)
            if (string.IsNullOrWhiteSpace(member.Reference))
                throw new ArgumentException("Every workspace member needs a non-empty reference.");
        var record = new WorkspaceRecord { WorkspaceId = workspaceId, Members = members, SavedAt = DateTimeOffset.UtcNow };
        _workspaces[workspaceId] = record;
        return record;
    }

    public WorkspaceRecord? Find(string workspaceId) => _workspaces.GetValueOrDefault(workspaceId);

    public static WorkspaceMemberKind ParseKind(string kind) => kind switch
    {
        "native_app" => WorkspaceMemberKind.NativeApp,
        "url" => WorkspaceMemberKind.Url,
        _ => throw new ArgumentException($"Unknown workspace member kind '{kind}'. Only 'native_app' and 'url' are supported -- a workspace member is a reference, never an action."),
    };
}
