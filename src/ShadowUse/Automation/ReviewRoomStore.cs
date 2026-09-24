// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

public sealed record ReviewAnnotation(string AnnotationId, string ActorId, string TargetRef, string Text, DateTimeOffset CreatedAt, bool Deleted);

/// <summary>F60-R01: a lightweight room membership -- an actor's auth_token is established once
/// at join and must match on every later call from that actor_id in this room (a shared-secret
/// per actor, not a real identity provider; honestly scoped the same way this project scopes every
/// other feature that would otherwise need external infrastructure). "Minimum necessary persisted
/// data": nothing here is written to disk -- the whole room is process-lifetime, in-memory.</summary>
public sealed class ReviewRoom
{
    public required string RoomId { get; init; }
    private readonly ConcurrentDictionary<string, string> _memberTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _presence = new(StringComparer.Ordinal);
    private readonly List<ReviewAnnotation> _annotations = [];
    private readonly object _annotationsLock = new();
    public string? LeaseHolderActorId { get; private set; }

    public bool TryJoin(string actorId, string authToken, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(authToken))
        {
            error = "actor_id and auth_token are both required.";
            return false;
        }
        var accepted = _memberTokens.AddOrUpdate(actorId, authToken, (_, existing) => existing);
        if (!string.Equals(accepted, authToken, StringComparison.Ordinal))
        {
            error = $"Actor '{actorId}' already joined this room with a different auth_token.";
            return false;
        }
        _presence[actorId] = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>F60-R01: the only gate before ANY room operation -- an actor must have joined with
    /// this exact token. Refuses cleanly for an unknown actor or a wrong token; never guesses.</summary>
    public bool IsAuthenticated(string actorId, string authToken) =>
        _memberTokens.TryGetValue(actorId, out var stored) && string.Equals(stored, authToken, StringComparison.Ordinal);

    public IReadOnlyList<(string ActorId, DateTimeOffset LastSeen)> Presence() =>
        _presence.Select(kv => (kv.Key, kv.Value)).ToArray();

    public ReviewAnnotation PostAnnotation(string actorId, string targetRef, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Annotation text must not be empty.");
        var annotation = new ReviewAnnotation(Guid.NewGuid().ToString("N"), actorId, targetRef, text, DateTimeOffset.UtcNow, Deleted: false);
        lock (_annotationsLock) _annotations.Add(annotation);
        _presence[actorId] = DateTimeOffset.UtcNow;
        return annotation;
    }

    /// <summary>F60-T01: every member reads the SAME list -- there is no per-actor filtering or
    /// caching, so two reviewers see identical annotations by construction.</summary>
    public IReadOnlyList<ReviewAnnotation> ListAnnotations()
    {
        lock (_annotationsLock) return _annotations.Where(a => !a.Deleted).ToArray();
    }

    /// <summary>F60-R02/T02: soft-deletes exactly one annotation record. This method has NO
    /// awareness of WorkflowRunner/ExperienceStore/RunTimeline/any executed action at all -- there
    /// is no code path here that could touch them even accidentally.</summary>
    public bool UndoAnnotation(string annotationId)
    {
        lock (_annotationsLock)
        {
            var index = _annotations.FindIndex(a => a.AnnotationId == annotationId && !a.Deleted);
            if (index < 0) return false;
            _annotations[index] = _annotations[index] with { Deleted = true };
            return true;
        }
    }

    /// <summary>F60-T01: only the current holder (or nobody) may acquire -- never stolen out from
    /// under an existing holder.</summary>
    public bool TryAcquireLease(string actorId)
    {
        if (LeaseHolderActorId is null || LeaseHolderActorId == actorId)
        {
            LeaseHolderActorId = actorId;
            return true;
        }
        return false;
    }

    public bool ReleaseLease(string actorId)
    {
        if (LeaseHolderActorId != actorId) return false;
        LeaseHolderActorId = null;
        return true;
    }

    public bool HoldsLease(string actorId) => LeaseHolderActorId == actorId;
}

/// <summary>F60-R01: a process-lifetime collaboration-provider interface -- optional shared
/// review rooms with annotations and presence. Kept entirely separate from DCU's own execution
/// state (WorkflowRunner/ExperienceStore/RunTimeline never appear anywhere in this file).</summary>
public sealed class ReviewRoomStore
{
    private readonly ConcurrentDictionary<string, ReviewRoom> _rooms = new();

    public ReviewRoom GetOrCreate(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            throw new ArgumentException("room_id is required.");
        return _rooms.GetOrAdd(roomId, id => new ReviewRoom { RoomId = id });
    }

    public ReviewRoom? Find(string roomId) => _rooms.GetValueOrDefault(roomId);
}
