using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ShadowUse.Automation;
using ShadowUse.Capture;
using ShadowUse.Native;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class ObservationContractTests
{
    [Fact]
    public void ObservationEnvelope_CaptureField_ReportsFullProvenanceOnSuccess()
    {
        // F02-R01: capture id, timestamp, origin, dimensions, coordinate-space scale, and an
        // explicit (null) failure reason must all be reachable on the wire, not just png bytes.
        var snapshot = Snapshot("obs-capture-ok", RegisterTarget());
        var capture = new CaptureEnvelope("cap-abc123", DateTimeOffset.UtcNow, "print_window", null, [1, 2, 3], 640, 480, 0.5);

        var json = Json(snapshot, snapshot.Elements, responseTruncated: false, capture: capture);
        var captureJson = json.RootElement.GetProperty("capture");

        Assert.Equal("cap-abc123", captureJson.GetProperty("capture_id").GetString());
        Assert.Equal("print_window", captureJson.GetProperty("origin").GetString());
        Assert.Equal(JsonValueKind.Null, captureJson.GetProperty("failure_reason").ValueKind);
        Assert.Equal(640, captureJson.GetProperty("width").GetInt32());
        Assert.Equal(480, captureJson.GetProperty("height").GetInt32());
        Assert.Equal(0.5, captureJson.GetProperty("coordinate_space").GetProperty("scale_from_window").GetDouble());
        Assert.True(captureJson.GetProperty("coordinate_space").GetProperty("window_relative").GetBoolean());
        Assert.NotNull(json.RootElement.GetProperty("screenshot_png_base64").GetString());
    }

    [Fact]
    public void ObservationEnvelope_CaptureField_ReportsExplicitFailureReason_NeverASilentSubstitute()
    {
        // F02-R02: a missing image is always an explicit result with a named reason -- never a
        // silently substituted image from elsewhere, and never just a bare null with no context.
        var snapshot = Snapshot("obs-capture-fail", RegisterTarget());
        var capture = new CaptureEnvelope("cap-fail1", DateTimeOffset.UtcNow, "none", "window_minimized", null, null, null, null);

        var json = Json(snapshot, snapshot.Elements, responseTruncated: false, capture: capture);
        var captureJson = json.RootElement.GetProperty("capture");

        Assert.Equal("none", captureJson.GetProperty("origin").GetString());
        Assert.Equal("window_minimized", captureJson.GetProperty("failure_reason").GetString());
        Assert.Equal(JsonValueKind.Null, captureJson.GetProperty("coordinate_space").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("screenshot_png_base64").ValueKind);
    }

    [Fact]
    public void ObservationEnvelope_PreservesLegacyFieldsAndAddsV1Fields()
    {
        var snapshot = Snapshot("obs-legacy", RegisterTarget());
        snapshot.Elements.Add(Element("e1", [42], enabled: true));

        var json = Json(snapshot, snapshot.Elements, responseTruncated: false);

        foreach (var key in new[]
        {
            "revision", "app", "pid", "title", "target_id", "lifetime_epoch", "bounds",
            "foreground_free", "elements", "screenshot_png_base64", "hint"
        })
            Assert.True(json.RootElement.TryGetProperty(key, out _), $"Missing legacy key '{key}'.");
        Assert.Equal("dcu.observation.v1", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(new[] { "uia" }, json.RootElement.GetProperty("sources").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public void ObservationEnvelope_UsesSessionTargetAndLifetimeProvenance()
    {
        var reference = RegisterTarget("provenance-session");
        var snapshot = Snapshot("obs-provenance", reference);
        snapshot.Elements.Add(Element("e1", [1, 2, 3], enabled: true));

        var root = Json(snapshot, snapshot.Elements, responseTruncated: false).RootElement;
        var target = root.GetProperty("target");
        Assert.Equal("native_window", target.GetProperty("kind").GetString());
        Assert.Equal(reference.SessionId, target.GetProperty("session_id").GetString());
        Assert.Equal(UiaService.FormatTargetId(reference.TargetId), target.GetProperty("target_id").GetString());
        Assert.Equal(reference.LifetimeEpoch, target.GetProperty("lifetime_epoch").GetInt64());
        Assert.Equal("123:0xABC", target.GetProperty("window_id").GetString());
        Assert.Equal("0xABC", target.GetProperty("hwnd").GetString());
        Assert.Equal(reference.LifetimeEpoch, root.GetProperty("freshness").GetProperty("target_lifetime_epoch").GetInt64());
    }

    [Fact]
    public void ElementIdentity_UsesRuntimeIdAndTargetLifetimeNotDisplayId()
    {
        var reference = RegisterTarget("identity-session");
        var first = Snapshot("obs-first", reference);
        first.Elements.Add(Element("e1", [42, 8, 1], enabled: true));
        var second = Snapshot("obs-second", reference);
        second.Elements.Add(Element("e99", [42, 8, 1], enabled: true));

        var firstElement = Json(first, first.Elements, responseTruncated: false).RootElement.GetProperty("elements")[0];
        var secondElement = Json(second, second.Elements, responseTruncated: false).RootElement.GetProperty("elements")[0];

        Assert.Equal(
            firstElement.GetProperty("stable_identity").GetProperty("key").GetString(),
            secondElement.GetProperty("stable_identity").GetProperty("key").GetString());
        Assert.NotEqual(firstElement.GetProperty("id").GetString(), secondElement.GetProperty("id").GetString());
        Assert.NotEqual(firstElement.GetProperty("element_ref").GetString(), secondElement.GetProperty("element_ref").GetString());
        Assert.Equal(new[] { 42, 8, 1 }, secondElement.GetProperty("stable_identity").GetProperty("runtime_id").EnumerateArray().Select(v => v.GetInt32()));
    }

    [Fact]
    public void NativeNodeAndDepthTruncationAreIndependentFromResponseTruncation()
    {
        var snapshot = Snapshot("obs-truncated", RegisterTarget());
        snapshot.NativeCompleteness = new NativeSnapshotCompleteness
        {
            ProjectionComplete = false,
            NodeTruncated = true,
            DepthTruncated = false,
            TreeTextTruncated = false,
            PropertyErrors = false,
            MaxNodes = 9,
            MaxDepth = 48,
            TextLimit = 500,
        };
        snapshot.Elements.Add(Element("e1", [1], enabled: true));

        var completeness = Json(snapshot, snapshot.Elements, responseTruncated: false)
            .RootElement.GetProperty("completeness");
        var native = completeness.GetProperty("native");
        Assert.True(native.GetProperty("node_truncated").GetBoolean());
        Assert.False(native.GetProperty("depth_truncated").GetBoolean());
        Assert.False(completeness.GetProperty("response").GetProperty("truncated").GetBoolean());

        snapshot.NativeCompleteness.DepthTruncated = true;
        var both = Json(snapshot, snapshot.Elements, responseTruncated: false)
            .RootElement.GetProperty("completeness").GetProperty("native");
        Assert.True(both.GetProperty("node_truncated").GetBoolean());
        Assert.True(both.GetProperty("depth_truncated").GetBoolean());
    }

    [Fact]
    public void TreeTextLimitIsReportedSeparately()
    {
        var snapshot = Snapshot("obs-text", RegisterTarget());
        snapshot.NativeCompleteness = new NativeSnapshotCompleteness
        {
            ProjectionComplete = true,
            TreeTextTruncated = true,
            MaxNodes = 100,
            MaxDepth = 48,
            TextLimit = 500,
        };
        snapshot.Elements.Add(Element("e1", [1], enabled: true));

        var completeness = Json(snapshot, snapshot.Elements, responseTruncated: false)
            .RootElement.GetProperty("completeness");
        Assert.True(completeness.GetProperty("native").GetProperty("tree_text_truncated").GetBoolean());
        Assert.False(completeness.GetProperty("response").GetProperty("truncated").GetBoolean());
        Assert.False(completeness.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void MaxElementsDoesNotMutateCachedSnapshot()
    {
        var snapshot = Snapshot("obs-pages", RegisterTarget());
        snapshot.NativeCompleteness = new NativeSnapshotCompleteness
        {
            ProjectionComplete = true,
            CollectedElements = 4,
            MaxNodes = 100,
            MaxDepth = 48,
            TextLimit = 500,
        };
        for (var i = 1; i <= 4; i++)
            snapshot.Elements.Add(Element($"e{i}", [i], enabled: true));

        var page = snapshot.Elements.Take(2).ToArray();
        var root = Json(snapshot, page, responseTruncated: true, nextIndex: 2, continuationToken: "cont-test").RootElement;

        Assert.Equal(4, snapshot.Elements.Count);
        Assert.Equal(2, root.GetProperty("elements").GetArrayLength());
        Assert.Equal(4, root.GetProperty("completeness").GetProperty("native").GetProperty("collected_elements").GetInt32());
        Assert.True(root.GetProperty("completeness").GetProperty("response").GetProperty("truncated").GetBoolean());
        Assert.Equal(2, root.GetProperty("completeness").GetProperty("response").GetProperty("omitted_collected_elements").GetInt32());
    }

    [Fact]
    public void ContinuationKeepsObservationAndExpiresAfterCacheReplacement()
    {
        var registry = new TargetRegistry("continuation-session");
        var cacheType = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache");
        Assert.NotNull(cacheType);
        var cache = Activator.CreateInstance(cacheType, registry, (Func<IntPtr, bool>)(_ => true));
        Assert.NotNull(cache);
        var store = cacheType.GetMethod("Store", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var create = cacheType.GetMethod("CreateContinuation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var read = cacheType.GetMethod("TryGetContinuation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(store);
        Assert.NotNull(create);
        Assert.NotNull(read);

        var reference = registry.Register(new NativeTargetIdentity(123, 1, (IntPtr)0xABC));
        var snapshot = Snapshot("obs-cache", reference);
        for (var i = 1; i <= 4; i++)
            snapshot.Elements.Add(Element($"e{i}", [i], enabled: true));
        store.Invoke(cache, [snapshot]);
        var token = Assert.IsType<string>(create.Invoke(cache, [snapshot, 2]));

        var readArgs = new object?[] { token, null, 0 };
        Assert.True(Assert.IsType<bool>(read.Invoke(cache, readArgs)));
        Assert.Same(snapshot, readArgs[1]);
        Assert.Equal(2, Assert.IsType<int>(readArgs[2]));
        var firstPage = Json(snapshot, snapshot.Elements.Take(2).ToArray(), true, 2, token).RootElement;
        Assert.Equal("obs-cache", firstPage.GetProperty("observation_id").GetString());
        Assert.Equal(snapshot.Revision, firstPage.GetProperty("revision").GetInt32());
        Assert.Equal(reference.SessionId, firstPage.GetProperty("target").GetProperty("session_id").GetString());
        Assert.Equal(reference.LifetimeEpoch, firstPage.GetProperty("target").GetProperty("lifetime_epoch").GetInt64());
        Assert.Equal(
            snapshot.NativeCompleteness.NodeTruncated,
            firstPage.GetProperty("completeness").GetProperty("native").GetProperty("node_truncated").GetBoolean());

        var replacementReference = registry.Register(new NativeTargetIdentity(123, 2, (IntPtr)0xABC));
        store.Invoke(cache, [Snapshot("obs-replacement", replacementReference)]);
        var expiredArgs = new object?[] { token, null, 0 };
        Assert.False(Assert.IsType<bool>(read.Invoke(cache, expiredArgs)));
    }

    [Fact]
    public void ElementMetadataIsNullableAndPreservesDisabledControls()
    {
        var snapshot = Snapshot("obs-metadata", RegisterTarget());
        snapshot.Elements.Add(new ElementInfo
        {
            Id = "e-disabled",
            RuntimeId = [7],
            ControlType = "Edit",
            Name = "Password",
            Actions = ["Value", "Toggle", "Scroll"],
            Enabled = false,
            ReadOnly = null,
            SupportedOperations = ["set_value", "scroll"],
            ActionableNow = false,
        });

        var element = Json(snapshot, snapshot.Elements, false).RootElement.GetProperty("elements")[0];
        Assert.False(element.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, element.GetProperty("read_only").ValueKind);
        Assert.Equal(new[] { "set_value", "scroll" }, element.GetProperty("supported_operations").EnumerateArray().Select(v => v.GetString()));
        Assert.False(element.GetProperty("action_metadata").GetProperty("actionable_now").GetBoolean());
        Assert.Equal(new[] { "Value", "Toggle", "Scroll" }, element.GetProperty("action_metadata").GetProperty("patterns").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public void ObservationEnvelope_PreservesNullableV1FieldsThroughMcpWireSerializer()
    {
        var snapshot = Snapshot("obs-wire", RegisterTarget());
        snapshot.Elements.Add(Element("e1", [1], enabled: true));

        // AIFunctionMcpServerTool uses this exact options/type-info path when it turns
        // an object result into the MCP text content sent over stdio.
        var wireJson = JsonSerializer.Serialize(
            ComputerUseTools.BuildObservationResponse(
                snapshot,
                snapshot.Elements,
                maxElements: snapshot.Elements.Count,
                responseTruncated: false,
                nextIndex: 0,
                continuationToken: null,
                capture: CaptureEnvelope.NotRequested(),
                visualGrounding: new { invoked = false, reason = "structured_evidence_present" },
                includeVisualSource: false),
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)));
        using var document = JsonDocument.Parse(wireJson);
        var root = document.RootElement;
        var element = root.GetProperty("elements")[0];

        Assert.Equal("dcu.observation.v1", root.GetProperty("schema").GetString());
        Assert.Equal("fixture", root.GetProperty("app").GetString());
        Assert.Equal(123, root.GetProperty("pid").GetInt32());
        Assert.True(root.GetProperty("bounds").TryGetProperty("left", out _));
        Assert.False(root.GetProperty("bounds").TryGetProperty("Left", out _));
        Assert.Equal(JsonValueKind.True, element.GetProperty("enabled").ValueKind);
        Assert.True(element.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, element.GetProperty("read_only").ValueKind);
        Assert.Equal(JsonValueKind.True, element.GetProperty("action_metadata").GetProperty("actionable_now").ValueKind);
        Assert.True(element.GetProperty("action_metadata").GetProperty("actionable_now").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("freshness").GetProperty("expires_at").ValueKind);
    }

    [Fact]
    public void FreshnessCarriesRevisionEpochBoundsAndFrames()
    {
        var reference = RegisterTarget("freshness-session");
        var snapshot = Snapshot("obs-freshness", reference);
        snapshot.Revision = 42;
        snapshot.CreatedAt = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        snapshot.Elements.Add(Element("e1", [1], enabled: true));

        var root = Json(snapshot, snapshot.Elements, false).RootElement;
        var freshness = root.GetProperty("freshness");
        Assert.Equal(42, freshness.GetProperty("revision").GetInt32());
        Assert.Equal(reference.LifetimeEpoch, freshness.GetProperty("target_lifetime_epoch").GetInt64());
        Assert.Equal("2026-09-21T00:00:00+00:00", freshness.GetProperty("observed_at").GetString());
        Assert.True(freshness.GetProperty("revalidate_target_before_action").GetBoolean());
        Assert.True(freshness.GetProperty("reresolve_element_before_action").GetBoolean());
        Assert.Equal(JsonValueKind.Null, freshness.GetProperty("expires_at").ValueKind);
        Assert.True(freshness.TryGetProperty("window_bounds", out _));
        Assert.False(freshness.TryGetProperty("document_epoch", out _));
        Assert.False(freshness.TryGetProperty("visual_capture_id", out _));
    }

    private static JsonDocument Json(
        Snapshot snapshot,
        IReadOnlyList<ElementInfo> elements,
        bool responseTruncated,
        int nextIndex = 0,
        string? continuationToken = null,
        CaptureEnvelope? capture = null)
        => JsonDocument.Parse(JsonSerializer.Serialize(
            ComputerUseTools.BuildObservationResponse(
                snapshot,
                elements,
                maxElements: Math.Max(1, elements.Count),
                responseTruncated: responseTruncated,
                nextIndex: nextIndex,
                continuationToken: continuationToken,
                capture: capture ?? CaptureEnvelope.NotRequested(),
                visualGrounding: new { invoked = false, reason = "structured_evidence_present" },
                includeVisualSource: false)));

    private static Snapshot Snapshot(string observationId, NativeTargetReference reference)
        => new()
        {
            ObservationId = observationId,
            Revision = 7,
            App = "fixture",
            Pid = reference.Pid,
            Hwnd = reference.Hwnd,
            Title = "Fixture",
            Bounds = new NativeMethods.RECT { Left = 10, Top = 20, Right = 410, Bottom = 320 },
            TargetReference = reference,
            NativeCompleteness = new NativeSnapshotCompleteness
            {
                ProjectionComplete = true,
                MaxNodes = 100,
                MaxDepth = 48,
                TextLimit = 500,
            },
        };

    private static ElementInfo Element(string id, int[] runtimeId, bool? enabled)
        => new()
        {
            Id = id,
            RuntimeId = runtimeId,
            ControlType = "Button",
            Name = "Save",
            AutomationId = "save",
            X = 1,
            Y = 2,
            Width = 80,
            Height = 24,
            Actions = ["Invoke"],
            Value = "",
            Enabled = enabled,
            ReadOnly = null,
            SupportedOperations = ["click"],
            ActionableNow = enabled,
        };

    private static NativeTargetReference RegisterTarget(string? session = null)
    {
        var registry = new TargetRegistry(session ?? "observation-session");
        return registry.Register(new NativeTargetIdentity(123, 456, (IntPtr)0xABC));
    }
}
