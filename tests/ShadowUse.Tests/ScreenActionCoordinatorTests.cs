using System.Drawing;
using System.Net;
using System.Text;
using System.Text.Json;
using ShadowUse.Automation;
using ShadowUse.Jev;
using ShadowUse.Native;
using ShadowUse.Tools;
using ShadowUse.Vision;

namespace ShadowUse.Tests;

public sealed class ScreenActionCoordinatorTests
{
    private const string CatalogJson = """
        {
          "app_id":"test-game",
          "app_version":"1.2.3",
          "profile":"default",
          "keyboard_layout":"us",
          "actions":[
            {"action_id":"open_battle","label":"Open battle","key_sequence":["enter"],"screen_id":"main-menu","target_aliases":["Battle"],"source_kind":"local_config","source_reference":"settings/controls.ini","source_hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","evidence_status":"source_attested","preconditions":"main menu"},
            {"action_id":"pause","label":"Pause game","key_sequence":["escape"],"screen_id":"gameplay","target_aliases":[],"source_kind":"official_documentation","source_reference":"https://example.org/game-controls","source_hash":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","evidence_status":"source_attested","preconditions":"gameplay"}
          ]
        }
        """;

    [Fact]
    public void CatalogImportValidatesScopedSourcesAndDetectsConflictingBindings()
    {
        var catalog = new AppActionCatalog();
        var imported = catalog.Import(CatalogJson);
        Assert.True(imported.Imported, imported.Error);
        Assert.Equal(2, imported.ActionCount);
        Assert.Equal(0, imported.ConflictCount);

        var lookup = catalog.Lookup("test-game", "1.2.3", "default", "us");
        Assert.True(lookup.Found);
        Assert.False(lookup.Advisory);
        Assert.Equal(imported.Revision, lookup.Snapshot!.Revision);

        var mismatch = catalog.Lookup("test-game", "2.0", "default", "us");
        Assert.True(mismatch.Advisory);
        Assert.Equal("app_version_mismatch", mismatch.Reason);

        string conflictJson = CatalogJson.Replace("\"action_id\":\"pause\"", "\"action_id\":\"open_battle\"", StringComparison.Ordinal)
            .Replace("\"screen_id\":\"gameplay\"", "\"screen_id\":\"main-menu\"", StringComparison.Ordinal);
        var conflict = new AppActionCatalog();
        var conflictResult = conflict.Import(conflictJson);
        Assert.True(conflictResult.Imported, conflictResult.Error);
        Assert.Equal(1, conflictResult.ConflictCount);
        var conflictedActions = conflict.Lookup("test-game", "1.2.3", "default", "us").Snapshot!.Actions;
        Assert.Contains("open_battle", AppActionCatalog.GetConflicts(conflictedActions, "main-menu"));
        Assert.Contains("open_battle", AppActionCatalog.GetAllConflicts(conflictedActions));
        string disjointScopes = conflictJson.Replace("\"label\":\"Pause game\",\"key_sequence\":[\"escape\"],\"screen_id\":\"main-menu\"",
            "\"label\":\"Pause game\",\"key_sequence\":[\"escape\"],\"screen_id\":\"gameplay\"", StringComparison.Ordinal);
        var disjointResult = new AppActionCatalog().Import(disjointScopes);
        Assert.True(disjointResult.Imported, disjointResult.Error);
        Assert.Equal(0, disjointResult.ConflictCount);

        string unsafeSource = CatalogJson.Replace("settings/controls.ini", "../../.env", StringComparison.Ordinal);
        Assert.False(new AppActionCatalog().Import(unsafeSource).Imported);
    }

    [Fact]
    public void StateStoreCachesSemanticRecipesButNeverOldCoordinatesAndLinksReportedTransitions()
    {
        var store = new ScreenStateStore();
        var first = store.Observe("test-game", "1", "default", "us", "menu",
            Capture("frame-1", "Main menu", new ScreenActionRecipe("battle", "button", "Battle")));
        Assert.False(first.Repeated);

        var same = store.Observe("test-game", "1", "default", "us", "menu",
            Capture("frame-2", "Main menu", new ScreenActionRecipe("battle", "button", "Battle")));
        Assert.Equal(first.ScreenId, same.ScreenId);
        Assert.True(same.Repeated);
        Assert.DoesNotContain("bbox", JsonSerializer.Serialize(same.CachedActions));

        Assert.True(store.RecordInput("test-game", "frame-2", "open_battle", "key", inputSent: true));
        var newState = store.Observe("test-game", "1", "default", "us", "battle-mode",
            Capture("frame-3", "Battle mode", new ScreenActionRecipe("solo", "button", "Solo")));
        Assert.NotEqual(first.ScreenId, newState.ScreenId);
        Assert.False(newState.Stable);
        Assert.Empty(newState.IncomingTransitions); // Withhold transition guidance until the new screen repeats.
        var next = store.Observe("test-game", "1", "default", "us", "battle-mode",
            Capture("frame-4", "Battle mode", new ScreenActionRecipe("solo", "button", "Solo")));
        Assert.True(next.Stable);
        var transition = Assert.Single(next.IncomingTransitions);
        Assert.Equal(first.ScreenId, transition.FromScreenId);
        Assert.Equal(next.ScreenId, transition.ToScreenId);
        Assert.Equal("open_battle", transition.ActionId);
        Assert.Contains("unverified", transition.EvidenceStatus);
        Assert.Contains("input_sent=true", Assert.Single(store.RecentActions("test-game")));
    }

    [Fact]
    public void EnabledAndKeyBindingChangesInvalidateSemanticScreenIdentity()
    {
        var store = new ScreenStateStore();
        var enabled = store.Observe("native-app", "1", "default", "us", "settings",
            new ScreenStateCapture("uia-1", "Settings", true, null,
                [new ScreenActionRecipe("apply", "button", "Apply", ["Alt+A"], Enabled: true)]));
        var disabled = store.Observe("native-app", "1", "default", "us", "settings",
            new ScreenStateCapture("uia-2", "Settings", true, null,
                [new ScreenActionRecipe("apply", "button", "Apply", ["Alt+A"], Enabled: false)]));
        var remapped = store.Observe("native-app", "1", "default", "us", "settings",
            new ScreenStateCapture("uia-3", "Settings", true, null,
                [new ScreenActionRecipe("apply", "button", "Apply", ["Alt+P"], Enabled: false)]));

        Assert.NotEqual(enabled.ScreenId, disabled.ScreenId);
        Assert.NotEqual(disabled.ScreenId, remapped.ScreenId);
        Assert.False(disabled.Repeated);
        Assert.False(remapped.Repeated);
    }

    [Fact]
    public void DynamicStateCluesAffectIdentityWithoutPersistingRawText()
    {
        var store = new ScreenStateStore();
        var first = store.Observe("game", "1", "default", "us", "menu",
            new ScreenStateCapture("f1", "menu", false, "incomplete", [], ["dynamic private clue alpha"]));
        var second = store.Observe("game", "1", "default", "us", "menu",
            new ScreenStateCapture("f2", "menu", false, "incomplete", [], ["dynamic private clue beta"]));

        Assert.NotEqual(first.ScreenId, second.ScreenId);
        Assert.DoesNotContain("dynamic private clue alpha", JsonSerializer.Serialize(store.ScreensFor("game")));
        Assert.DoesNotContain("dynamic private clue beta", JsonSerializer.Serialize(store.ScreensFor("game")));
    }

    [Fact]
    public void CachedScreenReuseRefusesSharedAnchorsUnlessCallerSuppliesExactScreenHint()
    {
        var store = new ScreenStateStore();
        const string layout = "same-layout";
        ScreenActionRecipe[] anchors =
        [
            new("nav-battle", "button", "Battle", Anchor: new ScreenUiAnchor(.05, .25, .1, .05, "0123456789abcdef")),
            new("top-menu", "button", "Menu", Anchor: new ScreenUiAnchor(.9, .02, .05, .05, "fedcba9876543210")),
        ];
        for (int i = 0; i < 3; i++)
        {
            store.Observe("shared-app", "1", "default", "us", "main-menu",
                new ScreenStateCapture($"main-{i}", "navigation", false, null, anchors, LayoutFingerprint: layout));
            store.Observe("shared-app", "1", "default", "us", "heroes",
                new ScreenStateCapture($"heroes-{i}", "navigation", false, null, anchors, LayoutFingerprint: layout));
        }

        Assert.False(store.TryMatchCachedScreen("shared-app", "1", "default", "us", layout,
            _ => true, out _, out _));
        Assert.True(store.TryMatchCachedScreen("shared-app", "1", "default", "us", layout,
            _ => true, out var menu, out _, screenHint: "main-menu"));
        Assert.Equal("main-menu", menu!.ScreenHint);
        Assert.True(store.TryMatchCachedScreen("shared-app", "1", "default", "us", layout,
            _ => true, out var heroes, out _, screenHint: "heroes"));
        Assert.Equal("heroes", heroes!.ScreenHint);
    }

    [Fact]
    public void StaleReportedInputDoesNotCreateScreenTransition()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new ScreenStateStore(clock);
        var before = store.Observe("test-game", "1", "default", "us", "menu",
            Capture("frame-old", "menu", new ScreenActionRecipe("start", "button", "Start")));
        Assert.True(store.RecordInput("test-game", "frame-old", "start", "key", inputSent: true));
        clock.Advance(TimeSpan.FromSeconds(31));
        var after = store.Observe("test-game", "1", "default", "us", "level",
            Capture("frame-new", "level", new ScreenActionRecipe("pause", "button", "Pause")));
        Assert.NotEqual(before.ScreenId, after.ScreenId);
        Assert.Empty(after.IncomingTransitions);
        Assert.Equal(0, store.TransitionCount);
    }

    [Fact]
    public async Task SourcedKeyOptionAndUnboundBoxFlowThroughFakeJevIntoFrameBoundPlans()
    {
        var catalog = new AppActionCatalog();
        Assert.True(catalog.Import(CatalogJson).Imported);
        var lookup = catalog.Lookup("test-game", "1.2.3", "default", "us");
        var observation = Observation();
        var state = new ScreenStateStore().Observe("test-game", "1.2.3", "default", "us", "main-menu",
            new ScreenStateCapture(observation.ObservationId, observation.Scope, false, observation.CandidateSetIssue,
                observation.Candidates.Select(candidate => new ScreenActionRecipe(candidate.Key, candidate.Role, candidate.Label)).ToArray()));
        var mapped = GameJevCandidateMapper.Map(observation, source: GameJevCandidateMapper.CallerVisualSource);
        var options = observation.Candidates.Select(candidate =>
        {
            var element = mapped.Single(item => item.Id == candidate.Id);
            return new ScreenActionOption(element, candidate.Key, [candidate.Key, candidate.Label],
                new ScreenInputPlan(element.Id, candidate.Key, "click", [], observation.ObservationId, lookup.Snapshot!.Revision,
                    candidate.X, candidate.Y, candidate.Width, candidate.Height, "fresh box"));
        }).ToArray();
        var plan = ScreenActionCoordinator.Build("test-game", "open the battle screen", "main menu",
            "1.2.3", "default", "us", state.ScreenId, state.ScreenHint, state.Note, observation.ObservationId,
            FrameBinding(observation.ObservationId), observation.Scope, adapterClaimsComplete: false,
            observation.CandidateSetIssue, observation.ObservedText, options, lookup,
            recentActions: ["loaded app"], storedRecentActions: ["opened launcher"]);

        Assert.False(plan.Request.CandidateSetComplete);
        Assert.True(plan.Request.AllowIncompleteCandidateSetForRecommendationOnly);
        Assert.Contains(plan.Request.KeyBindings!, binding => binding.Code == "open_battle");
        Assert.Contains("preconditions=main menu", plan.Request.KeyBindings!.Single(binding => binding.Code == "open_battle").Description);
        var mappedBattle = plan.Request.Elements.Single(element => element.Extra is { } extra
            && extra.TryGetValue("app_action_id", out var actionId)
            && string.Equals(actionId as string, "open_battle", StringComparison.Ordinal));
        Assert.Equal("main menu", mappedBattle.Extra!["binding_preconditions"]);
        Assert.Contains("loaded app", plan.Request.RecentActions!);
        Assert.Contains("opened launcher", plan.Request.RecentActions!);
        Assert.Contains("User intention: open the battle screen.", plan.Request.Context);
        Assert.Contains("Caller-believed state (unverified): main menu.", plan.Request.Context);
        Assert.Contains("coverage", plan.Request.CandidateSetIssue, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No input will be dispatched", plan.Request.Context);
        Assert.Equal(2, plan.Request.Elements.Count); // Pause is scoped to gameplay and is not offered on the main menu.

        var keyDecision = await DecideWithFakeJev(plan.Request, "1");
        using (var requestJson = JsonDocument.Parse(keyDecision.Handler.RequestJson!))
        {
            var stateJson = requestJson.RootElement.GetProperty("state");
            Assert.Equal(2, stateJson.GetProperty("candidate_count").GetInt32());
            Assert.False(stateJson.GetProperty("candidate_set_complete").GetBoolean());
            Assert.Contains("User intention: open the battle screen.", stateJson.GetProperty("context").GetString());
            Assert.Contains("Caller-believed state (unverified): main menu.", stateJson.GetProperty("context").GetString());
            Assert.Contains("loaded app", stateJson.GetProperty("recent_actions").ToString());
            Assert.Contains("opened launcher", stateJson.GetProperty("recent_actions").ToString());
            Assert.Contains("enter", stateJson.GetProperty("key_bindings").ToString());
            Assert.Contains("coverage", stateJson.GetProperty("candidate_set_issue").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        var keyResolution = ScreenActionCoordinator.Resolve(keyDecision.Decision, plan, plan.FrameBinding, lookup.Snapshot!.Revision);
        Assert.True(keyResolution.Prepared, keyResolution.Error);
        Assert.Equal("key", keyResolution.Input!.InputKind);
        Assert.Equal("open_battle", keyResolution.Input.ActionId);
        Assert.Equal(["enter"], keyResolution.Input.KeySequence);

        var clickDecision = await DecideWithFakeJev(plan.Request, "2");
        var clickResolution = ScreenActionCoordinator.Resolve(clickDecision.Decision, plan, plan.FrameBinding, lookup.Snapshot.Revision);
        Assert.True(clickResolution.Prepared, clickResolution.Error);
        Assert.Equal("click", clickResolution.Input!.InputKind);
        Assert.Equal(observation.ObservationId, clickResolution.Input.ObservationId);
        Assert.Equal((300, 200, 80, 40), (clickResolution.Input.X, clickResolution.Input.Y,
            clickResolution.Input.Width, clickResolution.Input.Height));

        Assert.False(ScreenActionCoordinator.Resolve(keyDecision.Decision with { ObservationId = "stale-frame" }, plan, plan.FrameBinding, lookup.Snapshot.Revision).Prepared);
        Assert.False(ScreenActionCoordinator.Resolve(keyDecision.Decision, plan, plan.FrameBinding with { DpiAtCapture = 120 }, lookup.Snapshot.Revision).Prepared);
        Assert.False(ScreenActionCoordinator.Resolve(keyDecision.Decision, plan, plan.FrameBinding with { FrameWidth = 799 }, lookup.Snapshot.Revision).Prepared);
        Assert.False(ScreenActionCoordinator.Resolve(keyDecision.Decision, plan, plan.FrameBinding with { TargetLifetime = "new-lifetime" }, lookup.Snapshot.Revision).Prepared);
        Assert.False(ScreenActionCoordinator.Resolve(keyDecision.Decision, plan, plan.FrameBinding with
        {
            MaskedImageBoxes = [new Rectangle(1, 1, 10, 10)],
        }, lookup.Snapshot.Revision).Prepared);
        var maskedPlan = plan with { FrameBinding = plan.FrameBinding with { MaskedImageBoxes = [new Rectangle(1, 1, 10, 10)] } };
        Assert.False(ScreenActionCoordinator.Resolve(keyDecision.Decision, maskedPlan, maskedPlan.FrameBinding,
            lookup.Snapshot.Revision).Prepared);
        var expired = plan with { Request = plan.Request with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) } };
        Assert.False(ScreenActionCoordinator.Resolve(keyDecision.Decision, expired, expired.FrameBinding, lookup.Snapshot.Revision).Prepared);
        Assert.False(ScreenActionCoordinator.Resolve(keyDecision.Decision, plan, plan.FrameBinding, "different-catalog-revision").Prepared);

        var duplicateLabelOptions = options.Select((option, index) => index == 1
            ? option with { Aliases = [.. option.Aliases, "Battle"] }
            : option).ToArray();
        var ambiguous = ScreenActionCoordinator.Build("test-game", "open the battle screen", "main menu",
            "1.2.3", "default", "us", state.ScreenId, state.ScreenHint, state.Note, observation.ObservationId,
            FrameBinding(observation.ObservationId), observation.Scope, false, observation.CandidateSetIssue,
            observation.ObservedText, duplicateLabelOptions, lookup);
        Assert.DoesNotContain(ambiguous.Request.KeyBindings!, binding => binding.Code == "open_battle");
        Assert.All(ambiguous.Request.Elements, element =>
            Assert.True(element.Extra!.TryGetValue("binding_ambiguous", out var value) && value is true));
        var overflow = duplicateLabelOptions.Concat(Enumerable.Repeat(duplicateLabelOptions[0], 254)).ToArray();
        Assert.Throws<ArgumentException>(() => ScreenActionCoordinator.Build("test-game", "open the battle screen", "main menu",
            "1.2.3", "default", "us", state.ScreenId, state.ScreenHint, state.Note, observation.ObservationId,
            FrameBinding(observation.ObservationId), observation.Scope, false, observation.CandidateSetIssue,
            observation.ObservedText, overflow, lookup));
    }

    [Fact]
    public void StaticAnchorsAndStateGraphPersistAcrossStoreInstancesAndRequireExactFrameVerification()
    {
        string path = Path.Combine(Path.GetTempPath(), "dcu-screen-map-" + Guid.NewGuid().ToString("N") + ".json");
        const string layout = "v1|frame=800x600|image=800x600|scale=1|dpi=96";
        try
        {
            var store = new ScreenStateStore(persistencePath: path);
            ScreenActionRecipe[] menuActions =
            [
                new("battle", "button", "Battle", Anchor: new ScreenUiAnchor(.1, .2, .2, .1, "0123456789abcdef", LayoutFingerprint: layout)),
                new("settings", "button", "Settings", Anchor: new ScreenUiAnchor(.5, .2, .2, .1, "fedcba9876543210", LayoutFingerprint: layout)),
            ];
            ScreenStateObservation first = default!;
            for (int i = 1; i <= 3; i++)
                first = store.Observe("generic-game", "1.2", "default", "us", "main-menu",
                    new ScreenStateCapture($"frame-{i}", "menu controls", false, "incomplete", menuActions,
                        CapturedAtUtc: DateTimeOffset.UtcNow.AddMilliseconds(i), LayoutFingerprint: layout,
                        ContextRegions: [new("status", "Status area", .1, .8, .8, .1)]));

            Assert.All(first.CachedActions, action =>
            {
                Assert.True(action.Anchor!.IsStatic);
                Assert.False(action.Anchor.IsDynamic);
            });
            Assert.True(store.RecordInput("generic-game", "frame-3", "open_battle", "key", true));
            var battle = store.Observe("generic-game", "2.0", "default", "us", "battle-menu",
                new ScreenStateCapture("frame-4", "battle menu", false, "incomplete", [], LayoutFingerprint: layout));
            Assert.NotEqual(first.ScreenId, battle.ScreenId);

            Assert.True(store.ReportTransitionOutcome("generic-game", first.ScreenId, "open_battle", battle.ScreenId,
                success: true, confidence: .92, evidence: "Battle menu appeared."));

            var reloaded = new ScreenStateStore(persistencePath: path);
            Assert.Equal(first.ScreenId, reloaded.ScreensFor("generic-game").Single(state => state.ScreenHint == "main-menu").ScreenId);
            var transition = Assert.Single(reloaded.TransitionsFor("generic-game", battle.ScreenId));
            Assert.Equal("success", transition.Outcome);
            Assert.Equal(.92, transition.Confidence);
            Assert.Equal(1, transition.SuccessCount);
            Assert.Contains("Status area", JsonSerializer.Serialize(reloaded.ScreensFor("generic-game")));
            Assert.True(reloaded.TryMatchCachedScreen("generic-game", "1.2", "default", "us", layout,
                _ => true, out var matched, out var anchors));
            Assert.Equal(first.ScreenId, matched!.ScreenId);
            Assert.Equal(2, anchors.Count);
            Assert.False(reloaded.TryMatchCachedScreen("generic-game", "1.2", "default", "us", layout,
                _ => false, out _, out _));
            Assert.False(reloaded.TryMatchCachedScreen("generic-game", "1.2", "default", "us", "different-layout",
                _ => true, out _, out _));
            Assert.True(reloaded.BindFrame("generic-game", first.ScreenId, "cached-frame"));
            Assert.True(reloaded.TryGetForFrame("generic-game", "cached-frame", out _));
            Assert.True(reloaded.RecordInput("generic-game", "cached-frame", "open_battle", "key", inputSent: true));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ChangingAnchorHashOrBoundsPreventsStaticPromotion()
    {
        var store = new ScreenStateStore();
        const string layout = "layout";
        ScreenStateObservation? observed = null;
        for (int i = 0; i < 5; i++)
        {
            string hash = i == 4 ? "ffffffffffffffff" : "0123456789abcdef";
            double x = i == 3 ? .3 : .1;
            observed = store.Observe("app", "1", "default", "unknown", "screen",
                new ScreenStateCapture($"frame-{i}", "scope", false, null,
                    [new("button", "button", "Continue", Anchor: new ScreenUiAnchor(x, .1, .2, .1, hash))],
                    LayoutFingerprint: layout));
        }
        var anchor = Assert.Single(observed!.CachedActions).Anchor!;
        Assert.False(anchor.IsStatic);
        Assert.True(anchor.IsDynamic);
        Assert.Equal(1, anchor.ConsecutiveMatches);
    }

    [Fact]
    public void CachedAnchorHashesMatchStablePixelsAndRejectChangedRegions()
    {
        using var bitmap = new Bitmap(64, 64);
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                bool white = ((x / 8) + (y / 8)) % 2 == 0;
                bitmap.SetPixel(x, y, white ? Color.White : Color.Black);
            }
        var region = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        string hash = GameVision.ComputeRegionHash(bitmap, region).ToString("x16");
        Assert.True(GameVision.RegionHashMatches(bitmap, region, hash));

        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                bool white = ((x / 8) + (y / 8)) % 2 != 0;
                bitmap.SetPixel(x, y, white ? Color.White : Color.Black);
            }
        Assert.False(GameVision.RegionHashMatches(bitmap, region, hash));
    }

    [Fact]
    public void ImportedActionCatalogPersistsAndCanBeInvalidated()
    {
        string path = Path.Combine(Path.GetTempPath(), "dcu-action-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var catalog = new AppActionCatalog(path);
            var imported = catalog.Import(CatalogJson);
            Assert.True(imported.Imported, imported.Error);
            Assert.True(imported.Persisted);
            var reloaded = new AppActionCatalog(path);
            var lookup = reloaded.Lookup("test-game", "1.2.3", "default", "us");
            Assert.True(lookup.Found);
            Assert.Equal(imported.Revision, lookup.Snapshot!.Revision);
            Assert.True(reloaded.Invalidate("test-game", "default", "us"));
            Assert.False(new AppActionCatalog(path).Lookup("test-game", "1.2.3", "default", "us").Found);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static ScreenStateCapture Capture(string frame, string scope, ScreenActionRecipe action)
        => new(frame, scope, false, "test inventory is incomplete", [action]);

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    private static GameScreenshotFrameBinding FrameBinding(string frame)
        => new(frame, DateTimeOffset.UtcNow, "test-lifetime", 800, 600, 800, 600, 1, 96,
            new NativeMethods.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }, true);

    private static GameUiObservation Observation()
    {
        const string frame = "frame-menu";
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 };
        return new GameUiObservation(frame, DateTimeOffset.UtcNow, "target-test", 800, 600, 800, 600, 1, 96,
            rect, "external-visual-candidate-set", "Main menu visible options", false, "unverified test coverage",
            ["Main menu"],
            [
                new GameUiCandidate("frame-menu:visual:battle", "battle", "button", "Battle", 100, 200, 80, 40,
                    140, 220, false, 0, "upstream_bbox_not_locally_verified", "unverified", "unverified", IdentitySource: "test-parser"),
                new GameUiCandidate("frame-menu:visual:settings", "settings", "button", "Settings", 300, 200, 80, 40,
                    340, 220, false, 0, "upstream_bbox_not_locally_verified", "unverified", "unverified", IdentitySource: "test-parser"),
            ], [], "caller_reported_incomplete_coverage", CandidateGenerator: "fake-test-parser");
    }

    private static async Task<(JevDecision Decision, FakeHandler Handler)> DecideWithFakeJev(JevDecideRequest request, string targetChoice)
    {
        const string keyName = "JEV_TEST_KEY_ScreenActionCoordinator";
        Environment.SetEnvironmentVariable(keyName, "fake-key");
        try
        {
            var probabilities = Enumerable.Range(1, request.Elements.Count)
                .ToDictionary(index => index.ToString(), index => index.ToString() == targetChoice ? 0.9 : 0.1 / (request.Elements.Count - 1));
            string response = JsonSerializer.Serialize(new
            {
                model = "jev-test",
                answers = new Dictionary<string, object>
                {
                    ["operation"] = new { choice = "RECOMMEND_TARGET", confidence = 0.9, probabilities = new { RECOMMEND_TARGET = 0.9, REOBSERVE = 0.05, ASK_USER = 0.03, STOP = 0.02 } },
                    ["recommend_target_target"] = new { choice = targetChoice, confidence = 0.9, probabilities },
                },
            });
            var handler = new FakeHandler(response);
            var controller = new JevController(new HttpClient(handler), new JevOptions
            {
                Mode = JevMode.Assist,
                ApiKeyEnvironmentVariable = keyName,
                BaseUrl = "http://jev-fake.invalid",
                MaxRetries = 0,
            });
            var decision = await controller.DecideAsync(request);
            Assert.Equal(1, handler.Calls);
            using var sent = JsonDocument.Parse(handler.RequestJson!);
            Assert.Equal(request.Elements.Count, sent.RootElement.GetProperty("state").GetProperty("candidates").GetArrayLength());
            return (decision, handler);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
        }
    }

    private sealed class FakeHandler(string responseJson) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? RequestJson { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            RequestJson = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
