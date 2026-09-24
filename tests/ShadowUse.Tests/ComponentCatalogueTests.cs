// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F59-R01/T01 coverage: catalogue validation and composition storage, independent of
/// any live UIA/browser dispatch (that half is exercised live via native_evolution_regression.py,
/// reusing DispatchStep's own routing table).</summary>
public sealed class ComponentCatalogueTests
{
    private static readonly HashSet<string> KnownTools = new(StringComparer.Ordinal) { "click", "browser_click", "set_value", "browser_fill" };

    private static CompositionNodeSpec Node(string id, string component, Dictionary<string, string>? props = null, CompositionActionBinding? binding = null)
        => new(id, component, props ?? [], binding);

    [Fact]
    public void ValidateNode_Accepts_ButtonWithDeclaredPropAndAllowedBinding()
    {
        var node = Node("n1", "button", new() { ["label"] = "Save" }, new CompositionActionBinding("App", "click", "{}"));
        Assert.Null(CompositionValidator.ValidateNode(node, KnownTools));
    }

    [Fact]
    public void ValidateNode_Rejects_UnknownComponent()
    {
        var node = Node("n1", "not_a_real_component");
        var error = CompositionValidator.ValidateNode(node, KnownTools);
        Assert.NotNull(error);
        Assert.Contains("unknown component", error);
    }

    [Fact]
    public void ValidateNode_Rejects_UndeclaredProp()
    {
        var node = Node("n1", "button", new() { ["onclick_handler"] = "whatever" });
        var error = CompositionValidator.ValidateNode(node, KnownTools);
        Assert.NotNull(error);
        Assert.Contains("undeclared prop", error);
    }

    [Fact]
    public void ValidateNode_StringProps_NeverSpuriouslyRejectValidInput()
    {
        // The real v1 catalogue only declares string-typed props (no number/bool prop exists to
        // exercise a genuine type-mismatch rejection against) -- documented honestly rather than
        // adding a synthetic catalogue entry just to test MatchesType's number/bool branches.
        var node = Node("n1", "button", new() { ["label"] = "anything at all is a valid string" });
        Assert.Null(CompositionValidator.ValidateNode(node, KnownTools));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("eval(x)")]
    [InlineData("${malicious}")]
    public void ValidateNode_Rejects_ExecutableLookingPropValues(string maliciousValue)
    {
        // F59-T01: an "executable string" fails validation before rendering.
        var node = Node("n1", "text", new() { ["content"] = maliciousValue });
        var error = CompositionValidator.ValidateNode(node, KnownTools);
        Assert.NotNull(error);
        Assert.Contains("executable-looking", error);
    }

    [Fact]
    public void ValidateNode_Rejects_ActionBindingToolOutsideDcuAllowlist()
    {
        var node = Node("n1", "button", binding: new CompositionActionBinding("App", "launch_process", "{}"));
        var error = CompositionValidator.ValidateNode(node, KnownTools);
        Assert.NotNull(error);
        Assert.Contains("not one of DCU's own dispatchable workflow tools", error);
    }

    [Fact]
    public void ValidateNode_Rejects_ActionBindingToolNotAllowedForThisComponent()
    {
        // "set_value" is a real DCU tool (in KnownTools) but "button" does not allow it --
        // forbidden action binding, distinct from an unrecognised tool entirely.
        var node = Node("n1", "button", binding: new CompositionActionBinding("App", "set_value", "{}"));
        var error = CompositionValidator.ValidateNode(node, KnownTools);
        Assert.NotNull(error);
        Assert.Contains("does not allow", error);
    }

    [Fact]
    public void ValidateNode_Rejects_EmptyNodeId()
    {
        var node = Node("", "button");
        Assert.NotNull(CompositionValidator.ValidateNode(node, KnownTools));
    }

    [Fact]
    public void CompositionStore_SaveThenFind_RoundTrips()
    {
        var store = new CompositionStore();
        var nodes = new[] { Node("n1", "text", new() { ["content"] = "hello" }) };
        store.Save("c1", nodes);

        var record = store.Find("c1");
        Assert.NotNull(record);
        Assert.Single(record!.Nodes);
        Assert.Equal(ComponentCatalogue.Version, record.CatalogueVersion);
    }

    [Fact]
    public void CompositionStore_Rejects_DuplicateNodeId()
    {
        var store = new CompositionStore();
        var nodes = new[] { Node("n1", "text"), Node("n1", "button") };
        Assert.Throws<ArgumentException>(() => store.Save("c1", nodes));
    }

    [Fact]
    public void CompositionStore_Rejects_EmptyComposition()
    {
        var store = new CompositionStore();
        Assert.Throws<ArgumentException>(() => store.Save("c1", []));
    }

    [Fact]
    public void CompositionStore_Save_IsDeterministic_SameInputSameShape()
    {
        // F59-T02: re-saving the identical composition produces node data identical in every
        // field except the timestamp -- a pure function of its input, not randomized.
        var store = new CompositionStore();
        var nodes = new[] { Node("n1", "button", new() { ["label"] = "Save" }) };
        var first = store.Save("c1", nodes);
        var second = store.Save("c1", nodes);

        Assert.Equal(first.Nodes[0].NodeId, second.Nodes[0].NodeId);
        Assert.Equal(first.Nodes[0].ComponentId, second.Nodes[0].ComponentId);
        Assert.Equal(first.Nodes[0].Props["label"], second.Nodes[0].Props["label"]);
    }
}
