// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Diagnostics;
using System.Text.Json;

namespace ShadowUse.Jev;

/// <summary>
/// Builds one Choice/Noul fan-out request from a real candidate set, sends it,
/// validates every answer, and maps the winning choice back to a real
/// <see cref="JevElement"/>. Jev never generates selectors, handles or
/// coordinates — DCU already has the concrete targets; Jev only judges among them.
/// </summary>
public sealed class JevController
{
    private readonly JevClient _client;
    private readonly JevOptions _options;

    public JevController(HttpClient httpClient, JevOptions options)
    {
        _client = new JevClient(httpClient, options);
        _options = options;
    }

    public JevMode Mode => _options.Mode;
    public double MinExecuteConfidence => _options.MinExecuteConfidence;

    public async Task<JevDecision> DecideAsync(JevDecideRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_options.Mode == JevMode.Off)
            throw new JevDisabledException("Jev is off; use the deterministic/manual control path.");

        var built = JevActionSpace.Build(request.Elements);

        var operationCriteria = new Dictionary<string, string>();
        foreach (var operation in built.TargetsByOperation.Keys)
            operationCriteria[operation] = request.OperationDescriptions.TryGetValue(operation, out var description) ? description : operation;
        if (request.ControlOptions is not null)
            foreach (var control in request.ControlOptions)
                operationCriteria[control.Code] = control.Description;

        if (operationCriteria.Count == 0)
            throw new JevValidationException("No operations available for Jev to choose from.");
        if (operationCriteria.Count > 255)
            throw new JevValidationException("Too many candidate operations for a single Jev Choice question.");

        var questions = new Dictionary<string, object>
        {
            ["operation"] = new
            {
                type = "choice",
                criteria = operationCriteria,
                instructions = new { goal = request.Goal, context = request.Context, recent_actions = request.RecentActions ?? [] },
            },
        };
        foreach (var (operation, targets) in built.TargetsByOperation)
        {
            questions[operation.ToLowerInvariant() + "_target"] = new
            {
                type = "choice",
                criteria = targets.ToDictionary(kv => kv.Key, kv => (object)DescribeElement(kv.Value)),
                instructions = new { goal = request.Goal, operation },
            };
        }
        if (request.AskGoalReached)
        {
            questions["goal_reached"] = new
            {
                type = "noul",
                instructions = "Does the current visible state show that the requested goal is complete?",
            };
        }

        var body = new
        {
            model = _options.Model,
            state = new
            {
                goal = request.Goal,
                observation_id = request.ObservationId,
                context = request.Context,
                elements = request.Elements.Select(e => new { e.Id, e.Role, e.Name, e.CurrentValue, e.Enabled, e.Operations }),
                recent_actions = request.RecentActions ?? [],
            },
            questions,
        };
        var requestJson = JsonSerializer.Serialize(body);

        var started = Stopwatch.GetTimestamp();
        var raw = await _client.PostAsync(body, ct).ConfigureAwait(false);
        var latencyMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw.Body);
        }
        catch (JsonException ex)
        {
            throw new JevValidationException("Invalid Jev response: not valid JSON.", ex);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("answers", out var answers))
                throw new JevValidationException("Invalid Jev response: missing 'answers'.");

            var operationAnswer = ValidateChoice(answers, "operation", operationCriteria.Keys.ToHashSet());
            var operation = operationAnswer.Choice;

            string? targetElementId = null;
            Dictionary<string, double> probabilities;
            bool disambiguatedByScore = false;
            double? topChoiceMargin = null;
            if (built.TargetsByOperation.TryGetValue(operation, out var opTargets))
            {
                var targetAnswer = ValidateChoice(answers, operation.ToLowerInvariant() + "_target", opTargets.Keys.ToHashSet());
                var chosenElement = opTargets[targetAnswer.Choice];
                targetElementId = chosenElement.Id;
                probabilities = opTargets.ToDictionary(kv => kv.Value.Id, kv => targetAnswer.Probabilities[kv.Key]);

                var ranked = targetAnswer.Probabilities.OrderByDescending(kv => kv.Value).ToArray();
                if (ranked.Length >= 2)
                    topChoiceMargin = ranked[0].Value - ranked[1].Value;
                if (_options.ScoreDisambiguationMargin > 0 && topChoiceMargin is { } margin && margin < _options.ScoreDisambiguationMargin)
                {
                    var tiedKeys = ranked
                        .Where(kv => ranked[0].Value - kv.Value < _options.ScoreDisambiguationMargin)
                        .Take(_options.MaxScoreDisambiguationCandidates)
                        .Select(kv => kv.Key)
                        .ToArray();
                    var rescored = await TryDisambiguateByScoreAsync(request, operation, opTargets, tiedKeys, ct).ConfigureAwait(false);
                    if (rescored is not null)
                    {
                        chosenElement = opTargets[rescored];
                        targetElementId = chosenElement.Id;
                        disambiguatedByScore = true;
                    }
                }
            }
            else
            {
                probabilities = new Dictionary<string, double> { [operation] = operationAnswer.Probabilities[operation] };
            }

            // A malformed goal_reached answer must not fail the primary operation/target decision;
            // Jev's judgment heads are a bonus, not a gate on the executable action already validated above.
            bool? goalReached = null;
            double? goalReachedProbability = null;
            if (request.AskGoalReached && answers.TryGetProperty("goal_reached", out var goalAnswer) && TryValidateNoul(goalAnswer, out var noulValue))
            {
                goalReachedProbability = noulValue;
                goalReached = noulValue >= 0.5;
            }

            var model = root.TryGetProperty("model", out var modelProperty) ? modelProperty.GetString() ?? _options.Model : _options.Model;

            return new JevDecision(
                operation,
                targetElementId,
                operationAnswer.Confidence,
                probabilities,
                goalReached,
                goalReachedProbability,
                request.ObservationId,
                model,
                latencyMs,
                requestJson,
                raw.Body,
                disambiguatedByScore,
                topChoiceMargin);
        }
    }

    /// <summary>Near-tied Choice targets get one follow-up Score call ranking match strength.
    /// Score is a refinement, not a gate: any failure or invalid answer keeps the original
    /// Choice winner instead of throwing.</summary>
    private async Task<string?> TryDisambiguateByScoreAsync(
        JevDecideRequest request,
        string operation,
        IReadOnlyDictionary<string, JevElement> opTargets,
        string[] tiedKeys,
        CancellationToken ct)
    {
        if (tiedKeys.Length < 2)
            return null;

        var criteria = new[] { "poor match", "partial match", "strong match" };
        var questions = tiedKeys.ToDictionary(
            key => "score_" + key,
            key => (object)new
            {
                type = "score",
                criteria,
                instructions = $"Goal: {request.Goal}. How well does this candidate for operation '{operation}' match the goal? {DescribeElementJson(opTargets[key])}",
            });

        var body = new { model = _options.Model, questions };
        JevRawResponse raw;
        try
        {
            raw = await _client.PostAsync(body, ct).ConfigureAwait(false);
        }
        catch (JevUnavailableException)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw.Body);
            if (!doc.RootElement.TryGetProperty("answers", out var answers))
                return null;

            string? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var key in tiedKeys)
            {
                if (!TryValidateScore(answers, "score_" + key, criteria.Length, out var score))
                    return null; // any invalid answer aborts disambiguation; keep the original Choice winner
                if (score > bestScore)
                {
                    bestScore = score;
                    best = key;
                }
            }
            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryValidateScore(JsonElement answers, string key, int levelCount, out double score)
    {
        score = 0;
        if (!answers.TryGetProperty(key, out var answerElement))
            return false;
        try
        {
            var value = answerElement.GetProperty("score").GetDouble();
            if (!double.IsFinite(value) || value < 0 || value > levelCount - 1)
                return false;
            score = value;
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static string DescribeElementJson(JevElement element)
        => JsonSerializer.Serialize(DescribeElement(element));

    private static object DescribeElement(JevElement element) => new
    {
        label = element.Name ?? element.Role ?? element.Id,
        role = element.Role,
        current_value = element.CurrentValue,
        enabled = element.Enabled,
    };

    private static ChoiceAnswer ValidateChoice(JsonElement answers, string key, HashSet<string> validIds)
    {
        if (!answers.TryGetProperty(key, out var answerElement))
            throw new JevValidationException($"Invalid Jev response: missing answer '{key}'.");
        try
        {
            var choice = answerElement.GetProperty("choice").GetString();
            var confidence = answerElement.GetProperty("confidence").GetDouble();
            var probabilities = new Dictionary<string, double>();
            foreach (var property in answerElement.GetProperty("probabilities").EnumerateObject())
                probabilities[property.Name] = property.Value.GetDouble();

            var valid = choice is not null
                && validIds.Contains(choice)
                && probabilities.Keys.ToHashSet().SetEquals(validIds)
                && probabilities.Values.All(v => double.IsFinite(v) && v is >= 0 and <= 1)
                && double.IsFinite(confidence) && confidence is >= 0 and <= 1
                && Math.Abs(probabilities.Values.Sum() - 1) < 0.02
                && probabilities[choice] >= probabilities.Values.Max() - 1e-6;
            if (!valid)
                throw new JevValidationException($"Invalid Jev response for '{key}'; no action executed.");
            return new ChoiceAnswer(choice!, confidence, probabilities);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new JevValidationException($"Invalid Jev response for '{key}'; no action executed.");
        }
    }

    /// <summary>Noul answers are a judgment bonus, not a gate: an invalid shape returns false rather than throwing.</summary>
    private static bool TryValidateNoul(JsonElement answerElement, out double probability)
    {
        probability = 0;
        if (!answerElement.TryGetProperty("noul", out var noulProperty))
            return false;
        try
        {
            var value = noulProperty.GetDouble();
            if (!double.IsFinite(value) || value is < 0 or > 1)
                return false;
            probability = value;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record ChoiceAnswer(string Choice, double Confidence, Dictionary<string, double> Probabilities);
}
