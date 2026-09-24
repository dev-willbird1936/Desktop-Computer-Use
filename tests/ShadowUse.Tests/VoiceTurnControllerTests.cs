// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;
using Xunit;

namespace ShadowUse.Tests;

/// <summary>F58-R01/R02/T01/T02 pure turn-taking state-machine coverage. No speech-recognition
/// pipeline exists in this environment -- the caller supplies partial/final transcripts exactly
/// like a real adapter would, and these tests prove the deterministic scaffolding around them:
/// hesitation never prematurely finalizes, stop cancels late responses, and a finalized transcript
/// hands off through the same SubgoalStore.Define typed goals already use.</summary>
public sealed class VoiceTurnControllerTests
{
    private static VoiceTurnController NewController() => new(new SubgoalStore());

    [Fact]
    public void PartialTranscriptNeverFinalizesRegardlessOfContent()
    {
        var controller = NewController();
        controller.OpenSession("s1", "resume_listening");
        var (accepted, reason) = controller.SubmitPartial("s1", 1, "send email to");
        Assert.True(accepted);
        Assert.Null(reason);
        var session = controller.Find("s1");
        Assert.Equal(VoiceTurnStatus.Listening, session.Status);
        Assert.Null(session.SubgoalId);
    }

    [Fact]
    public void StaleGenerationIdIsRejected()
    {
        var controller = NewController();
        controller.OpenSession("s1", "resume_listening");
        controller.SubmitPartial("s1", 5, "later text");
        var (accepted, reason) = controller.SubmitPartial("s1", 3, "an out-of-order older response");
        Assert.False(accepted);
        Assert.Equal("stale_generation_id", reason);
    }

    [Fact]
    public void ExplicitFinalHandsOffToTheSameSubgoalStoreTypedGoalsUse()
    {
        var subgoals = new SubgoalStore();
        var controller = new VoiceTurnController(subgoals);
        controller.OpenSession("s1", "resume_listening");
        var (finalized, reason, subgoalId) = controller.SubmitFinal("s1", 1, "open the settings page");
        Assert.True(finalized);
        Assert.Null(reason);
        Assert.NotNull(subgoalId);
        var record = subgoals.Find(subgoalId!);
        Assert.NotNull(record);
        Assert.Equal("open the settings page", record!.Objective);
    }

    [Fact]
    public void SilenceBelowThresholdDoesNotFinalize_HesitationIsNotPrematureExecution()
    {
        var controller = NewController();
        controller.OpenSession("s1", "resume_listening");
        controller.SubmitPartial("s1", 1, "send the email to");
        var (finalized, reason, subgoalId) = controller.CheckSilenceTimeout("s1", silenceMsSinceLastPartial: 200, silenceThresholdMs: 800);
        Assert.False(finalized);
        Assert.Equal("below_silence_threshold", reason);
        Assert.Null(subgoalId);
        Assert.Equal(VoiceTurnStatus.Listening, controller.Find("s1").Status);
    }

    [Fact]
    public void SilenceCrossingThresholdFinalizesTheLastPartial()
    {
        var controller = NewController();
        controller.OpenSession("s1", "resume_listening");
        controller.SubmitPartial("s1", 1, "open the calculator");
        var (finalized, reason, subgoalId) = controller.CheckSilenceTimeout("s1", silenceMsSinceLastPartial: 900, silenceThresholdMs: 800);
        Assert.True(finalized);
        Assert.Null(reason);
        Assert.NotNull(subgoalId);
    }

    [Fact]
    public void StopCancelsAFinalArrivingLate()
    {
        var controller = NewController();
        controller.OpenSession("s1", "resume_listening");
        controller.SubmitPartial("s1", 1, "delete the");
        controller.Stop("s1");
        var (finalized, reason, subgoalId) = controller.SubmitFinal("s1", 2, "delete the file");
        Assert.False(finalized);
        Assert.Equal("session_is_stopped", reason);
        Assert.Null(subgoalId);
    }

    [Fact]
    public void StopDoesNotAffectABrandNewSession()
    {
        var controller = NewController();
        controller.OpenSession("s1", "resume_listening");
        controller.Stop("s1");
        controller.OpenSession("s2", "resume_listening");
        var (finalized, _, subgoalId) = controller.SubmitFinal("s2", 1, "a totally new request");
        Assert.True(finalized);
        Assert.NotNull(subgoalId);
    }

    [Fact]
    public void TranscriptionFailureReturnsTheDeclaredFallbackNeverAHardcodedDefault()
    {
        var controller = NewController();
        controller.OpenSession("s1", "cancel");
        var fallback = controller.ReportTranscriptionFailure("s1", "connection_reset");
        Assert.Equal("cancel", fallback);
        Assert.Equal(VoiceTurnStatus.Failed, controller.Find("s1").Status);
    }

    [Fact]
    public void AfterFailureALateFinalIsRefused()
    {
        var controller = NewController();
        controller.OpenSession("s1", "resume_listening");
        controller.SubmitPartial("s1", 1, "partial text");
        controller.ReportTranscriptionFailure("s1", "timeout");
        var (finalized, reason, _) = controller.SubmitFinal("s1", 2, "a late-arriving response");
        Assert.False(finalized);
        Assert.Equal("session_is_failed", reason);
    }

    [Fact]
    public void EmptyFinalTranscriptIsRejected()
    {
        var controller = NewController();
        controller.OpenSession("s1", "resume_listening");
        var (finalized, reason, _) = controller.SubmitFinal("s1", 1, "");
        Assert.False(finalized);
        Assert.Equal("empty_transcript", reason);
    }

    [Fact]
    public void OpeningTheSameSessionIdTwiceThrows()
    {
        var controller = NewController();
        controller.OpenSession("s1", "resume_listening");
        Assert.Throws<ArgumentException>(() => controller.OpenSession("s1", "resume_listening"));
    }
}
