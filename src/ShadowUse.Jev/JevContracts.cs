// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
namespace ShadowUse.Jev;

/// <summary>
/// off: never calls Jev, no network dependency at all.
/// assist: Jev is available for judgment calls (e.g. goal-reached checks) but does not
/// have to drive every action.
/// fast_loop: Jev drives operation + target choice on the observe-decide-act path.
/// </summary>
public enum JevMode
{
    Off,
    Assist,
    FastLoop,
}

public sealed class JevOptions
{
    public JevMode Mode { get; init; } = JevMode.Off;
    public string Model { get; init; } = "jev-latest";
    public string BaseUrl { get; init; } = "https://api.typesafe.ai/v1/systemone";
    public string ApiKeyEnvironmentVariable { get; init; } = "TYPESAFE_API_KEY";
    public int TimeoutMs { get; init; } = 8_000;
    public int MaxRetries { get; init; } = 2;

    /// <summary>fast_loop only: below this operation-choice confidence, act tools return an
    /// escalation result instead of executing, per spec §6.7 ("close alternatives -> reobserve").</summary>
    public double MinExecuteConfidence { get; init; } = 0.6;

    /// <summary>When the top two target candidates for the chosen operation are within this
    /// probability margin, issue one follow-up Score call to rank them by match strength
    /// instead of trusting the near-tied Choice pick. Disabled by default because it adds a model round trip.</summary>
    public double ScoreDisambiguationMargin { get; init; } = 0;

    /// <summary>Cap on how many near-tied candidates are sent to the follow-up Score call.</summary>
    public int MaxScoreDisambiguationCandidates { get; init; } = 5;
}

/// <summary>A real, already-resolved candidate target. Not free-form model text.</summary>
public sealed record JevElement(
    string Id,
    string? Role,
    string? Name,
    string? CurrentValue,
    bool? Enabled,
    IReadOnlyList<string> Operations,
    IReadOnlyDictionary<string, object?>? Extra = null);

public sealed record JevControlOption(string Code, string Description);

public sealed record JevBoxQuestion(string TargetVisibleQuestionId, string TargetDescription);

public sealed record JevDecideRequest(
    string Goal,
    string ObservationId,
    IReadOnlyList<JevElement> Elements,
    IReadOnlyDictionary<string, string> OperationDescriptions,
    IReadOnlyList<JevControlOption>? ControlOptions = null,
    IReadOnlyList<string>? RecentActions = null,
    string? Context = null,
    bool AskGoalReached = true,
    string? ObservedText = null,
    IReadOnlyList<JevControlOption>? KeyBindings = null,
    bool CandidateSetComplete = true,
    DateTimeOffset? ExpiresAtUtc = null,
    bool ObservedTextTruncated = false,
    string? CandidateSetIssue = null,
    IReadOnlyList<string>? CandidateRoleFilter = null,
    bool AllowIncompleteCandidateSetForRecommendationOnly = false,
    bool AllowScoreDisambiguation = true,
    int? MaxRetriesOverride = null,
    IReadOnlyDictionary<string, object>? RequiredNoulQuestions = null,
    IReadOnlyDictionary<string, JevBoxQuestion>? RequiredBoxQuestions = null);

public sealed record JevDecision(
    string Operation,
    string? TargetElementId,
    double Confidence,
    IReadOnlyDictionary<string, double> Probabilities,
    bool? GoalReached,
    double? GoalReachedProbability,
    string ObservationId,
    string Model,
    long LatencyMs,
    string RequestJson,
    string ResponseJson,
    bool DisambiguatedByScore = false,
    double? TopChoiceMargin = null,
    DateTimeOffset? ExpiresAtUtc = null,
    double? TargetConfidence = null,
    IReadOnlyDictionary<string, double>? OperationProbabilities = null,
    IReadOnlyDictionary<string, double>? RequiredNoulProbabilities = null,
    IReadOnlyDictionary<string, JevBoxDecision>? RequiredBoxDecisions = null)
{
    /// <summary>True when this decision was computed against an observation the caller no longer holds current.</summary>
    public bool IsStaleAgainst(string currentObservationId) => !string.Equals(ObservationId, currentObservationId, StringComparison.Ordinal);

    public bool IsExpired(DateTimeOffset? now = null) => ExpiresAtUtc is { } expiry && expiry <= (now ?? DateTimeOffset.UtcNow);
}

public sealed record JevBoxDecision(
    double TargetVisibleProbability,
    double Confidence,
    double Top,
    double Bottom,
    double Left,
    double Right,
    long LatencyMs,
    string Model);

public class JevDisabledException(string message) : InvalidOperationException(message);
public class JevUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
public class JevValidationException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
