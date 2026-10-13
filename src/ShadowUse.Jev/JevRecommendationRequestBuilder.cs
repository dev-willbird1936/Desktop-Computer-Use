// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
namespace ShadowUse.Jev;

/// <summary>Builds a single-call, non-executing recommendation over caller-supplied visual targets.</summary>
public static class JevRecommendationRequestBuilder
{
    public const string RecommendTargetOperation = "RECOMMEND_TARGET";

    private static readonly IReadOnlyDictionary<string, string> OperationDescriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RecommendTargetOperation] = "Recommend one listed visual target for review. This is advice only; no input will be dispatched.",
        };

    private static readonly IReadOnlyList<JevControlOption> Controls =
    [
        new("REOBSERVE", "The visual observation is stale, incomplete, or too ambiguous; capture and parse the screen again."),
        new("ASK_USER", "A visual identity, intent, or actionability fact is missing; ask the user."),
        new("STOP", "Do not recommend a target."),
    ];

    public static JevDecideRequest Build(
        string goal,
        string observationId,
        IReadOnlyList<JevElement> elements,
        string scope,
        string? observedText,
        DateTimeOffset expiresAtUtc,
        bool candidateSetComplete,
        string? candidateSetIssue = null,
        IReadOnlyList<string>? recentActions = null,
        bool allowIncompleteCandidateSetForRecommendationOnly = false,
        bool allowScoreDisambiguation = true,
        int? maxRetriesOverride = null,
        IReadOnlyList<JevControlOption>? keyBindings = null)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return new JevDecideRequest(
            goal,
            observationId,
            elements,
            OperationDescriptions,
            Controls,
            recentActions,
            Context: scope,
            AskGoalReached: false,
            ObservedText: observedText,
            CandidateSetComplete: candidateSetComplete,
            ExpiresAtUtc: expiresAtUtc,
            CandidateSetIssue: candidateSetIssue,
            AllowIncompleteCandidateSetForRecommendationOnly: allowIncompleteCandidateSetForRecommendationOnly,
            AllowScoreDisambiguation: allowScoreDisambiguation,
            MaxRetriesOverride: maxRetriesOverride,
            KeyBindings: keyBindings);
    }
}
