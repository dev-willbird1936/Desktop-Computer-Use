// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F56-R01/R02 coverage: the workspace abstraction itself, independent of any live UIA
/// target. F56-T02's core safety claim (a member can never encode an action) is structural --
/// WorkspaceMember has no tool/args field at all -- and is exercised here via ParseKind rejecting
/// anything outside the two allowed reference kinds.</summary>
public sealed class WorkspaceStoreTests
{
    private static WorkspaceMember[] TwoMembers() =>
        [new(WorkspaceMemberKind.NativeApp, "Notepad"), new(WorkspaceMemberKind.Url, "https://example.com")];

    [Fact]
    public void Save_ThenFind_RoundTripsMembersInOrder()
    {
        var store = new WorkspaceStore();
        store.Save("w1", TwoMembers());

        var record = store.Find("w1");
        Assert.NotNull(record);
        Assert.Equal(2, record!.Members.Count);
        Assert.Equal(WorkspaceMemberKind.NativeApp, record.Members[0].Kind);
        Assert.Equal("Notepad", record.Members[0].Reference);
        Assert.Equal(WorkspaceMemberKind.Url, record.Members[1].Kind);
    }

    [Fact]
    public void Find_ReturnsNull_WhenNeverSaved()
    {
        var store = new WorkspaceStore();
        Assert.Null(store.Find("missing"));
    }

    [Fact]
    public void Save_Rejects_EmptyWorkspaceId()
    {
        var store = new WorkspaceStore();
        Assert.Throws<ArgumentException>(() => store.Save("", TwoMembers()));
    }

    [Fact]
    public void Save_Rejects_NoMembers()
    {
        var store = new WorkspaceStore();
        Assert.Throws<ArgumentException>(() => store.Save("w1", []));
    }

    [Fact]
    public void Save_Rejects_EmptyReference()
    {
        var store = new WorkspaceStore();
        Assert.Throws<ArgumentException>(() => store.Save("w1", [new(WorkspaceMemberKind.NativeApp, "")]));
    }

    [Fact]
    public void Save_Again_ReplacesMembership_WithExactlyWhatWasSupplied()
    {
        // F56-R02: manual edits are the source of truth -- a second save is a full replace,
        // in the newly supplied order, not a merge with the previous membership.
        var store = new WorkspaceStore();
        store.Save("w1", TwoMembers());

        store.Save("w1", [new(WorkspaceMemberKind.Url, "https://only-this-remains.example")]);

        var record = store.Find("w1")!;
        Assert.Single(record.Members);
        Assert.Equal("https://only-this-remains.example", record.Members[0].Reference);
    }

    [Theory]
    [InlineData("native_app", WorkspaceMemberKind.NativeApp)]
    [InlineData("url", WorkspaceMemberKind.Url)]
    public void ParseKind_AcceptsTheTwoSupportedKinds(string input, WorkspaceMemberKind expected)
    {
        Assert.Equal(expected, WorkspaceStore.ParseKind(input));
    }

    [Theory]
    [InlineData("shell_command")]
    [InlineData("delete_file")]
    [InlineData("purchase")]
    [InlineData("")]
    [InlineData("NATIVE_APP")] // case-sensitive on purpose -- no silent normalization of an unexpected kind
    public void ParseKind_Rejects_AnythingOutsideTheTwoSupportedKinds(string input)
    {
        // F56-T02: there is no way for any kind string to smuggle an action into a workspace
        // member -- the type itself has no tool/args slot, and only these two literal kinds parse.
        Assert.Throws<ArgumentException>(() => WorkspaceStore.ParseKind(input));
    }
}
