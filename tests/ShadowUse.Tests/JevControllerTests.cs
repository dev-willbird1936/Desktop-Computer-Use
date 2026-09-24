// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Net;
using System.Text;
using ShadowUse.Jev;

namespace ShadowUse.Tests;

public sealed class JevControllerTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public int CallCount;
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => throw new InvalidOperationException("Respond not configured.");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static JevElement[] TwoClickTargets() =>
    [
        new("e1", "button", "Cancel", null, true, ["click"]),
        new("e2", "button", "Sign in", null, true, ["click"]),
    ];

    private static readonly IReadOnlyList<JevControlOption> ControlOptions =
    [
        new("WAIT", "The interface is still changing."),
        new("DONE", "The goal appears complete."),
        new("BLOCKED", "No supported operation can progress."),
    ];

    private static JevDecideRequest BasicRequest(string obsId = "obs-1") => new(
        "Sign in",
        obsId,
        TwoClickTargets(),
        new Dictionary<string, string> { ["click"] = "Click a visible button." },
        ControlOptions,
        AskGoalReached: true);

    private const string HappyPathBody = """
        {
          "model": "jev-test",
          "answers": {
            "operation": { "choice": "click", "confidence": 0.9, "probabilities": { "click": 0.9, "WAIT": 0.05, "DONE": 0.03, "BLOCKED": 0.02 } },
            "click_target": { "choice": "2", "confidence": 0.95, "probabilities": { "1": 0.05, "2": 0.95 } },
            "goal_reached": { "noul": 0.15 }
          }
        }
        """;

    [Fact]
    public async Task OffMode_ThrowsWithoutAnyNetworkCall()
    {
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, HappyPathBody) };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Off });

        await Assert.ThrowsAsync<JevDisabledException>(() => controller.DecideAsync(BasicRequest()));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task FastLoop_HappyPath_MapsWinningTargetBackToRealElement()
    {
        var envVar = "JEV_TEST_KEY_" + nameof(FastLoop_HappyPath_MapsWinningTargetBackToRealElement);
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, HappyPathBody) };
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.FastLoop,
            ApiKeyEnvironmentVariable = envVar,
        });
        Environment.SetEnvironmentVariable(envVar, "test-key");
        try
        {
            var decision = await controller.DecideAsync(BasicRequest("obs-42"));

            Assert.Equal("click", decision.Operation);
            Assert.Equal("e2", decision.TargetElementId);
            Assert.Equal(0.95, decision.Probabilities["e2"], 3);
            Assert.Equal("obs-42", decision.ObservationId);
            Assert.False(decision.GoalReached);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task MissingApiKey_ThrowsUnavailableWithoutNetworkCall()
    {
        var envVar = "JEV_TEST_KEY_" + nameof(MissingApiKey_ThrowsUnavailableWithoutNetworkCall);
        Environment.SetEnvironmentVariable(envVar, null);
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, HappyPathBody) };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist, ApiKeyEnvironmentVariable = envVar });

        var ex = await Assert.ThrowsAsync<JevUnavailableException>(() => controller.DecideAsync(BasicRequest()));
        Assert.Contains(envVar, ex.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task InvalidProbabilities_ThrowValidationException()
    {
        const string body = """
            {
              "answers": {
                "operation": { "choice": "click", "confidence": 0.9, "probabilities": { "click": 0.5, "WAIT": 0.1, "DONE": 0.1, "BLOCKED": 0.1 } }
              }
            }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(InvalidProbabilities_ThrowValidationException);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, body) };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist, ApiKeyEnvironmentVariable = envVar, MaxRetries = 0 });
        try
        {
            await Assert.ThrowsAsync<JevValidationException>(() => controller.DecideAsync(BasicRequest()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task ServerError_ExhaustsRetriesThenThrowsUnavailable()
    {
        var envVar = "JEV_TEST_KEY_" + nameof(ServerError_ExhaustsRetriesThenThrowsUnavailable);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.InternalServerError, "boom") };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist, ApiKeyEnvironmentVariable = envVar, MaxRetries = 0 });
        try
        {
            var ex = await Assert.ThrowsAsync<JevUnavailableException>(() => controller.DecideAsync(BasicRequest()));
            Assert.Contains("500", ex.Message);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task RateLimited_RetriesThenSucceeds()
    {
        var envVar = "JEV_TEST_KEY_" + nameof(RateLimited_RetriesThenSucceeds);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var attempt = 0;
        var handler = new FakeHandler
        {
            Respond = _ => Interlocked.Increment(ref attempt) == 1
                ? Json((HttpStatusCode)429, "rate limited")
                : Json(HttpStatusCode.OK, HappyPathBody),
        };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist, ApiKeyEnvironmentVariable = envVar, MaxRetries = 1 });
        try
        {
            var decision = await controller.DecideAsync(BasicRequest());
            Assert.Equal("e2", decision.TargetElementId);
            Assert.Equal(2, handler.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task TooManyOperations_ThrowsValidationBeforeAnyRequest()
    {
        var elements = Enumerable.Range(0, 256)
            .Select(i => new JevElement($"e{i}", "button", $"Button {i}", null, true, [$"op{i}"]))
            .ToArray();
        var descriptions = elements.ToDictionary(e => e.Operations[0], _ => "desc");
        var request = new JevDecideRequest("goal", "obs-1", elements, descriptions);
        var handler = new FakeHandler { Respond = _ => throw new InvalidOperationException("should not be called") };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist });

        await Assert.ThrowsAsync<JevValidationException>(() => controller.DecideAsync(request));
    }

    [Fact]
    public async Task MalformedGoalReached_DoesNotFailThePrimaryDecision()
    {
        const string body = """
            {
              "answers": {
                "operation": { "choice": "click", "confidence": 0.9, "probabilities": { "click": 0.95, "WAIT": 0.02, "DONE": 0.02, "BLOCKED": 0.01 } },
                "click_target": { "choice": "2", "confidence": 0.95, "probabilities": { "1": 0.05, "2": 0.95 } },
                "goal_reached": { "value": true, "confidence": 0.9 }
              }
            }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(MalformedGoalReached_DoesNotFailThePrimaryDecision);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, body) };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist, ApiKeyEnvironmentVariable = envVar });
        try
        {
            var decision = await controller.DecideAsync(BasicRequest());
            Assert.Equal("e2", decision.TargetElementId);
            Assert.Null(decision.GoalReached);
            Assert.Null(decision.GoalReachedProbability);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task GoalReached_ParsesRealNoulShapeAndThresholds()
    {
        const string body = """
            {
              "answers": {
                "operation": { "choice": "click", "confidence": 0.9, "probabilities": { "click": 0.95, "WAIT": 0.02, "DONE": 0.02, "BLOCKED": 0.01 } },
                "click_target": { "choice": "2", "confidence": 0.95, "probabilities": { "1": 0.05, "2": 0.95 } },
                "goal_reached": { "noul": 0.83 }
              }
            }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(GoalReached_ParsesRealNoulShapeAndThresholds);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, body) };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist, ApiKeyEnvironmentVariable = envVar });
        try
        {
            var decision = await controller.DecideAsync(BasicRequest());
            Assert.True(decision.GoalReached);
            Assert.Equal(0.83, decision.GoalReachedProbability!.Value, 3);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public void IsStaleAgainst_DetectsObservationChange()
    {
        var decision = new JevDecision("click", "e2", 0.9, new Dictionary<string, double>(), null, null, "obs-1", "jev-test", 5, "{}", "{}");
        Assert.False(decision.IsStaleAgainst("obs-1"));
        Assert.True(decision.IsStaleAgainst("obs-2"));
    }

    private const string TiedTargetsBody = """
        {
          "model": "jev-test",
          "answers": {
            "operation": { "choice": "click", "confidence": 0.9, "probabilities": { "click": 0.9, "WAIT": 0.05, "DONE": 0.03, "BLOCKED": 0.02 } },
            "click_target": { "choice": "2", "confidence": 0.52, "probabilities": { "1": 0.48, "2": 0.52 } },
            "goal_reached": { "noul": 0.15 }
          }
        }
        """;

    [Fact]
    public async Task NearTiedChoice_IssuesOneScoreCall_AndCanFlipTheWinner()
    {
        const string scoreBody = """
            {
              "answers": {
                "score_2": { "score": 0.3, "probabilities": { "0": 0.5, "1": 0.3, "2": 0.2 }, "confidence": 0.6 },
                "score_1": { "score": 1.8, "probabilities": { "0": 0.05, "1": 0.15, "2": 0.8 }, "confidence": 0.85 }
              }
            }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(NearTiedChoice_IssuesOneScoreCall_AndCanFlipTheWinner);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler();
        handler.Respond = _ => Json(HttpStatusCode.OK, handler.CallCount == 1 ? TiedTargetsBody : scoreBody);
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.FastLoop, ApiKeyEnvironmentVariable = envVar });
        try
        {
            var decision = await controller.DecideAsync(BasicRequest());

            Assert.Equal(2, handler.CallCount);
            Assert.True(decision.DisambiguatedByScore);
            Assert.Equal("e1", decision.TargetElementId);
            Assert.NotNull(decision.TopChoiceMargin);
            Assert.True(decision.TopChoiceMargin < 0.1);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task NearTiedChoice_ScoreCallFails_FallsBackToTheOriginalChoiceWinner()
    {
        var envVar = "JEV_TEST_KEY_" + nameof(NearTiedChoice_ScoreCallFails_FallsBackToTheOriginalChoiceWinner);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler();
        handler.Respond = req => handler.CallCount == 1
            ? Json(HttpStatusCode.OK, TiedTargetsBody)
            : Json(HttpStatusCode.InternalServerError, "boom");
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.FastLoop, ApiKeyEnvironmentVariable = envVar, MaxRetries = 0 });
        try
        {
            var decision = await controller.DecideAsync(BasicRequest());

            Assert.Equal(2, handler.CallCount);
            Assert.False(decision.DisambiguatedByScore);
            Assert.Equal("e2", decision.TargetElementId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task WideMargin_NeverIssuesAScoreCall()
    {
        var envVar = "JEV_TEST_KEY_" + nameof(WideMargin_NeverIssuesAScoreCall);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, HappyPathBody) };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.FastLoop, ApiKeyEnvironmentVariable = envVar });
        try
        {
            var decision = await controller.DecideAsync(BasicRequest());

            Assert.Equal(1, handler.CallCount);
            Assert.False(decision.DisambiguatedByScore);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }
}
