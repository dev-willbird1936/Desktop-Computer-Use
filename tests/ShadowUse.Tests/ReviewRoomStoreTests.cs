// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F60-R01/R02 coverage: the room/annotation/lease state machine itself, independent of
/// any live execution dispatch (that half is exercised live in native_evolution_regression.py,
/// reusing DispatchStep's own routing table).</summary>
public sealed class ReviewRoomStoreTests
{
    [Fact]
    public void Join_ThenIsAuthenticated_Succeeds()
    {
        var room = new ReviewRoomStore().GetOrCreate("r1");
        Assert.True(room.TryJoin("alice", "token-a", out _));
        Assert.True(room.IsAuthenticated("alice", "token-a"));
    }

    [Fact]
    public void IsAuthenticated_Refuses_WrongToken()
    {
        var room = new ReviewRoomStore().GetOrCreate("r1");
        room.TryJoin("alice", "token-a", out _);
        Assert.False(room.IsAuthenticated("alice", "wrong-token"));
    }

    [Fact]
    public void IsAuthenticated_Refuses_UnknownActor()
    {
        var room = new ReviewRoomStore().GetOrCreate("r1");
        Assert.False(room.IsAuthenticated("nobody", "token"));
    }

    [Fact]
    public void Join_Refuses_DifferentTokenForSameActor()
    {
        // F60-R01: impersonation protection -- an actor's token is fixed at first join.
        var room = new ReviewRoomStore().GetOrCreate("r1");
        room.TryJoin("alice", "token-a", out _);
        Assert.False(room.TryJoin("alice", "different-token", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Join_SameActorSameToken_IsIdempotent()
    {
        var room = new ReviewRoomStore().GetOrCreate("r1");
        room.TryJoin("alice", "token-a", out _);
        Assert.True(room.TryJoin("alice", "token-a", out _));
    }

    [Fact]
    public void PostAnnotation_ThenList_BothReviewersSeeTheSameAnnotations()
    {
        // F60-T01: two reviewers reading the same room see identical results.
        var room = new ReviewRoomStore().GetOrCreate("r1");
        room.TryJoin("alice", "a", out _);
        room.PostAnnotation("alice", "element-1", "looks wrong");

        var seenByReviewerOne = room.ListAnnotations();
        var seenByReviewerTwo = room.ListAnnotations();

        Assert.Single(seenByReviewerOne);
        Assert.Equal(seenByReviewerOne[0].AnnotationId, seenByReviewerTwo[0].AnnotationId);
        Assert.Equal("looks wrong", seenByReviewerTwo[0].Text);
    }

    [Fact]
    public void PostAnnotation_Rejects_EmptyText()
    {
        var room = new ReviewRoomStore().GetOrCreate("r1");
        Assert.Throws<ArgumentException>(() => room.PostAnnotation("alice", "target", ""));
    }

    [Fact]
    public void UndoAnnotation_RemovesItFromListAnnotations()
    {
        var room = new ReviewRoomStore().GetOrCreate("r1");
        var annotation = room.PostAnnotation("alice", "target", "text");

        Assert.True(room.UndoAnnotation(annotation.AnnotationId));
        Assert.Empty(room.ListAnnotations());
    }

    [Fact]
    public void UndoAnnotation_Twice_SecondCallReturnsFalse()
    {
        var room = new ReviewRoomStore().GetOrCreate("r1");
        var annotation = room.PostAnnotation("alice", "target", "text");
        room.UndoAnnotation(annotation.AnnotationId);
        Assert.False(room.UndoAnnotation(annotation.AnnotationId));
    }

    [Fact]
    public void TryAcquireLease_SucceedsWhenUnheld_ThenRefusesADifferentActor()
    {
        // F60-T01: only the current holder may submit; never stolen out from under them.
        var room = new ReviewRoomStore().GetOrCreate("r1");
        Assert.True(room.TryAcquireLease("alice"));
        Assert.Equal("alice", room.LeaseHolderActorId);
        Assert.False(room.TryAcquireLease("bob"));
        Assert.Equal("alice", room.LeaseHolderActorId);
    }

    [Fact]
    public void TryAcquireLease_IsReentrant_ForTheSameActor()
    {
        var room = new ReviewRoomStore().GetOrCreate("r1");
        room.TryAcquireLease("alice");
        Assert.True(room.TryAcquireLease("alice"));
    }

    [Fact]
    public void ReleaseLease_ThenAnotherActorCanAcquire()
    {
        var room = new ReviewRoomStore().GetOrCreate("r1");
        room.TryAcquireLease("alice");
        Assert.True(room.ReleaseLease("alice"));
        Assert.True(room.TryAcquireLease("bob"));
    }

    [Fact]
    public void ReleaseLease_Refuses_WhenCallerDoesNotHoldIt()
    {
        var room = new ReviewRoomStore().GetOrCreate("r1");
        room.TryAcquireLease("alice");
        Assert.False(room.ReleaseLease("bob"));
        Assert.Equal("alice", room.LeaseHolderActorId);
    }

    [Fact]
    public void HoldsLease_ReflectsCurrentHolderOnly()
    {
        var room = new ReviewRoomStore().GetOrCreate("r1");
        room.TryAcquireLease("alice");
        Assert.True(room.HoldsLease("alice"));
        Assert.False(room.HoldsLease("bob"));
    }

    [Fact]
    public void GetOrCreate_ReturnsTheSameRoomInstance_ForTheSameId()
    {
        var store = new ReviewRoomStore();
        Assert.Same(store.GetOrCreate("r1"), store.GetOrCreate("r1"));
    }

    [Fact]
    public void Find_ReturnsNull_ForAnUncreatedRoom()
    {
        Assert.Null(new ReviewRoomStore().Find("missing"));
    }
}
