// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;
using Xunit;

namespace ShadowUse.Tests;

/// <summary>F57-T01/T02 pure store coverage: a prediction captured against a stale edit_version
/// is rejected rather than overwriting a newer user edit; undo restores the exact pre-run cell
/// state; and cache hits never re-remember a value, matching "cache hits do not trigger
/// unnecessary API requests". The live-generation half is out of scope, same as TextGenerationCache.</summary>
public sealed class GridPredictionStoreTests
{
    [Fact]
    public void EditingARowWhileInferenceInFlightRejectsTheStalePrediction()
    {
        var store = new GridPredictionStore();
        var key = new GridCellKey("sheet1", "row1", "status");
        var capturedVersion = store.RecordEdit(key, "pending"); // edit_version = 1, captured for in-flight inference

        store.RecordEdit(key, "user changed it"); // edit_version = 2, a newer edit lands first

        var applied = store.TryApplyPrediction(key, capturedVersion, "stale prediction", "batch-1");

        Assert.False(applied);
        Assert.Equal("user changed it", store.GetCell(key).Value);
    }

    [Fact]
    public void MatchingEditVersionAppliesThePrediction()
    {
        var store = new GridPredictionStore();
        var key = new GridCellKey("sheet1", "row1", "status");
        var version = store.RecordEdit(key, "pending");

        var applied = store.TryApplyPrediction(key, version, "predicted value", "batch-1");

        Assert.True(applied);
        Assert.Equal("predicted value", store.GetCell(key).Value);
    }

    [Fact]
    public void UndoRestoresExactPreRunStateIncludingEditVersion()
    {
        var store = new GridPredictionStore();
        var key = new GridCellKey("sheet1", "row1", "status");
        var version = store.RecordEdit(key, "original");

        store.TryApplyPrediction(key, version, "predicted", "batch-1");
        Assert.Equal("predicted", store.GetCell(key).Value);

        var undone = store.TryUndo("batch-1", out var restored);

        Assert.True(undone);
        Assert.Equal("original", store.GetCell(key).Value);
        Assert.Equal(version, store.GetCell(key).EditVersion);
        Assert.Single(restored);
    }

    [Fact]
    public void UndoAfterKeepFindsNoRecord()
    {
        var store = new GridPredictionStore();
        var key = new GridCellKey("sheet1", "row1", "status");
        var version = store.RecordEdit(key, "original");
        store.TryApplyPrediction(key, version, "predicted", "batch-1");

        Assert.True(store.Keep("batch-1"));
        Assert.False(store.TryUndo("batch-1", out _));
        Assert.Equal("predicted", store.GetCell(key).Value); // kept, not reverted
    }

    [Fact]
    public void CacheHitReturnsRememberedValueWithoutOverwritingIt()
    {
        var store = new GridPredictionStore();
        var predictionKey = new GridPredictionKey(GridPredictionStore.HashContent("row content"), "rubric-a", "model-x");
        store.RememberPrediction(predictionKey, "first value");

        Assert.True(store.TryGetCachedPrediction(predictionKey, out var cached));
        Assert.Equal("first value", cached.Value);
    }

    [Theory]
    [InlineData("Due Date", null, "date")]
    [InlineData("Email Address", null, "email")]
    [InlineData("Total Amount", null, "number")]
    [InlineData("is_active", null, "boolean")]
    [InlineData("Notes", null, "text")]
    [InlineData("Anything", "2026-09-24", "date")]
    [InlineData("Anything", "42", "number")]
    [InlineData("Anything", "user@example.com", "email")]
    public void SchemaPreviewInfersTypeFromHeadingOrSample(string heading, string? sample, string expected) =>
        Assert.Equal(expected, GridSchemaPreview.InferType(heading, sample));
}
