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
    public async Task HttpFailureIncludesBoundedProviderErrorMessageOnly()
    {
        var envVar = "JEV_TEST_KEY_" + nameof(HttpFailureIncludesBoundedProviderErrorMessageOnly);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler
        {
            Respond = _ => Json(HttpStatusCode.BadRequest,
                "{\"error\":{\"message\":\"Unsupported request format\"},\"debug_secret\":\"not forwarded\"}"),
        };
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            ApiKeyEnvironmentVariable = envVar,
        });

        try
        {
            var error = await Assert.ThrowsAsync<JevUnavailableException>(() => controller.DecideAsync(BasicRequest()));
            Assert.Contains("HTTP 400", error.Message);
            Assert.Contains("Unsupported request format", error.Message);
            Assert.DoesNotContain("not forwarded", error.Message);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task HttpFailureIncludesSanitizedBoundedPlainTextProviderBody()
    {
        var envVar = "JEV_TEST_KEY_" + nameof(HttpFailureIncludesSanitizedBoundedPlainTextProviderBody);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("Bad request: model unavailable\n" + new string('x', 500), Encoding.UTF8, "text/plain"),
            },
        };
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            ApiKeyEnvironmentVariable = envVar,
        });

        try
        {
            var error = await Assert.ThrowsAsync<JevUnavailableException>(() => controller.DecideAsync(BasicRequest()));
            Assert.Contains("Bad request: model unavailable", error.Message);
            Assert.DoesNotContain("\n", error.Message);
            Assert.True(error.Message.Length < 450);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
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
            Assert.Equal(0.95, decision.TargetConfidence!.Value, 3);
            Assert.Equal(0.9, decision.OperationProbabilities!["click"], 3);
            Assert.Equal("obs-42", decision.ObservationId);
            Assert.True(decision.IsStaleAgainst("obs-43")); // same short candidate IDs cannot alias a newer observation
            Assert.False(decision.GoalReached);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task RequestCarriesVersionedObservationTextKeyBindingsAndSafetyPolicy()
    {
        var envVar = "JEV_TEST_KEY_" + nameof(RequestCarriesVersionedObservationTextKeyBindingsAndSafetyPolicy);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        string? requestJson = null;
        var handler = new FakeHandler
        {
            Respond = request =>
            {
                requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(HttpStatusCode.OK, HappyPathBody);
            },
        };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist, ApiKeyEnvironmentVariable = envVar });
        var request = BasicRequest() with
        {
            ObservedText = "OCR: Continue",
            ObservedTextTruncated = true,
            CandidateSetIssue = "page candidate list capped",
            KeyBindings = [new JevControlOption("PRESS_KEY_ENTER", "Press Enter")],
        };

        try
        {
            await controller.DecideAsync(request);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }

        using var body = System.Text.Json.JsonDocument.Parse(requestJson!);
        var state = body.RootElement.GetProperty("state");
        Assert.Equal(1, state.GetProperty("schema_version").GetInt32());
        Assert.Equal("Sign in", state.GetProperty("goal").GetString());
        Assert.Equal("OCR: Continue", state.GetProperty("observed_text").GetString());
        Assert.True(state.GetProperty("observed_text_truncated").GetBoolean());
        Assert.Equal("page candidate list capped", state.GetProperty("candidate_set_issue").GetString());
        Assert.Equal("PRESS_KEY_ENTER", state.GetProperty("key_bindings")[0].GetProperty("Code").GetString());
        Assert.Contains("untrusted observations", body.RootElement.GetProperty("questions").GetProperty("operation").GetProperty("instructions").GetProperty("policy").GetString());
        var targetInstructions = body.RootElement.GetProperty("questions").GetProperty("click_target").GetProperty("instructions");
        Assert.Equal("OCR: Continue", targetInstructions.GetProperty("observed_text").GetString());
        Assert.True(targetInstructions.GetProperty("observed_text_truncated").GetBoolean());
        Assert.Contains("untrusted observations", targetInstructions.GetProperty("policy").GetString());
    }

    [Fact]
    public async Task IncompleteOrExpiredObservationIsRejectedBeforeNetworkCall()
    {
        var handler = new FakeHandler { Respond = _ => throw new InvalidOperationException("request must not be sent") };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist });

        await Assert.ThrowsAsync<JevValidationException>(() => controller.DecideAsync(BasicRequest() with { CandidateSetComplete = false }));
        await Assert.ThrowsAsync<JevValidationException>(() => controller.DecideAsync(BasicRequest() with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CallerCancellationIsNotRewrittenAsJevUnavailable()
    {
        var envVar = "JEV_TEST_KEY_" + nameof(CallerCancellationIsNotRewrittenAsJevUnavailable);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => throw new OperationCanceledException("cancelled") };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist, ApiKeyEnvironmentVariable = envVar });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.DecideAsync(BasicRequest(), cancellation.Token));
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
    public async Task TooManyCandidates_ThrowsValidationBeforeAnyRequest()
    {
        var elements = Enumerable.Range(0, 256)
            .Select(i => new JevElement($"e{i}", "button", $"Button {i}", null, true, ["click"]))
            .ToArray();
        var request = new JevDecideRequest("goal", "obs-1", elements, new Dictionary<string, string> { ["click"] = "desc" });
        var handler = new FakeHandler { Respond = _ => throw new InvalidOperationException("should not be called") };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist });

        await Assert.ThrowsAsync<JevValidationException>(() => controller.DecideAsync(request));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task EmptyCandidateSetCanChooseNoActionWithoutInventingATarget()
    {
        const string waitBody = """
            { "answers": { "operation": { "choice": "WAIT", "confidence": 0.9, "probabilities": { "WAIT": 0.9, "DONE": 0.05, "BLOCKED": 0.05 } } } }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(EmptyCandidateSetCanChooseNoActionWithoutInventingATarget);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, waitBody) };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist, ApiKeyEnvironmentVariable = envVar });
        var request = new JevDecideRequest("wait", "obs-empty", [], new Dictionary<string, string>(), ControlOptions);

        try
        {
            var decision = await controller.DecideAsync(request);
            Assert.Equal("WAIT", decision.Operation);
            Assert.Null(decision.TargetElementId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task NoTargetDecisionPreservesTheFullOperationProbabilityDistribution()
    {
        const string blockedBody = """
            { "answers": { "operation": { "choice": "BLOCKED", "confidence": 0.45, "probabilities": { "click": 0.2, "WAIT": 0.1, "DONE": 0.25, "BLOCKED": 0.45 } } } }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(NoTargetDecisionPreservesTheFullOperationProbabilityDistribution);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, blockedBody) };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist, ApiKeyEnvironmentVariable = envVar });

        try
        {
            var decision = await controller.DecideAsync(BasicRequest());
            Assert.Equal("BLOCKED", decision.Operation);
            Assert.Equal(4, decision.Probabilities.Count);
            Assert.Equal(0.45, decision.Probabilities["BLOCKED"], 3);
            Assert.Equal(1.0, decision.Probabilities.Values.Sum(), 3);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
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
        string? scoreRequestJson = null;
        var handler = new FakeHandler();
        handler.Respond = request =>
        {
            if (handler.CallCount == 1) return Json(HttpStatusCode.OK, TiedTargetsBody);
            scoreRequestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, scoreBody);
        };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.FastLoop, ApiKeyEnvironmentVariable = envVar, ScoreDisambiguationMargin = 0.1 });
        try
        {
            var decision = await controller.DecideAsync(BasicRequest() with { ObservedText = "Board: O O . / . X . / X . ." });

            Assert.Equal(2, handler.CallCount);
            Assert.True(decision.DisambiguatedByScore);
            Assert.Equal("e1", decision.TargetElementId);
            Assert.NotNull(decision.TopChoiceMargin);
            Assert.True(decision.TopChoiceMargin < 0.1);
            using var scoreRequest = System.Text.Json.JsonDocument.Parse(scoreRequestJson!);
            Assert.Equal("Board: O O . / . X . / X . .", scoreRequest.RootElement.GetProperty("state").GetProperty("observed_text").GetString());
            Assert.Contains("Board: O O .", scoreRequest.RootElement.GetProperty("questions").GetProperty("score_1").GetProperty("instructions").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task FastLoop_DisablesFollowUpScoreCallByDefault()
    {
        var envVar = "JEV_TEST_KEY_" + nameof(FastLoop_DisablesFollowUpScoreCallByDefault);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, TiedTargetsBody) };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.FastLoop, ApiKeyEnvironmentVariable = envVar });
        try
        {
            var decision = await controller.DecideAsync(BasicRequest());

            Assert.Equal(1, handler.CallCount);
            Assert.False(decision.DisambiguatedByScore);
            Assert.Equal("e2", decision.TargetElementId);
            Assert.Equal(0.52, decision.TargetConfidence!.Value, 3);
            Assert.Equal(0.9, decision.OperationProbabilities!["click"], 3);
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
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.FastLoop, ApiKeyEnvironmentVariable = envVar, MaxRetries = 0, ScoreDisambiguationMargin = 0.1 });
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

    [Fact]
    public async Task ImageDecision_SendsSameAssetReferenceWithSafetyHeader_AndRedactsDiagnosticJson()
    {
        const string response = """
            { "model": "jev-1.14.0", "answers": {
              "operation": { "type": "choice", "choice": "CLICK", "confidence": 0.96, "probabilities": { "CLICK": 0.96, "WAIT": 0.04 } },
              "click_target": { "type": "choice", "choice": "1", "confidence": 0.97, "probabilities": { "1": 1.0 } },
              "action_applicable_01": { "type": "noul", "noul": 0.98 }
            } }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(ImageDecision_SendsSameAssetReferenceWithSafetyHeader_AndRedactsDiagnosticJson);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        byte[] jpeg = [0xff, 0xd8, 0x13, 0x37];
        string? sentBody = null;
        var handler = new FakeHandler
        {
            Respond = request =>
            {
                Assert.True(request.Headers.TryGetValues("TypeSafe-Danger-Zone-Images", out var values));
                Assert.Equal("true", Assert.Single(values!));
                sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(HttpStatusCode.OK, response);
            },
        };
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.FastLoop,
            Model = "jev-1.14.0",
            ApiKeyEnvironmentVariable = envVar,
        });
        var requestData = new JevDecideRequest("start solo", "frame-1",
            [new JevElement("start", "game_action", "Start solo", null, null, ["CLICK"])],
            new Dictionary<string, string> { ["CLICK"] = "Click the listed visual control." },
            [new JevControlOption("WAIT", "Do not act now.")],
            AskGoalReached: false,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddSeconds(4),
            AllowScoreDisambiguation: false,
            MaxRetriesOverride: 0,
            RequiredNoulQuestions: new Dictionary<string, object> { ["action_applicable_01"] = new { question = "Is this action applicable?" } });

        try
        {
            var decision = await controller.DecideWithImageAsync(requestData, jpeg);

            Assert.True(controller.SupportsBox);
            Assert.Equal("CLICK", decision.Operation);
            Assert.Equal("start", decision.TargetElementId);
            Assert.Equal(0.98, decision.RequiredNoulProbabilities!["action_applicable_01"], 3);
            Assert.DoesNotContain(Convert.ToBase64String(jpeg), decision.RequestJson);
            using var diagnostic = System.Text.Json.JsonDocument.Parse(decision.RequestJson);
            var diagnosticAsset = diagnostic.RootElement.GetProperty("assets").EnumerateObject().Single().Value;
            Assert.Equal("<redacted>", diagnosticAsset.GetProperty("data_base64").GetString());
            using var body = System.Text.Json.JsonDocument.Parse(sentBody!);
            var imageReference = body.RootElement.GetProperty("state").GetProperty("screenshot").GetString();
            var asset = body.RootElement.GetProperty("assets").GetProperty(imageReference!);
            Assert.Equal(Convert.ToBase64String(jpeg), asset.GetProperty("data_base64").GetString());
            Assert.Equal("jpg", asset.GetProperty("format").GetString());
            Assert.Equal("noul", body.RootElement.GetProperty("questions").GetProperty("action_applicable_01").GetProperty("type").GetString());
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task RequiredNoulAnswerMissingOrWrongType_FailsClosed()
    {
        const string response = """
            { "answers": {
              "operation": { "choice": "CLICK", "confidence": 0.96, "probabilities": { "CLICK": 0.96, "WAIT": 0.04 } },
              "click_target": { "choice": "1", "confidence": 0.97, "probabilities": { "1": 1.0 } },
              "action_applicable_01": { "confidence": 0.98, "noul": 0.99 }
            } }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(RequiredNoulAnswerMissingOrWrongType_FailsClosed);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, response) };
        var controller = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Assist, ApiKeyEnvironmentVariable = envVar, MaxRetries = 0 });
        var requestData = new JevDecideRequest("goal", "frame-1",
            [new JevElement("action", "game_action", "Action", null, null, ["CLICK"])],
            new Dictionary<string, string> { ["CLICK"] = "Click." },
            [new JevControlOption("WAIT", "Wait.")], AskGoalReached: false,
            RequiredNoulQuestions: new Dictionary<string, object> { ["action_applicable_01"] = new { question = "Is it applicable?" } });
        try
        {
            await Assert.ThrowsAsync<JevValidationException>(() => controller.DecideAsync(requestData));
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task ImageDecision_RejectsUntypedChoiceAnswer()
    {
        const string response = """
            { "model": "jev-1.14.0", "answers": {
              "operation": { "type": "noul", "choice": "CLICK", "confidence": 0.96, "probabilities": { "CLICK": 0.96, "WAIT": 0.04 } },
              "click_target": { "type": "choice", "choice": "1", "confidence": 0.97, "probabilities": { "1": 1.0 } },
              "action_applicable_01": { "type": "noul", "noul": 0.98 }
            } }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(ImageDecision_RejectsUntypedChoiceAnswer);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, response) };
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.FastLoop,
            Model = "jev-1.14.0",
            ApiKeyEnvironmentVariable = envVar,
            MaxRetries = 0,
        });
        var request = new JevDecideRequest("goal", "frame", [new JevElement("action", "button", "Action", null, null, ["CLICK"])],
            new Dictionary<string, string> { ["CLICK"] = "Click" }, [new JevControlOption("WAIT", "Wait")],
            AskGoalReached: false, RequiredNoulQuestions: new Dictionary<string, object> { ["action_applicable_01"] = new { question = "Applicable?" } });
        try
        {
            await Assert.ThrowsAsync<JevValidationException>(() => controller.DecideWithImageAsync(request, new byte[] { 1 }));
            Assert.Equal(1, handler.CallCount);
        }
        finally { Environment.SetEnvironmentVariable(envVar, null); }
    }

    [Fact]
    public async Task ImageDecision_RejectsDuplicateSafetyProperties()
    {
        const string response = """
            { "model": "jev-1.14.0", "answers": {
              "operation": { "type": "choice", "choice": "CLICK", "confidence": 0.96, "confidence": 0.99, "probabilities": { "CLICK": 0.96, "WAIT": 0.04 } },
              "click_target": { "type": "choice", "choice": "1", "confidence": 0.97, "probabilities": { "1": 1.0 } },
              "action_applicable_01": { "type": "noul", "noul": 0.98 }
            } }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(ImageDecision_RejectsDuplicateSafetyProperties);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, response) };
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.FastLoop,
            Model = "jev-1.14.0",
            ApiKeyEnvironmentVariable = envVar,
            MaxRetries = 0,
        });
        var request = new JevDecideRequest("goal", "frame", [new JevElement("action", "button", "Action", null, null, ["CLICK"])],
            new Dictionary<string, string> { ["CLICK"] = "Click" }, [new JevControlOption("WAIT", "Wait")],
            AskGoalReached: false, RequiredNoulQuestions: new Dictionary<string, object> { ["action_applicable_01"] = new { question = "Applicable?" } });
        try
        {
            await Assert.ThrowsAsync<JevValidationException>(() => controller.DecideWithImageAsync(request, new byte[] { 1 }));
            Assert.Equal(1, handler.CallCount);
        }
        finally { Environment.SetEnvironmentVariable(envVar, null); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong_type")]
    [InlineData("mismatch")]
    public async Task LocateBoxAsync_RequiresExplicitMatchingResponseModel(string modelCase)
    {
        string modelField = modelCase switch
        {
            "missing" => "",
            "wrong_type" => "\"model\":42,",
            _ => "\"model\":\"jev-preview\",",
        };
        string response = $$"""
            { {{modelField}} "answers": {
              "target_visible": { "type": "noul", "noul": 0.97 },
              "target_box": { "type": "box", "confidence": 0.96, "box": { "top": 0.1, "bottom": 0.4, "left": 0.2, "right": 0.5 } }
            } }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(LocateBoxAsync_RequiresExplicitMatchingResponseModel) + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, response) };
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            Model = "jev-1.14.0",
            ApiKeyEnvironmentVariable = envVar,
            MaxRetries = 0,
        });
        try
        {
            await Assert.ThrowsAsync<JevValidationException>(() => controller.LocateBoxAsync("frame", "goal", "button", new byte[] { 1 }));
            Assert.Equal(1, handler.CallCount);
        }
        finally { Environment.SetEnvironmentVariable(envVar, null); }
    }

    [Fact]
    public async Task LocateBoxAsync_RejectsDuplicateBoxProperties()
    {
        const string response = """
            { "model": "jev-1.14.0", "answers": {
              "target_visible": { "type": "noul", "noul": 0.97 },
              "target_box": { "type": "box", "confidence": 0.96, "confidence": 0.99, "box": { "top": 0.1, "bottom": 0.4, "left": 0.2, "right": 0.5 } }
            } }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(LocateBoxAsync_RejectsDuplicateBoxProperties);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var handler = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, response) };
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            Model = "jev-1.14.0",
            ApiKeyEnvironmentVariable = envVar,
            MaxRetries = 0,
        });
        try
        {
            await Assert.ThrowsAsync<JevValidationException>(() => controller.LocateBoxAsync("frame", "goal", "button", new byte[] { 1 }));
            Assert.Equal(1, handler.CallCount);
        }
        finally { Environment.SetEnvironmentVariable(envVar, null); }
    }

    [Fact]
    public async Task LocateBoxAsync_SendsImageAndRejectsInvalidGeometry()
    {
        const string response = """
            { "model": "jev-1.14.0", "answers": {
              "target_visible": { "type": "noul", "noul": 0.97 },
              "target_box": { "type": "box", "confidence": 0.96, "box": { "top": 0.1, "bottom": 0.4, "left": 0.2, "right": 0.5 } }
            } }
            """;
        var envVar = "JEV_TEST_KEY_" + nameof(LocateBoxAsync_SendsImageAndRejectsInvalidGeometry);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        byte[] jpeg = [0xff, 0xd8, 0xaa];
        string? sentBody = null;
        var handler = new FakeHandler
        {
            Respond = request =>
            {
                Assert.True(request.Headers.TryGetValues("TypeSafe-Danger-Zone-Images", out var values));
                Assert.Equal("true", Assert.Single(values!));
                sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(HttpStatusCode.OK, response);
            },
        };
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            Model = "jev-1.14.0",
            ApiKeyEnvironmentVariable = envVar,
        });
        try
        {
            var box = await controller.LocateBoxAsync("frame-2", "start solo", "the Start Solo button", jpeg);
            Assert.Equal(0.97, box.TargetVisibleProbability, 3);
            Assert.Equal(0.96, box.Confidence, 3);
            Assert.Equal(0.1, box.Top, 3);
            using var body = System.Text.Json.JsonDocument.Parse(sentBody!);
            var imageReference = body.RootElement.GetProperty("state").GetProperty("screenshot").GetString();
            Assert.Equal(Convert.ToBase64String(jpeg), body.RootElement.GetProperty("assets").GetProperty(imageReference!).GetProperty("data_base64").GetString());

            handler.Respond = _ => Json(HttpStatusCode.OK, response.Replace("\"right\": 0.5", "\"right\": 1.5", StringComparison.Ordinal));
            await Assert.ThrowsAsync<JevValidationException>(() => controller.LocateBoxAsync("frame-2", "start solo", "the Start Solo button", jpeg));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task ChoiceNoulAndBoxQuestionsShareOneImageRequest()
    {
        const string response = """
            { "model": "jev-1.14.0", "answers": {
              "operation": { "type": "choice", "choice": "click", "confidence": 0.97, "probabilities": { "click": 0.95, "WAIT": 0.03, "DONE": 0.01, "BLOCKED": 0.01 } },
              "click_target": { "type": "choice", "choice": "2", "confidence": 0.98, "probabilities": { "1": 0.02, "2": 0.98 } },
              "visible": { "type": "noul", "noul": 0.96 },
              "button_box": { "type": "box", "confidence": 0.93, "box": { "top": 0.2, "bottom": 0.4, "left": 0.3, "right": 0.6 } }
            } }
            """;
        string envVar = "JEV_TEST_KEY_" + nameof(ChoiceNoulAndBoxQuestionsShareOneImageRequest);
        Environment.SetEnvironmentVariable(envVar, "test-key");
        string? sentBody = null;
        int requestCount = 0;
        var handler = new FakeHandler
        {
            Respond = request =>
            {
                requestCount++;
                Assert.True(request.Headers.TryGetValues("TypeSafe-Danger-Zone-Images", out var values));
                Assert.Equal("true", Assert.Single(values!));
                sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(HttpStatusCode.OK, response);
            },
        };
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.FastLoop,
            Model = "jev-1.14.0",
            ApiKeyEnvironmentVariable = envVar,
            MaxRetries = 0,
        });
        var request = BasicRequest("frame-1") with
        {
            RequiredNoulQuestions = new Dictionary<string, object>
            {
                ["visible"] = new { question = "Is the exact button visible in screenshot?" },
            },
            RequiredBoxQuestions = new Dictionary<string, JevBoxQuestion>
            {
                ["button_box"] = new("visible", "Bloon Lab button"),
            },
        };
        byte[] jpeg = [0xff, 0xd8, 0x01, 0x02];
        try
        {
            var decision = await controller.DecideWithImageAsync(request, jpeg);

            Assert.Equal(1, requestCount);
            Assert.Equal("e2", decision.TargetElementId);
            Assert.True(decision.RequiredBoxDecisions!.TryGetValue("button_box", out var box));
            Assert.Equal(0.96, box!.TargetVisibleProbability, 3);
            Assert.Equal(0.93, box.Confidence, 3);
            Assert.Equal("jev-1.14.0", box.Model);
            using var body = System.Text.Json.JsonDocument.Parse(sentBody!);
            var root = body.RootElement;
            var state = root.GetProperty("state");
            var imageReference = state.GetProperty("screenshot").GetString()!;
            Assert.Equal(Convert.ToBase64String(jpeg), root.GetProperty("assets").GetProperty(imageReference).GetProperty("data_base64").GetString());
            Assert.Equal("choice", root.GetProperty("questions").GetProperty("operation").GetProperty("type").GetString());
            Assert.Equal("noul", root.GetProperty("questions").GetProperty("visible").GetProperty("type").GetString());
            Assert.Equal("box", root.GetProperty("questions").GetProperty("button_box").GetProperty("type").GetString());
        }
        finally { Environment.SetEnvironmentVariable(envVar, null); }
    }
}
