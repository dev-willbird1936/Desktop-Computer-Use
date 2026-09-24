// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

public enum VoiceTurnStatus { Listening, Finalized, Stopped, Failed }

/// <summary>F58-R01/R02: DCU has no microphone/speech-recognition pipeline of its own in this
/// environment (same documented no-live-external-service constraint as F24/F27/F46/F47/F55/F57)
/// -- the caller's own speech-recognition adapter feeds partial/final transcripts in, tagged with
/// a caller-assigned generation_id per utterance. This controller owns only the deterministic
/// turn-taking state machine: which transcript is current, when an utterance is genuinely
/// complete (never inferred from a bare partial, however long its pause), and how Stop cancels
/// anything still in flight for that session. A finalized transcript hands off to SubgoalStore
/// via the exact same Define() typed goals already use (F58-R01's "same planner/controller path"),
/// never a separate voice-only execution route.</summary>
public sealed class VoiceTurnSession
{
    public required string SessionId { get; init; }
    public required string FallbackBehavior { get; init; }
    public VoiceTurnStatus Status { get; set; } = VoiceTurnStatus.Listening;
    public int? LatestGenerationId { get; set; }
    public string? LatestPartialText { get; set; }
    public string? FinalTranscript { get; set; }
    public string? SubgoalId { get; set; }
    public string? FailureReason { get; set; }
}

public sealed class VoiceTurnController
{
    private readonly ConcurrentDictionary<string, VoiceTurnSession> _sessions = new();
    private readonly SubgoalStore _subgoals;

    public VoiceTurnController(SubgoalStore subgoals) => _subgoals = subgoals;

    public VoiceTurnSession OpenSession(string sessionId, string fallbackBehavior)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("session_id is required.");
        if (string.IsNullOrWhiteSpace(fallbackBehavior))
            throw new ArgumentException("fallback_behavior is required (declared up front, F58-T02).");
        if (!_sessions.TryAdd(sessionId, new VoiceTurnSession { SessionId = sessionId, FallbackBehavior = fallbackBehavior }))
            throw new ArgumentException($"Voice session '{sessionId}' already exists.");
        return _sessions[sessionId];
    }

    public VoiceTurnSession Find(string sessionId) =>
        _sessions.GetValueOrDefault(sessionId) ?? throw new ArgumentException($"Voice session '{sessionId}' was not found.");

    /// <summary>F58-T01: a partial transcript never dispatches or finalizes anything by itself --
    /// only the most recent generation_id/text is retained. A generation_id at or behind the one
    /// already recorded is a stale, out-of-order response and is rejected outright.</summary>
    public (bool Accepted, string? Reason) SubmitPartial(string sessionId, int generationId, string text)
    {
        var session = Find(sessionId);
        if (session.Status != VoiceTurnStatus.Listening)
            return (false, $"session_is_{session.Status.ToString().ToLowerInvariant()}");
        if (session.LatestGenerationId is { } latest && generationId <= latest)
            return (false, "stale_generation_id");
        session.LatestGenerationId = generationId;
        session.LatestPartialText = text;
        return (true, null);
    }

    /// <summary>F58-R01/T01: only an explicit is_final transcript from the caller's own
    /// speech-recognition adapter completes an utterance -- hesitation, however long, never does.
    /// A stale/stopped/already-finalized session refuses rather than silently re-finalizing.</summary>
    public (bool Finalized, string? Reason, string? SubgoalId) SubmitFinal(string sessionId, int generationId, string text)
    {
        var session = Find(sessionId);
        if (session.Status != VoiceTurnStatus.Listening)
            return (false, $"session_is_{session.Status.ToString().ToLowerInvariant()}", null);
        if (session.LatestGenerationId is { } latest && generationId < latest)
            return (false, "stale_generation_id", null);
        if (string.IsNullOrWhiteSpace(text))
            return (false, "empty_transcript", null);
        return (true, null, CompleteWith(session, text));
    }

    /// <summary>F58-R02: bounded fallback timing as a SEPARATE, independently testable trigger
    /// from SubmitFinal -- a caller whose adapter never emits an explicit final signal can instead
    /// report elapsed silence since the last partial; only crossing the caller-chosen threshold
    /// finalizes, so the exact bound is proven through a test rather than copied from a demo.</summary>
    public (bool Finalized, string? Reason, string? SubgoalId) CheckSilenceTimeout(string sessionId, int silenceMsSinceLastPartial, int silenceThresholdMs)
    {
        var session = Find(sessionId);
        if (session.Status != VoiceTurnStatus.Listening)
            return (false, $"session_is_{session.Status.ToString().ToLowerInvariant()}", null);
        if (silenceMsSinceLastPartial < silenceThresholdMs)
            return (false, "below_silence_threshold", null);
        if (string.IsNullOrWhiteSpace(session.LatestPartialText))
            return (false, "no_transcript_to_finalize", null);
        return (true, null, CompleteWith(session, session.LatestPartialText));
    }

    private string CompleteWith(VoiceTurnSession session, string text)
    {
        session.FinalTranscript = text;
        session.Status = VoiceTurnStatus.Finalized;
        var subgoal = _subgoals.Define($"voice-{session.SessionId}", text, [], null, null, null);
        session.SubgoalId = subgoal.SubgoalId;
        return subgoal.SubgoalId;
    }

    /// <summary>F58-T02: Stop is terminal and distinct from a new request -- it marks THIS
    /// session's future activity refused (any late-arriving partial/final/timeout call for it is
    /// rejected by the Status guard above), while a brand new session_id is entirely unaffected.</summary>
    public void Stop(string sessionId)
    {
        var session = Find(sessionId);
        if (session.Status == VoiceTurnStatus.Listening)
            session.Status = VoiceTurnStatus.Stopped;
    }

    /// <summary>F58-T02: an API failure falls back to the behavior declared at OpenSession time,
    /// never a hardcoded default -- the caller decides whether failure means resume listening or
    /// cancel, DCU only records which one applies and reports it back.</summary>
    public string ReportTranscriptionFailure(string sessionId, string reason)
    {
        var session = Find(sessionId);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required.");
        if (session.Status == VoiceTurnStatus.Listening)
        {
            session.Status = VoiceTurnStatus.Failed;
            session.FailureReason = reason;
        }
        return session.FallbackBehavior;
    }
}
