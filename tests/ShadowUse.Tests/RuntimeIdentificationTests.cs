using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class RuntimeIdentificationTests
{
    private static readonly Assembly Dcu = typeof(RuntimeIdentification).Assembly;

    [Fact]
    public void CurrentIdentityReportsVersionModuleAndProcess()
    {
        var identity = RuntimeIdentification.Current;

        Assert.Equal(Environment.ProcessId, identity.ProcessId);
        Assert.Equal(Dcu.ManifestModule.ModuleVersionId, identity.ModuleVersionId);
        Assert.Equal("dcu", identity.AssemblyName);
        Assert.Equal(Dcu.GetName().Version!.ToString(), identity.AssemblyVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+$", identity.Version);
    }

    [Fact]
    public void BuildTimestampComesFromAssemblyMetadataNotProcessStart()
    {
        var stamped = Dcu.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == RuntimeIdentification.BuildTimestampMetadataKey).Value;

        Assert.False(string.IsNullOrEmpty(stamped));
        Assert.Equal(stamped, RuntimeIdentification.Current.BuildTimestampUtc);
        Assert.True(DateTimeOffset.TryParse(stamped, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var built));
        Assert.True(built <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void MissingBuildMetadataIsReportedUnavailableNotInvented()
    {
        // The test assembly carries no BuildTimestampUtc metadata.
        var identity = RuntimeIdentification.Capture(typeof(RuntimeIdentificationTests).Assembly, null, 1234, DateTimeOffset.UtcNow);

        Assert.Null(identity.BuildTimestampUtc);
        var json = JsonSerializer.SerializeToElement(RuntimeIdentification.Describe(identity, DateTimeOffset.UtcNow));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("build_timestamp_utc").ValueKind);
        Assert.StartsWith("unavailable", json.GetProperty("build_timestamp_source").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("2026-13-45T99:00:00Z")]
    public void UnparseableBuildMetadataIsNull(string? value)
    {
        var assembly = DynamicAssemblyWithMetadata(value);
        Assert.Null(RuntimeIdentification.ReadBuildTimestamp(assembly));
    }

    [Fact]
    public void ValidBuildMetadataRoundTrips()
    {
        var assembly = DynamicAssemblyWithMetadata("2026-05-17T08:09:10Z");
        Assert.Equal("2026-05-17T08:09:10Z", RuntimeIdentification.ReadBuildTimestamp(assembly));
    }

    [Fact]
    public void ReplacedExecutableIsFlaggedStale()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dcu-runtime-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var identity = RuntimeIdentification.Capture(Dcu, path, 1, DateTimeOffset.UtcNow);
            Assert.False(RuntimeIdentification.BinariesChangedSinceStart(identity, out var unchanged));
            Assert.Empty(unchanged);

            File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6]);
            Assert.True(RuntimeIdentification.BinariesChangedSinceStart(identity, out var changed));
            Assert.Contains("executable", changed);

            var json = JsonSerializer.SerializeToElement(RuntimeIdentification.Describe(identity, DateTimeOffset.UtcNow));
            Assert.True(json.GetProperty("stale_executable").GetBoolean());

            File.Delete(path);
            Assert.True(RuntimeIdentification.BinariesChangedSinceStart(identity, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DescribeKeepsProcessStartSeparateFromBuildTime()
    {
        var started = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var identity = RuntimeIdentification.Capture(Dcu, null, 4321, started);
        var json = JsonSerializer.SerializeToElement(RuntimeIdentification.Describe(identity, started.AddSeconds(90)));

        Assert.Equal(4321, json.GetProperty("process_id").GetInt32());
        Assert.Equal(90, json.GetProperty("uptime_seconds").GetInt64());
        Assert.StartsWith("2030-01-02T03:04:05", json.GetProperty("process_started_utc").GetString());
        Assert.NotEqual(json.GetProperty("process_started_utc").GetString(), json.GetProperty("build_timestamp_utc").GetString());
        Assert.Equal(identity.ModuleVersionId.ToString(), json.GetProperty("module_version_id").GetString());
    }

    [Fact]
    public void InstructionsSuffixNamesBuildModuleAndProcess()
    {
        var identity = RuntimeIdentification.Current;
        var suffix = RuntimeIdentification.InstructionsSuffix(identity);

        Assert.Contains(identity.Version, suffix);
        Assert.Contains(identity.ModuleVersionId.ToString(), suffix);
        Assert.Contains($"pid {identity.ProcessId}", suffix);
        Assert.Contains(identity.BuildTimestampUtc!, suffix);
    }

    [Fact]
    public void SchemaFingerprintIgnoresCollectionOrderAndSchemaKeyOrder()
    {
        var a = Tool("alpha", "first", """{"type":"object","properties":{"x":{"type":"string"},"y":{"type":"integer"}},"required":["x"]}""");
        var b = Tool("beta", "second", """{"type":"object","properties":{"q":{"type":"boolean"}}}""");
        var bReordered = Tool("beta", "second", """{"properties":{"q":{"type":"boolean"}},"type":"object"}""");

        var forward = ToolCatalog.SchemaFingerprint(new[] { a, b });
        var backward = ToolCatalog.SchemaFingerprint(new[] { bReordered, a });

        Assert.Equal(forward, backward);
        Assert.Matches("^[0-9a-f]{64}$", forward);
    }

    [Fact]
    public void SchemaFingerprintChangesWithNameDescriptionSchemaAnnotationsOrMembership()
    {
        var baseline = Tool("alpha", "first", """{"type":"object"}""");
        var baselineHash = ToolCatalog.SchemaFingerprint(new[] { baseline });

        Assert.NotEqual(baselineHash, ToolCatalog.SchemaFingerprint(new[] { Tool("alpha2", "first", """{"type":"object"}""") }));
        Assert.NotEqual(baselineHash, ToolCatalog.SchemaFingerprint(new[] { Tool("alpha", "changed", """{"type":"object"}""") }));
        Assert.NotEqual(baselineHash, ToolCatalog.SchemaFingerprint(new[] { Tool("alpha", "first", """{"type":"object","properties":{"z":{}}}""") }));
        Assert.NotEqual(baselineHash, ToolCatalog.SchemaFingerprint(new[] { baseline, Tool("extra", "x", """{"type":"object"}""") }));

        var readOnly = Tool("alpha", "first", """{"type":"object"}""");
        readOnly.Annotations = new ToolAnnotations { ReadOnlyHint = true };
        Assert.NotEqual(baselineHash, ToolCatalog.SchemaFingerprint(new[] { readOnly }));
    }

    [Fact]
    public void ServerToolOverloadIsOrderIndependentAndAppliesCatalogAnnotations()
    {
        var list = McpServerTool.Create(() => "x", new McpServerToolCreateOptions { Name = "list_apps" });
        var click = McpServerTool.Create(() => "x", new McpServerToolCreateOptions { Name = "click" });

        var forward = ToolCatalog.SchemaFingerprint(new[] { list, click });
        var backward = ToolCatalog.SchemaFingerprint(new[] { click, list });

        Assert.Equal(forward, backward);
        Assert.True(list.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(click.ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public void FullRegisteredCatalogFingerprintIsOrderIndependentAndHealthCheckSchemaIsFactual()
    {
        var tools = typeof(ComputerUseTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods().Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                .Select(m => McpServerTool.Create(m, _ => throw new InvalidOperationException("schema only"))))
            .ToList();

        Assert.Contains(tools, t => t.ProtocolTool.Name == "health_check");
        var forward = ToolCatalog.SchemaFingerprint(tools);
        var backward = ToolCatalog.SchemaFingerprint(Enumerable.Reverse(tools));
        Assert.Equal(forward, backward);

        Assert.Contains(tools, t => t.ProtocolTool.Name == "game_input_ownership");
        Assert.Contains(tools, t => t.ProtocolTool.Name == "game_release_input_ownership");
        var withoutOwnership = tools.Where(t => !t.ProtocolTool.Name.Contains("input_ownership", StringComparison.Ordinal));
        Assert.NotEqual(forward, ToolCatalog.SchemaFingerprint(withoutOwnership));

        var health = tools.Single(t => t.ProtocolTool.Name == "health_check").ProtocolTool;
        Assert.Contains("schema fingerprint", health.Description);
        Assert.DoesNotContain("server", health.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    private static Tool Tool(string name, string description, string schemaJson) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonDocument.Parse(schemaJson).RootElement.Clone(),
    };

    private static Assembly DynamicAssemblyWithMetadata(string? value)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"dyn{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
        var ctor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(ctor, [RuntimeIdentification.BuildTimestampMetadataKey, value]));
        return assembly;
    }
}
