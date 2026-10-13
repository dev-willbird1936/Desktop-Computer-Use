// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Text.Json;
using ShadowUse.Jev;
using ShadowUse.Native;
using ShadowUse.Tools;
using ShadowUse.Vision;

namespace ShadowUse.Tests;

public sealed class JevVisualRecommendationTests
{
    private sealed class FakeHandler(string responseJson) : HttpMessageHandler
    {
        public int Calls;
        public string? RequestJson;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            RequestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    [Fact]
    public void GenericCandidateMappingDoesNotAssumeBloonsCalibration()
    {
        var observation = DetectSyntheticSelectionScreen();
        var elements = GameJevCandidateMapper.Map(observation);
        Assert.NotEmpty(elements);
        Assert.All(elements, element =>
        {
            Assert.Equal(GameJevCandidateMapper.UnspecifiedVisualSource, element.Extra!["source"]);
            Assert.Equal("source_unspecified_complete_unverified", element.Extra["coverage_status"]);
        });
    }

    [Fact]
    public async Task CalibratedTwentyOneCandidateObservationSendsBoxesAndReturnsListedTarget()
    {
        const string keyName = "JEV_TEST_KEY_CalibratedVisualRecommendation";
        Environment.SetEnvironmentVariable(keyName, "test-key");
        var observation = DetectSyntheticSelectionScreen();
        Assert.True(observation.CandidateSetComplete, observation.CandidateSetIssue);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hero-r1-c1"] = "dark hooded archer using a crossbow, orange-red accents",
            ["hero-r1-c2"] = "red-haired character holding a black handheld device",
            ["hero-r1-c3"] = "blue-green character wearing a leafy headpiece, arms raised",
            ["hero-r1-c4"] = "green-uniformed commander with a brass horn",
            ["hero-r1-c5"] = "tan character with spiky hair and blue goggles",
            ["hero-r1-c6"] = "green tracked tank with a goggled driver and cannon",
            ["hero-r1-c7"] = "brown-haired character holding an open green book",
            ["hero-r1-c8"] = "purple-hooded mage holding a staff",
            ["hero-r2-c1"] = "dark hooded archer using a cyan-blue bow",
            ["hero-r2-c2"] = "blue-haired character holding a black handheld device",
            ["hero-r2-c3"] = "pale blue-white winged character with horns or tufts and a teal outfit",
            ["hero-r2-c4"] = "yellow-orange helmeted character with round goggles and a small propeller",
            ["hero-r2-c5"] = "yellow armored insect-like character with antennae and dark goggles",
            ["hero-r2-c6"] = "bright-green armored vehicle or character with a black visor and cannon",
            ["hero-r2-c7"] = "white-helmeted character with red headphones at a DJ console",
            ["hero-r2-c8"] = "orange-blue catlike character wearing a blue suit",
        };
        var elements = GameJevCandidateMapper.Map(observation, labels, GameJevCandidateMapper.BloonsLayoutSource);
        Assert.Equal(23, elements.Count);
        int selectedIndex = Array.FindIndex(elements.ToArray(), element => element.Id == "frame-8:hero-r2-c1") + 1;
        Assert.Equal(9, selectedIndex);

        var targetProbabilities = elements
            .Select((element, index) => new KeyValuePair<string, double>((index + 1).ToString(), index + 1 == selectedIndex ? 0.9 : 0.1 / (elements.Count - 1)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var responseJson = JsonSerializer.Serialize(new
        {
            model = "jev-test",
            answers = new Dictionary<string, object>
            {
                ["operation"] = new
                {
                    choice = JevRecommendationRequestBuilder.RecommendTargetOperation,
                    confidence = 0.9,
                    probabilities = new { RECOMMEND_TARGET = 0.9, REOBSERVE = 0.05, ASK_USER = 0.03, STOP = 0.02 },
                },
                ["recommend_target_target"] = new { choice = selectedIndex.ToString(), confidence = 0.92, probabilities = targetProbabilities },
            },
        });
        var handler = new FakeHandler(responseJson);
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            ApiKeyEnvironmentVariable = keyName,
            MaxRetries = 0,
        });
        var request = JevRecommendationRequestBuilder.Build(
            "Recommend the cyan-bow archer. Do not ready or confirm.",
            observation.ObservationId,
            elements,
            observation.Scope + " Advice only; no input dispatch.",
            string.Join("\n", observation.ObservedText),
            DateTimeOffset.UtcNow.AddSeconds(20),
            candidateSetComplete: true);

        try
        {
            var decision = await controller.DecideAsync(request);
            Assert.Equal("RECOMMEND_TARGET", decision.Operation);
            Assert.Equal("frame-8:hero-r2-c1", decision.TargetElementId);
            Assert.Equal(1, handler.Calls);

            using var json = JsonDocument.Parse(handler.RequestJson!);
            var root = json.RootElement;
            Assert.False(root.GetProperty("questions").TryGetProperty("goal_reached", out _));
            var candidates = root.GetProperty("state").GetProperty("candidates");
            Assert.Equal(23, candidates.GetArrayLength());
            var upperLeft = candidates.EnumerateArray().Single(element => element.GetProperty("id").GetString() == "frame-8:hero-r1-c1");
            var lowerLeft = candidates.EnumerateArray().Single(element => element.GetProperty("id").GetString() == "frame-8:hero-r2-c1");
            Assert.Contains("orange-red", upperLeft.GetProperty("name").GetString());
            Assert.Contains("cyan-blue bow", lowerLeft.GetProperty("name").GetString());
            var selectedState = lowerLeft;
            var metadata = selectedState.GetProperty("metadata");
            Assert.Equal(233, metadata.GetProperty("bbox").GetProperty("x").GetInt32());
            Assert.Equal("screenshot_image_pixels", metadata.GetProperty("coordinate_space").GetString());
            Assert.Equal("unverified_visual_description", metadata.GetProperty("identity_status").GetString());
            Assert.Equal("unverified_visual_candidate", metadata.GetProperty("actionability").GetString());
            Assert.Equal(249, metadata.GetProperty("screen_bbox").GetProperty("x").GetInt32());
            Assert.Equal(747, metadata.GetProperty("screen_bbox").GetProperty("y").GetInt32());

            var targetCriteria = root.GetProperty("questions").GetProperty("recommend_target_target").GetProperty("criteria");
            Assert.Equal(23, targetCriteria.EnumerateObject().Count());
            Assert.Equal(233, targetCriteria.GetProperty(selectedIndex.ToString()).GetProperty("metadata").GetProperty("bbox").GetProperty("x").GetInt32());
            Assert.Equal("screenshot_image_pixels", targetCriteria.GetProperty(selectedIndex.ToString()).GetProperty("metadata").GetProperty("coordinate_space").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
        }
    }

    [Fact]
    public async Task GenericFrameBoundCandidateSetFlowsIntoJevWithCallerCoverageProvenance()
    {
        const string keyName = "JEV_TEST_KEY_GenericVisualRecommendation";
        Environment.SetEnvironmentVariable(keyName, "test-key");
        const string candidateJson = """
            {
              "frame_id":"frame-generic",
              "image_width":1680,
              "image_height":975,
              "scope":"Visible toolbar and ready/cancel controls",
              "candidate_set_complete":true,
              "coverage_evidence":"Unit-test fixture note; parser must preserve but not verify it.",
              "observed_text":["Ready!","Cancel"],
              "candidates":[
                {"key":"ready","role":"button","label":"Ready!","identity_source":"vision_ocr","bbox":{"x":1185,"y":740,"width":290,"height":130}},
                {"key":"cancel","role":"button","label":"Cancel","identity_source":"vision_ocr","bbox":{"x":1185,"y":570,"width":290,"height":120}}
              ],
              "context_regions":[{"key":"profile","label":"Player profile banner","bbox":{"x":220,"y":48,"width":460,"height":64},"note":"possible control; actionability unknown"}]
            }
            """;
        Assert.True(ExternalGameVisualCandidateSetParser.TryParse(candidateJson, "frame-generic", 1680, 975, out var parsed, out var parseError), parseError);
        Assert.NotNull(parsed);
        Assert.True(parsed.CandidateSetComplete);
        Assert.Equal(2, parsed.Candidates.Count);
        Assert.Single(parsed.ContextRegions);
        double scale = 1680 / 1722.0;
        var observation = new GameUiObservation("frame-generic", DateTimeOffset.UtcNow, "target-test",
            1722, 999, 1680, 975, scale, 96,
            new NativeMethods.RECT { Left = 10, Top = 20, Right = 1732, Bottom = 1019 },
            "caller-visual-candidates-v1", parsed.Scope, parsed.CandidateSetComplete,
            parsed.CandidateSetIssue, parsed.ObservedText, parsed.Candidates, parsed.ContextRegions,
            "caller_declared_coverage_unverified", parsed.CoverageEvidence);
        var elements = GameJevCandidateMapper.Map(observation, source: GameJevCandidateMapper.CallerVisualSource);
        const string fakeResponse = """
            {
              "model":"jev-test",
              "answers":{
                "operation":{"choice":"RECOMMEND_TARGET","confidence":0.9,"probabilities":{"RECOMMEND_TARGET":0.9,"REOBSERVE":0.05,"ASK_USER":0.03,"STOP":0.02}},
                "recommend_target_target":{"choice":"1","confidence":0.95,"probabilities":{"1":0.95,"2":0.05}}
              }
            }
            """;
        var handler = new FakeHandler(fakeResponse);
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            ApiKeyEnvironmentVariable = keyName,
            MaxRetries = 0,
        });
        var request = JevRecommendationRequestBuilder.Build("Recommend Ready if the goal is to start.",
            observation.ObservationId, elements, observation.Scope, string.Join("\n", observation.ObservedText),
            DateTimeOffset.UtcNow.AddSeconds(20), observation.CandidateSetComplete,
            observation.CandidateSetIssue);

        try
        {
            var decision = await controller.DecideAsync(request);
            Assert.Equal("frame-generic:visual:ready", decision.TargetElementId);
            Assert.Equal(1, handler.Calls);
            using var json = JsonDocument.Parse(handler.RequestJson!);
            var stateCandidate = json.RootElement.GetProperty("state").GetProperty("candidates")[0];
            Assert.Equal(GameJevCandidateMapper.CallerVisualSource, stateCandidate.GetProperty("metadata").GetProperty("source").GetString());
            Assert.False(stateCandidate.GetProperty("metadata").GetProperty("pixel_anchor_detected").GetBoolean());
            Assert.Equal("caller_declared_coverage_unverified", stateCandidate.GetProperty("metadata").GetProperty("candidate_set_completeness_source").GetString());
            Assert.Equal("caller_asserted_complete_unverified", stateCandidate.GetProperty("metadata").GetProperty("coverage_status").GetString());
            Assert.Equal("candidate_schema_and_frame_geometry_validated", stateCandidate.GetProperty("metadata").GetProperty("processing_integrity").GetString());
            Assert.Equal("Unit-test fixture note; parser must preserve but not verify it.", stateCandidate.GetProperty("metadata").GetProperty("coverage_evidence").GetString());
            Assert.Equal(JsonValueKind.Null, stateCandidate.GetProperty("enabled").ValueKind);
            var target = json.RootElement.GetProperty("questions").GetProperty("recommend_target_target").GetProperty("criteria").GetProperty("1");
            Assert.Equal(1185, target.GetProperty("metadata").GetProperty("bbox").GetProperty("x").GetInt32());
            Assert.Equal(1225, target.GetProperty("metadata").GetProperty("screen_bbox").GetProperty("x").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
        }
    }

    [Fact]
    public async Task IncompleteRecommendationAdvisoryKeepsCoverageUnknownAndUsesOnePost()
    {
        const string keyName = "JEV_TEST_KEY_IncompleteVisualAdvisory";
        Environment.SetEnvironmentVariable(keyName, "test-key");
        const string response = """
            {"model":"jev-test","answers":{"operation":{"choice":"RECOMMEND_TARGET","confidence":0.8,"probabilities":{"RECOMMEND_TARGET":0.8,"REOBSERVE":0.1,"ASK_USER":0.05,"STOP":0.05}},"recommend_target_target":{"choice":"1","confidence":0.6,"probabilities":{"1":0.5,"2":0.5}}}}
            """;
        var handler = new FakeHandler(response);
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            ApiKeyEnvironmentVariable = keyName,
            MaxRetries = 4,
            ScoreDisambiguationMargin = 1,
        });
        var elements = new[]
        {
            new JevElement("frame:visual:one", "icon", "blue icon", null, true, [JevRecommendationRequestBuilder.RecommendTargetOperation]),
            new JevElement("frame:visual:two", "icon", "red icon", null, true, [JevRecommendationRequestBuilder.RecommendTargetOperation]),
        };
        var request = JevRecommendationRequestBuilder.Build("Compare the visible icons.", "frame", elements,
            "Visible icons only; advisory recommendation; no input.", null, DateTimeOffset.UtcNow.AddSeconds(20),
            candidateSetComplete: false, candidateSetIssue: "lower toolbar may be occluded",
            allowIncompleteCandidateSetForRecommendationOnly: true, allowScoreDisambiguation: false, maxRetriesOverride: 0);

        try
        {
            var decision = await controller.DecideAsync(request);
            Assert.Equal("RECOMMEND_TARGET", decision.Operation);
            Assert.Equal("frame:visual:one", decision.TargetElementId);
            Assert.Equal(1, handler.Calls);
            using var json = JsonDocument.Parse(handler.RequestJson!);
            var state = json.RootElement.GetProperty("state");
            Assert.False(state.GetProperty("candidate_set_complete").GetBoolean());
            Assert.Equal("lower toolbar may be occluded", state.GetProperty("candidate_set_issue").GetString());
            Assert.False(json.RootElement.GetProperty("questions").TryGetProperty("goal_reached", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
        }
    }

    [Fact]
    public async Task LocalFastCandidatesSendFrameBoundBoxesAsIncompleteJevAdvisory()
    {
        const string keyName = "JEV_TEST_KEY_LocalFastCandidates";
        Environment.SetEnvironmentVariable(keyName, "test-key");
        var candidates = new List<GameUiCandidate>
        {
            new("frame-local:fast:edge-001", "edge-001", "possible_visual_region",
                "compact icon-like region; purpose unknown", 40, 30, 40, 24, 60, 42,
                false, 0.4, "fast_edge_component", "unverified_visual_candidate", "unverified_visual_description",
                IdentitySource: "fast_edge_component"),
        };
        for (int i = 1; i < 168; i++)
        {
            int x = (i % 16) * 5, y = (i / 16) * 5;
            candidates.Add(new GameUiCandidate($"frame-local:fast:edge-{i + 1:D3}", $"edge-{i + 1:D3}",
                "possible_visual_region", "possible visual region; purpose unknown", x, y, 4, 3,
                x + 2, y + 1, false, 0.1, "fast_edge_component", "unverified_visual_candidate",
                "unverified_visual_description", IdentitySource: "fast_edge_component"));
        }
        var observation = new GameUiObservation("frame-local", DateTimeOffset.UtcNow, "target-test",
            160, 120, 80, 60, 0.5, 96,
            new NativeMethods.RECT { Left = 100, Top = 200, Right = 260, Bottom = 320 },
            "fast-local-visual-candidate-set-v1", "Visible possible UI-like regions from local edge proposals",
            false, "Local proposals are incomplete; coverage and actionability are unknown.", [], candidates, [],
            "local_heuristic_proposals_incomplete", CandidateGenerator: "ShadowUse.FastUiCandidateDetector:edge");
        var elements = GameJevCandidateMapper.Map(observation, source: GameJevCandidateMapper.LocalFastVisualSource);
        var targetProbabilities = Enumerable.Range(1, elements.Count)
            .ToDictionary(index => index.ToString(), index => index == 1 ? 1.0 : 0.0);
        string response = JsonSerializer.Serialize(new
        {
            model = "jev-test",
            answers = new Dictionary<string, object>
            {
                ["operation"] = new { choice = "RECOMMEND_TARGET", confidence = 0.8, probabilities = new { RECOMMEND_TARGET = 0.8, REOBSERVE = 0.1, ASK_USER = 0.05, STOP = 0.05 } },
                ["recommend_target_target"] = new { choice = "1", confidence = 0.6, probabilities = targetProbabilities },
            },
        });
        var handler = new FakeHandler(response);
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            ApiKeyEnvironmentVariable = keyName,
            MaxRetries = 0,
        });
        var request = JevRecommendationRequestBuilder.Build("Recommend a visible control; do not activate it.",
            observation.ObservationId, elements,
            observation.Scope + " " + observation.CandidateSetIssue + " Recommendation only.", null,
            DateTimeOffset.UtcNow.AddSeconds(20), candidateSetComplete: observation.CandidateSetComplete,
            candidateSetIssue: observation.CandidateSetIssue,
            allowIncompleteCandidateSetForRecommendationOnly: true, maxRetriesOverride: 0);

        try
        {
            var decision = await controller.DecideAsync(request);
            Assert.Equal("RECOMMEND_TARGET", decision.Operation);
            Assert.Equal("frame-local:fast:edge-001", decision.TargetElementId);
            Assert.Equal(1, handler.Calls);
            using var json = JsonDocument.Parse(handler.RequestJson!);
            var root = json.RootElement;
            var state = root.GetProperty("state");
            Assert.False(state.GetProperty("candidate_set_complete").GetBoolean());
            Assert.Equal(observation.CandidateSetIssue, state.GetProperty("candidate_set_issue").GetString());
            var serializedCandidates = state.GetProperty("candidates");
            Assert.Equal(168, serializedCandidates.GetArrayLength());
            var candidate = serializedCandidates[0];
            var metadata = candidate.GetProperty("metadata");
            Assert.Equal(GameJevCandidateMapper.LocalFastVisualSource, metadata.GetProperty("source").GetString());
            Assert.Equal("local_heuristic_incomplete", metadata.GetProperty("coverage_status").GetString());
            Assert.Equal("local_heuristic_proposals_incomplete", metadata.GetProperty("candidate_set_completeness_source").GetString());
            Assert.Equal(40, metadata.GetProperty("bbox").GetProperty("x").GetInt32());
            Assert.Equal(30, metadata.GetProperty("bbox").GetProperty("y").GetInt32());
            Assert.Equal(180, metadata.GetProperty("screen_bbox").GetProperty("x").GetInt32());
            Assert.Equal(260, metadata.GetProperty("screen_bbox").GetProperty("y").GetInt32());
            Assert.Equal("unverified_visual_candidate", metadata.GetProperty("actionability").GetString());
            Assert.Equal(JsonValueKind.Null, candidate.GetProperty("enabled").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
        }
    }

    [Fact]
    public async Task Fused239CandidateJsonParsesAndReachesFakeJevPostAsIncomplete()
    {
        const string keyName = "JEV_TEST_KEY_FusedVisualCandidates";
        Environment.SetEnvironmentVariable(keyName, "test-key");
        const string frameId = "frame-fused-anvil";
        var candidateJson = JsonSerializer.Serialize(new
        {
            frame_id = frameId,
            image_width = 2559,
            image_height = 1437,
            scope = "Visible UI-like regions proposed by local detector fusion",
            candidate_set_complete = false,
            candidate_set_issue = "Fused local proposals are truncated and unverified.",
            observed_text = Array.Empty<string>(),
            candidates = Enumerable.Range(0, 239).Select(index => new
            {
                key = $"fused-{index + 1:D3}",
                role = "possible_visual_region",
                label = "possible UI region; purpose unknown",
                identity_source = "local_detector_fusion",
                bbox = new { x = index % 40 * 60, y = index / 40 * 40, width = 32, height = 22 },
            }).ToArray(),
        });
        Assert.True(ExternalGameVisualCandidateSetParser.TryParse(candidateJson, frameId, 2559, 1437,
            out var parsed, out var parseError), parseError);
        Assert.NotNull(parsed);
        var observation = new GameUiObservation(frameId, DateTimeOffset.UtcNow, "target-test",
            2559, 1437, 2559, 1437, 1, 96,
            new NativeMethods.RECT { Left = 50, Top = 100, Right = 2609, Bottom = 1537 },
            "caller-visual-candidates-v1", parsed!.Scope, parsed.CandidateSetComplete,
            parsed.CandidateSetIssue, parsed.ObservedText, parsed.Candidates, parsed.ContextRegions,
            "caller_reported_incomplete_coverage");
        var elements = GameJevCandidateMapper.Map(observation, source: GameJevCandidateMapper.CallerVisualSource);
        var targetProbabilities = Enumerable.Range(1, elements.Count)
            .ToDictionary(index => index.ToString(), index => index == 1 ? 1.0 : 0.0);
        var response = JsonSerializer.Serialize(new
        {
            model = "jev-test",
            answers = new Dictionary<string, object>
            {
                ["operation"] = new { choice = "RECOMMEND_TARGET", confidence = 0.8, probabilities = new { RECOMMEND_TARGET = 0.8, REOBSERVE = 0.1, ASK_USER = 0.05, STOP = 0.05 } },
                ["recommend_target_target"] = new { choice = "1", confidence = 0.6, probabilities = targetProbabilities },
            },
        });
        var handler = new FakeHandler(response);
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            ApiKeyEnvironmentVariable = keyName,
            MaxRetries = 0,
        });
        var request = JevRecommendationRequestBuilder.Build("Recommend a likely visible toolbar target; do not activate it.",
            observation.ObservationId, elements,
            observation.Scope + " " + observation.CandidateSetIssue + " Advisory only.", null,
            DateTimeOffset.UtcNow.AddSeconds(20), candidateSetComplete: false,
            candidateSetIssue: observation.CandidateSetIssue,
            allowIncompleteCandidateSetForRecommendationOnly: true, maxRetriesOverride: 0);

        try
        {
            var decision = await controller.DecideAsync(request);
            Assert.Equal("RECOMMEND_TARGET", decision.Operation);
            Assert.Equal($"{frameId}:visual:fused-001", decision.TargetElementId);
            Assert.Equal(1, handler.Calls);
            using var json = JsonDocument.Parse(handler.RequestJson!);
            var state = json.RootElement.GetProperty("state");
            Assert.False(state.GetProperty("candidate_set_complete").GetBoolean());
            Assert.Equal(239, state.GetProperty("candidates").GetArrayLength());
            var candidate = state.GetProperty("candidates")[0];
            Assert.Equal(50, candidate.GetProperty("metadata").GetProperty("screen_bbox").GetProperty("x").GetInt32());
            Assert.Equal("unverified_visual_candidate", candidate.GetProperty("metadata").GetProperty("actionability").GetString());
            Assert.Equal(JsonValueKind.Null, candidate.GetProperty("enabled").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
        }
    }

    [Fact]
    public async Task UiaAndLocalVisualBoundingBoxesReachJevQuestionContext()
    {
        const string keyName = "JEV_TEST_KEY_UiaAndLocalBounds";
        Environment.SetEnvironmentVariable(keyName, "test-key");
        var elements = new[]
        {
            new JevElement("uia:save", "Button", "Save", null, true,
                [JevRecommendationRequestBuilder.RecommendTargetOperation],
                new Dictionary<string, object?>
                {
                    ["source"] = "uia",
                    ["coordinate_space"] = "screen_pixels",
                    ["bbox"] = new { x = 110, y = 220, width = 30, height = 40 },
                }),
            new JevElement("frame:local:region-1", "interactive_region", "possible interactive region", null, null,
                [JevRecommendationRequestBuilder.RecommendTargetOperation],
                new Dictionary<string, object?>
                {
                    ["source"] = GameJevCandidateMapper.LocalYoloVisualSource,
                    ["coordinate_space"] = "screenshot_image_pixels",
                    ["bbox"] = new { x = 150, y = 250, width = 20, height = 20 },
                    ["screen_bbox"] = new { x = 350, y = 450, width = 20, height = 20 },
                }),
        };
        var responseJson = JsonSerializer.Serialize(new
        {
            model = "jev-test",
            answers = new Dictionary<string, object>
            {
                ["operation"] = new { choice = "RECOMMEND_TARGET", confidence = 0.8, probabilities = new { RECOMMEND_TARGET = 0.8, REOBSERVE = 0.1, ASK_USER = 0.05, STOP = 0.05 } },
                ["recommend_target_target"] = new { choice = "2", confidence = 0.7, probabilities = new Dictionary<string, double> { ["1"] = 0.2, ["2"] = 0.8 } },
            },
        });
        var handler = new FakeHandler(responseJson);
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            ApiKeyEnvironmentVariable = keyName,
            MaxRetries = 0,
        });
        var request = JevRecommendationRequestBuilder.Build(
            "Recommend a relevant visible control; do not activate it.", "obs-uia-local", elements,
            "Accessible control and local visual proposals; advice only.", null,
            DateTimeOffset.UtcNow.AddSeconds(20), candidateSetComplete: false,
            candidateSetIssue: "The local visual proposals may omit targets.",
            allowIncompleteCandidateSetForRecommendationOnly: true, maxRetriesOverride: 0);

        try
        {
            var decision = await controller.DecideAsync(request);
            Assert.Equal("frame:local:region-1", decision.TargetElementId);
            using var json = JsonDocument.Parse(handler.RequestJson!);
            var stateCandidates = json.RootElement.GetProperty("state").GetProperty("candidates");
            Assert.Equal(2, stateCandidates.GetArrayLength());
            Assert.Equal(110, stateCandidates[0].GetProperty("metadata").GetProperty("bbox").GetProperty("x").GetInt32());
            Assert.Equal(150, stateCandidates[1].GetProperty("metadata").GetProperty("bbox").GetProperty("x").GetInt32());

            var targetCriteria = json.RootElement.GetProperty("questions")
                .GetProperty("recommend_target_target").GetProperty("criteria");
            var uiaMetadata = targetCriteria.GetProperty("1").GetProperty("metadata");
            Assert.Equal("uia", uiaMetadata.GetProperty("source").GetString());
            Assert.Equal(110, uiaMetadata.GetProperty("bbox").GetProperty("x").GetInt32());
            var localMetadata = targetCriteria.GetProperty("2").GetProperty("metadata");
            Assert.Equal(GameJevCandidateMapper.LocalYoloVisualSource, localMetadata.GetProperty("source").GetString());
            Assert.Equal(150, localMetadata.GetProperty("bbox").GetProperty("x").GetInt32());
            Assert.Equal(350, localMetadata.GetProperty("screen_bbox").GetProperty("x").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
        }
    }

    [Fact]
    public async Task IncompleteAdvisoryCannotCarryExecutableOperations()
    {
        const string keyName = "JEV_TEST_KEY_IncompleteUnsafeAdvisory";
        Environment.SetEnvironmentVariable(keyName, "test-key");
        var handler = new FakeHandler("{}");
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            ApiKeyEnvironmentVariable = keyName,
        });
        var request = JevRecommendationRequestBuilder.Build("Inspect this partial set.", "frame",
            [new JevElement("x", "button", "X", null, null, ["CLICK"])],
            "advisory", null, DateTimeOffset.UtcNow.AddSeconds(20), candidateSetComplete: false,
            candidateSetIssue: "unverified controls may be missing",
            allowIncompleteCandidateSetForRecommendationOnly: true) with
        {
            OperationDescriptions = new Dictionary<string, string> { ["CLICK"] = "Click a target." },
        };

        try
        {
            await Assert.ThrowsAsync<JevValidationException>(() => controller.DecideAsync(request));
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
        }
    }

    [Fact]
    public async Task RecommendationRetryOverridePreventsSecondPost()
    {
        const string keyName = "JEV_TEST_KEY_RecommendationRetryOverride";
        Environment.SetEnvironmentVariable(keyName, "test-key");
        var handler = new StatusHandler(HttpStatusCode.TooManyRequests);
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            ApiKeyEnvironmentVariable = keyName,
            MaxRetries = 3,
        });
        var request = JevRecommendationRequestBuilder.Build("Choose one.", "frame",
            [new JevElement("x", "button", "X", null, true, [JevRecommendationRequestBuilder.RecommendTargetOperation])],
            "decision only", null, DateTimeOffset.UtcNow.AddSeconds(20), candidateSetComplete: true,
            maxRetriesOverride: 0);

        try
        {
            await Assert.ThrowsAsync<JevUnavailableException>(() => controller.DecideAsync(request));
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
        }
    }

    [Fact]
    public void GenericCandidateParserRejectsOutOfBoundsAndPreservesIncompleteCoverage()
    {
        const string invalid = """
            {"frame_id":"f","image_width":100,"image_height":100,"scope":"test","candidate_set_complete":true,"candidates":[{"key":"x","role":"button","label":"X","bbox":{"x":99,"y":0,"width":2,"height":2}}]}
            """;
        Assert.False(ExternalGameVisualCandidateSetParser.TryParse(invalid, "f", 100, 100, out _, out var error));
        Assert.Contains("bbox", error);

        const string incomplete = """
            {"frame_id":"f","image_width":100,"image_height":100,"scope":"known regions only","candidate_set_complete":false,"candidate_set_issue":"bottom region occluded","candidates":[{"key":"x","role":"button","label":"X","bbox":{"x":1,"y":2,"width":3,"height":4}}]}
            """;
        Assert.True(ExternalGameVisualCandidateSetParser.TryParse(incomplete, "f", 100, 100, out var parsed, out error), error);
        Assert.False(parsed!.CandidateSetComplete);
        Assert.Equal("bottom region occluded", parsed.CandidateSetIssue);

        const string incompleteWithoutIssue = """
            {"frame_id":"f","image_width":100,"image_height":100,"scope":"known regions only","candidate_set_complete":false,"candidates":[{"key":"x","role":"button","label":"X","bbox":{"x":1,"y":2,"width":3,"height":4}}]}
            """;
        Assert.False(ExternalGameVisualCandidateSetParser.TryParse(incompleteWithoutIssue, "f", 100, 100, out _, out error));
        Assert.Contains("candidate_set_issue", error);

        const string completeWithoutEvidence = """
            {"frame_id":"f","image_width":100,"image_height":100,"scope":"all controls","candidate_set_complete":true,"candidates":[{"key":"x","role":"button","label":"X","bbox":{"x":1,"y":2,"width":3,"height":4}}]}
            """;
        Assert.False(ExternalGameVisualCandidateSetParser.TryParse(completeWithoutEvidence, "f", 100, 100, out _, out error));
        Assert.Contains("coverage_evidence", error);

        var tooMany = JsonSerializer.Serialize(new
        {
            frame_id = "f",
            image_width = 100,
            image_height = 100,
            scope = "screen",
            candidate_set_complete = true,
            candidates = Enumerable.Range(0, 256).Select(index => new
            {
                key = $"c{index}", role = "icon", label = "unknown icon",
                bbox = new { x = 0, y = 0, width = 1, height = 1 },
            }),
        });
        Assert.False(ExternalGameVisualCandidateSetParser.TryParse(tooMany, "f", 100, 100, out _, out error));
        Assert.Contains("0-255", error);

        const string emptyCandidates = """
            {"frame_id":"f","image_width":100,"image_height":100,"scope":"screen","candidate_set_complete":true,"coverage_evidence":"entire screen inspected","candidates":[]}
            """;
        Assert.True(ExternalGameVisualCandidateSetParser.TryParse(emptyCandidates, "f", 100, 100, out var emptySet, out error), error);
        Assert.Empty(emptySet!.Candidates);

        const string onePixelAtEdge = """
            {"frame_id":"f","image_width":100,"image_height":100,"scope":"screen edge","candidate_set_complete":true,"coverage_evidence":"edge inspected","candidates":[{"key":"corner","role":"button","label":"Corner control","bbox":{"x":99,"y":99,"width":1,"height":1}}]}
            """;
        Assert.True(ExternalGameVisualCandidateSetParser.TryParse(onePixelAtEdge, "f", 100, 100, out var edgeSet, out error), error);
        Assert.Equal(99, Assert.Single(edgeSet!.Candidates).X);
    }

    private static GameUiObservation DetectSyntheticSelectionScreen()
    {
        const int width = 1722, height = 999;
        int[] centers = [291, 408, 526, 644, 762, 880, 998, 1116];
        using var frame = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(frame))
        using (var yellow = new Pen(Color.FromArgb(255, 220, 20), 5))
        using (var green = new Pen(Color.FromArgb(123, 188, 132), 5))
        {
            graphics.Clear(Color.FromArgb(90, 60, 40));
            for (int row = 1; row <= 2; row++)
            {
                int y = row == 1 ? 570 : 727;
                int bottom = row == 1 ? 710 : 862;
                for (int column = 1; column <= 8; column++)
                    graphics.DrawRectangle(yellow, centers[column - 1] - 52, y, 103, bottom - y - 1);
            }
            graphics.FillRectangle(Brushes.Yellow, 1196, 582, 290, 121);
            graphics.FillRectangle(Brushes.Yellow, 1196, 757, 290, 122);
            graphics.FillRectangle(Brushes.White, 620, 128, 47, 70);
            graphics.FillRectangle(Brushes.White, 1057, 128, 44, 70);
            graphics.FillRectangle(Brushes.White, 865, 862, 26, 26);
            graphics.DrawRectangle(green, 229, 49, 458, 63);
            graphics.DrawRectangle(green, 1035, 49, 459, 63);
        }
        return BloonsTdb2SelectionGrounding.Detect(
            frame,
            "frame-8",
            DateTimeOffset.UtcNow,
            "target-test",
            0.975609756097561,
            96,
            new NativeMethods.RECT { Left = 10, Top = 20, Right = width + 10, Bottom = height + 20 },
            ["Cancel", "Ready!"]);
    }
}
