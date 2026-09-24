using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F30: pure unit coverage of DemonstrationStore -- lifecycle/validation only, no live
/// resolution (that lives in ComputerUseTools and is covered by the live regression instead).</summary>
public sealed class DemonstrationStoreTests
{
    private static DemonstrationStepSpec Step(string tool = "click", string taskIntent = "submit")
        => new("app", tool, taskIntent, "{}");

    [Fact]
    public void Create_RejectsEmptySteps()
    {
        var store = new DemonstrationStore();
        Assert.Throws<ArgumentException>(() => store.Create("d1", [], null, null, null));
    }

    [Fact]
    public void Create_RejectsUnsupportedTool()
    {
        var store = new DemonstrationStore();
        Assert.Throws<ArgumentException>(() => store.Create("d1", [Step(tool: "wait_for")], null, null, null));
    }

    [Fact]
    public void Create_RejectsEmptyTaskIntent()
    {
        var store = new DemonstrationStore();
        Assert.Throws<ArgumentException>(() => store.Create("d1", [Step(taskIntent: "")], null, null, null));
    }

    [Fact]
    public void Create_RejectsDuplicateId()
    {
        var store = new DemonstrationStore();
        store.Create("d1", [Step()], null, null, null);
        Assert.Throws<ArgumentException>(() => store.Create("d1", [Step()], null, null, null));
    }

    [Fact]
    public void Create_RejectsPartialCompletionSpec()
    {
        var store = new DemonstrationStore();
        Assert.Throws<ArgumentException>(() => store.Create("d1", [Step()], "app", null, "text"));
    }

    [Fact]
    public void Create_StartsInDraft()
    {
        var store = new DemonstrationStore();
        var demo = store.Create("d1", [Step()], null, null, null);
        Assert.Equal(DemonstrationStatus.Draft, demo.Status);
        Assert.Null(demo.AdmittedAt);
    }

    [Fact]
    public void Admit_TransitionsDraftToAdmitted()
    {
        var store = new DemonstrationStore();
        store.Create("d1", [Step()], null, null, null);
        store.Admit("d1");
        var demo = store.Find("d1")!;
        Assert.Equal(DemonstrationStatus.Admitted, demo.Status);
        Assert.NotNull(demo.AdmittedAt);
    }

    [Fact]
    public void Admit_RefusesToReAdmit()
    {
        // F30-T02: admission is one-way -- an already-admitted demonstration cannot be re-stamped.
        var store = new DemonstrationStore();
        store.Create("d1", [Step()], null, null, null);
        store.Admit("d1");
        Assert.Throws<ArgumentException>(() => store.Admit("d1"));
    }

    [Fact]
    public void Admit_UnknownId_Throws()
    {
        var store = new DemonstrationStore();
        Assert.Throws<ArgumentException>(() => store.Admit("missing"));
    }

    [Fact]
    public void Find_UnknownId_ReturnsNull()
    {
        Assert.Null(new DemonstrationStore().Find("missing"));
    }
}
