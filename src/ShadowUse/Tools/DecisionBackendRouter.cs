// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Config;
using ShadowUse.Jev;

namespace ShadowUse.Tools;

/// <summary>Selects one bounded decision backend for native, browser, and game JEV requests.</summary>
public sealed class DecisionBackendRouter(ShadowSettings settings, JevController jev,
    OpenAiResponsesDecisionBackend? openAi = null)
{
    public string Name => settings.DecisionBackend == "openai" ? openAi?.Name ?? "openai-responses" : "jev";

    public Task<JevDecision> DecideAsync(JevDecideRequest request,
        ReadOnlyMemory<byte>? screenshotJpeg = null, CancellationToken ct = default)
    {
        if (settings.DecisionBackend == "openai")
        {
            if (openAi is null)
                throw new JevUnavailableException("OpenAI decision backend is unavailable; no request was made.");
            return openAi.DecideAsync(request, screenshotJpeg, ct);
        }
        return jev.DecideAsync(request, ct);
    }
}
