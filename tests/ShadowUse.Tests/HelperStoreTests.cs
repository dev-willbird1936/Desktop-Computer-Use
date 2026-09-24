// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Reflection;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F18-R01/R02 coverage: the helper workspace abstraction itself (register/admit/
/// hash-invalidation/param substitution), independent of any live UIA or browser target.
/// F18-T02's "undeclared destination / unrecognised tool blocked even though admitted" check
/// lives as a private static ValidateHelperNodes on ComputerUseTools -- reached here via
/// reflection, matching this repo's existing convention (VisualGroundingServiceTests) for
/// testing pure logic embedded in an otherwise live-dependent tools class.</summary>
public sealed class HelperStoreTests
{
    private const string OneNodeTemplate = "[{\"id\":\"n1\",\"app\":\"Notepad\",\"tool\":\"click\",\"args\":{\"element_id\":\"{{target}}\"},\"prerequisites\":[]}]";

    [Fact]
    public void Register_LandsUnadmitted()
    {
        var store = new HelperStore();
        var record = store.Register("h1", OneNodeTemplate, "{}", ["Notepad"], "");

        Assert.False(record.Admitted);
        Assert.Null(record.AdmittedHash);
        Assert.NotEmpty(record.SourceHash);
    }

    [Fact]
    public void RequireAdmitted_Throws_WhenNeverAdmitted()
    {
        var store = new HelperStore();
        store.Register("h1", OneNodeTemplate, "{}", ["Notepad"], "");

        Assert.Throws<ArgumentException>(() => store.RequireAdmitted("h1"));
    }

    [Fact]
    public void RequireAdmitted_Throws_WhenNeverRegistered()
    {
        var store = new HelperStore();
        Assert.Throws<ArgumentException>(() => store.RequireAdmitted("missing"));
    }

    [Fact]
    public void Admit_ThenRequireAdmitted_Succeeds()
    {
        var store = new HelperStore();
        store.Register("h1", OneNodeTemplate, "{}", ["Notepad"], "");
        store.Admit("h1", "reviewed nodes_json by hand, ran it once against a test fixture");

        var record = store.RequireAdmitted("h1");
        Assert.True(record.Admitted);
        Assert.Equal(record.SourceHash, record.AdmittedHash);
    }

    [Fact]
    public void Admit_Rejects_EmptyTestEvidence()
    {
        var store = new HelperStore();
        store.Register("h1", OneNodeTemplate, "{}", ["Notepad"], "");

        Assert.Throws<ArgumentException>(() => store.Admit("h1", ""));
    }

    [Fact]
    public void Register_AfterAdmit_WithEditedNodesJson_InvalidatesAdmission()
    {
        // F18-T01: an edited helper hash invalidates previous admission.
        var store = new HelperStore();
        store.Register("h1", OneNodeTemplate, "{}", ["Notepad"], "");
        store.Admit("h1", "reviewed");
        Assert.True(store.RequireAdmitted("h1").Admitted);

        var edited = OneNodeTemplate.Replace("Notepad", "Calculator");
        store.Register("h1", edited, "{}", ["Notepad"], "");

        var ex = Assert.Throws<ArgumentException>(() => store.RequireAdmitted("h1"));
        Assert.Contains("not admitted", ex.Message);
    }

    [Fact]
    public void Register_AfterAdmit_WithSameNodesJson_ButDifferentDeclaredApps_InvalidatesAdmission()
    {
        var store = new HelperStore();
        store.Register("h1", OneNodeTemplate, "{}", ["Notepad"], "");
        store.Admit("h1", "reviewed");

        store.Register("h1", OneNodeTemplate, "{}", ["Notepad", "Calculator"], "");

        Assert.Throws<ArgumentException>(() => store.RequireAdmitted("h1"));
    }

    [Fact]
    public void ComputeHash_IsDeterministic_AndOrderIndependentForDeclaredApps()
    {
        var a = HelperStore.ComputeHash(OneNodeTemplate, "{}", ["Notepad", "Calculator"], "v1");
        var b = HelperStore.ComputeHash(OneNodeTemplate, "{}", ["Calculator", "Notepad"], "v1");
        var c = HelperStore.ComputeHash(OneNodeTemplate, "{}", ["Notepad"], "v1");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void ComputeHash_ChangesWhenDependencyLockChanges()
    {
        var a = HelperStore.ComputeHash(OneNodeTemplate, "{}", ["Notepad"], "v1");
        var b = HelperStore.ComputeHash(OneNodeTemplate, "{}", ["Notepad"], "v2");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Register_Rejects_InvalidNodesJson()
    {
        var store = new HelperStore();
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => store.Register("h1", "not json", "{}", ["Notepad"], ""));
    }

    [Fact]
    public void SubstituteParams_ReplacesTokenInsideArgsObject()
    {
        var result = HelperStore.SubstituteParams(OneNodeTemplate, new Dictionary<string, string> { ["target"] = "e42" });

        Assert.Contains("\"element_id\":\"e42\"", result);
    }

    [Fact]
    public void SubstituteParams_ReplacesRecursivelyInsideNestedArraysAndObjects()
    {
        const string nested = "[{\"id\":\"n1\",\"app\":\"Notepad\",\"tool\":\"click\",\"args\":{\"nested\":{\"list\":[\"{{p}}\",\"literal\"]}},\"prerequisites\":[]}]";
        var result = HelperStore.SubstituteParams(nested, new Dictionary<string, string> { ["p"] = "resolved" });

        Assert.Contains("\"resolved\"", result);
        Assert.Contains("\"literal\"", result);
    }

    [Fact]
    public void SubstituteParams_LeavesUnknownTokensUnchanged()
    {
        var result = HelperStore.SubstituteParams(OneNodeTemplate, new Dictionary<string, string>());
        Assert.Contains("{{target}}", result);
    }

    // ---- F18-T02: ComputerUseTools.ValidateHelperNodes via reflection ----

    private static string? ValidateHelperNodes(string helperId, IReadOnlyList<WorkflowNodeSpec> specs, IReadOnlyList<string> declaredApps)
    {
        var method = typeof(UiaService).Assembly
            .GetType("ShadowUse.Tools.ComputerUseTools")!
            .GetMethod("ValidateHelperNodes", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, [helperId, specs, declaredApps]);
    }

    [Fact]
    public void ValidateHelperNodes_Allows_NodeWithDeclaredNativeDestination()
    {
        WorkflowNodeSpec[] specs = [new("n1", "Notepad", "click", "{}", [])];
        var error = ValidateHelperNodes("h1", specs, ["Notepad"]);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateHelperNodes_Blocks_UndeclaredNativeDestination()
    {
        WorkflowNodeSpec[] specs = [new("n1", "Calculator", "click", "{}", [])];
        var error = ValidateHelperNodes("h1", specs, ["Notepad"]);
        Assert.NotNull(error);
        Assert.Contains("undeclared destination", error);
    }

    [Fact]
    public void ValidateHelperNodes_Blocks_UnrecognisedTool_EvenWithDeclaredDestination()
    {
        // F18-T02: "arbitrary process launch" has no real tool name in DCU at all -- this proves
        // an invented/unknown tool name is refused regardless of declared_apps.
        WorkflowNodeSpec[] specs = [new("n1", "Notepad", "launch_process", "{}", [])];
        var error = ValidateHelperNodes("h1", specs, ["Notepad"]);
        Assert.NotNull(error);
        Assert.Contains("not one of DCU's own dispatchable workflow tools", error);
    }

    [Fact]
    public void ValidateHelperNodes_RequiresLiteralBrowserDestination_ForBrowserTools()
    {
        WorkflowNodeSpec[] specs = [new("n1", "", "browser_click", "{}", [])];
        Assert.NotNull(ValidateHelperNodes("h1", specs, ["Notepad"]));
        Assert.Null(ValidateHelperNodes("h1", specs, ["browser"]));
    }

    [Fact]
    public void ValidateHelperNodes_ChecksEveryNode_BeforeAnyWouldRun()
    {
        // The first node's destination is declared; the second node's is not. Both must be
        // reported as blocked collectively -- proven here by asserting the SECOND node's
        // violation is what's returned (validation walks the whole list, not just node 0).
        WorkflowNodeSpec[] specs =
        [
            new("n1", "Notepad", "click", "{}", []),
            new("n2", "Calculator", "click", "{}", []),
        ];
        var error = ValidateHelperNodes("h1", specs, ["Notepad"]);
        Assert.NotNull(error);
        Assert.Contains("n2", error);
    }
}
