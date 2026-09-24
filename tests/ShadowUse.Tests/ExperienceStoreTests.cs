// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F28-R01 coverage: the store abstraction itself, independent of any adapter.
/// F28-T02 ("a previously successful workflow for a different app version remains
/// advisory until revalidated") is exercised directly here since it requires an app
/// version change the live fixtures cannot produce without rebuilding the binary.</summary>
public sealed class ExperienceStoreTests
{
    private static ExperienceKey Key(string taskIntent = "login_submit") => new("native", "app.exe", taskIntent);

    private static ExperienceRecord Record(string appVersion = "1.0.0", string outcome = "success", ActionResolutionRecipe? recipe = null) =>
        new(appVersion, "ci", recipe, outcome, outcome == "success" ? null : "element_not_found", DateTimeOffset.UtcNow);

    [Fact]
    public void Lookup_ReturnsNotFound_WhenNothingRecorded()
    {
        var store = new ExperienceStore();
        var lookup = store.Lookup(Key(), "1.0.0");

        Assert.False(lookup.Found);
        Assert.Equal("not_found", lookup.Reason);
        Assert.False(lookup.Advisory);
    }

    [Fact]
    public void Lookup_ReturnsTheRecord_WhenAppVersionMatches()
    {
        var store = new ExperienceStore();
        var record = Record();
        store.Record(Key(), record);

        var lookup = store.Lookup(Key(), "1.0.0");

        Assert.True(lookup.Found);
        Assert.False(lookup.Advisory);
        Assert.Null(lookup.Reason);
        Assert.Equal(record, lookup.Record);
    }

    [Fact]
    public void Lookup_IsAdvisory_WhenLiveAppVersionDiffersFromRecorded()
    {
        var store = new ExperienceStore();
        store.Record(Key(), Record(appVersion: "1.0.0"));

        var lookup = store.Lookup(Key(), "2.0.0");

        Assert.True(lookup.Found);
        Assert.True(lookup.Advisory);
        Assert.Equal("app_version_changed", lookup.Reason);
        Assert.Equal("1.0.0", lookup.Record!.AppVersion);
    }

    [Fact]
    public void DifferentTaskIntent_IsADifferentKey_EvenWithinTheSameApp()
    {
        var store = new ExperienceStore();
        store.Record(Key(taskIntent: "login_submit"), Record());

        Assert.False(store.Lookup(Key(taskIntent: "logout"), "1.0.0").Found);
    }

    [Fact]
    public void DifferentTargetIdentity_IsADifferentKey()
    {
        var store = new ExperienceStore();
        store.Record(new ExperienceKey("native", "app-a.exe", "login_submit"), Record());

        Assert.False(store.Lookup(new ExperienceKey("native", "app-b.exe", "login_submit"), "1.0.0").Found);
    }

    [Fact]
    public void RecordOverwritesAPriorEntry_ForTheSameKey()
    {
        var store = new ExperienceStore();
        var key = Key();
        store.Record(key, Record(outcome: "failure"));
        store.Record(key, Record(outcome: "success"));

        var lookup = store.Lookup(key, "1.0.0");

        Assert.Equal("success", lookup.Record!.Outcome);
        Assert.Null(lookup.Record.FailureCategory);
    }

    [Fact]
    public void Count_ReflectsDistinctKeysRecorded()
    {
        var store = new ExperienceStore();
        store.Record(Key("a"), Record());
        store.Record(Key("b"), Record());
        store.Record(Key("a"), Record());

        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void RecordCanCarryARecipe_ForLaterLiveReResolution()
    {
        var store = new ExperienceStore();
        var recipe = new ActionResolutionRecipe("submit-btn", "Button", "Sign in", null, DateTimeOffset.UtcNow);
        store.Record(Key(), Record(recipe: recipe));

        var lookup = store.Lookup(Key(), "1.0.0");

        Assert.Equal(recipe, lookup.Record!.Recipe);
    }
}
