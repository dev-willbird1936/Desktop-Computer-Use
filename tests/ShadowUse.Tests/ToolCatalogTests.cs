using System.Reflection;
using ModelContextProtocol.Server;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

/// <summary>The capability registry must cover every MCP tool exactly, so no tool becomes
/// undiscoverable or silently lands in an "other" namespace.</summary>
public sealed class ToolCatalogTests
{
    private static readonly string[] ToolNames = typeof(ComputerUseTools).Assembly.GetTypes()
        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
        .SelectMany(t => t.GetMethods())
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .OfType<string>()
        .ToArray();

    [Fact]
    public void EveryToolHasACatalogEntry()
    {
        Assert.DoesNotContain(ToolNames, n => ToolCatalog.Get(n) == null);
        Assert.Empty(ToolCatalog.Entries.Keys.Except(ToolNames));
    }

    [Fact]
    public void CoreSetIsTheDesktopLoopPlusDiscovery()
    {
        var core = ToolCatalog.Entries.Where(e => e.Value.Core).Select(e => e.Key).ToHashSet();
        Assert.Contains("get_app_state", core);
        Assert.Contains("search_tools", core);
        Assert.Contains("call_tool", core);
        Assert.DoesNotContain("browser_open", core);
        Assert.True(core.Count < ToolNames.Length / 4);
    }

    [Fact]
    public void InputOwnershipToolsAreRegisteredGameTools()
    {
        Assert.Contains("game_input_ownership", ToolNames);
        Assert.Contains("game_release_input_ownership", ToolNames);
        var inspect = ToolCatalog.Get("game_input_ownership")!;
        var release = ToolCatalog.Get("game_release_input_ownership")!;
        Assert.Equal("game.observe", inspect.Namespace);
        Assert.True(inspect.ReadOnly);
        Assert.True(inspect.Game);
        Assert.Equal("game.input", release.Namespace);
        Assert.False(release.ReadOnly);
        Assert.True(release.Game);
    }

    [Fact]
    public void ReadOnlyToolsAreNeverDestructive()
    {
        Assert.DoesNotContain(ToolCatalog.Entries.Values, e => e.ReadOnly && e.Destructive);
    }
}
