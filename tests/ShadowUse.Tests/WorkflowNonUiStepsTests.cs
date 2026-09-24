// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Net;
using ShadowUse.Automation;
using Xunit;

namespace ShadowUse.Tests;

public class WorkflowFilePathsTests
{
    [Fact]
    public void Resolve_AllowsAPathInsideTheApprovedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-workflow-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "note.txt"), "hi");
            var resolved = WorkflowFilePaths.Resolve(root, "note.txt", mustExist: true);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "note.txt"), resolved);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Resolve_RejectsPathTraversalOutsideTheApprovedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-workflow-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<ArgumentException>(() => WorkflowFilePaths.Resolve(root, "../../escape.txt", mustExist: false));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Resolve_RejectsAMissingFileWhenMustExist()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-workflow-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<ArgumentException>(() => WorkflowFilePaths.Resolve(root, "missing.txt", mustExist: true));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Resolve_AllowsAMissingFileWhenNotMustExist_ForWriteTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-workflow-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var resolved = WorkflowFilePaths.Resolve(root, "new-output.txt", mustExist: false);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "new-output.txt"), resolved);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Resolve_RejectsARelativeAllowedRoot()
    {
        Assert.Throws<ArgumentException>(() => WorkflowFilePaths.Resolve("relative/root", "x.txt", mustExist: false));
    }
}

public class WorkflowDataTransformTests
{
    [Theory]
    [InlineData("trim", "  hi  ", "hi")]
    [InlineData("uppercase", "hi", "HI")]
    [InlineData("lowercase", "HI", "hi")]
    public void Apply_HandlesSimpleStringOps(string op, string input, string expected)
        => Assert.Equal(expected, WorkflowDataTransform.Apply(op, input));

    [Fact]
    public void Apply_CsvCell_ExtractsTheRequestedCell()
    {
        var csv = "name,value\nalice,42\nbob,7";
        Assert.Equal("bob", WorkflowDataTransform.Apply("csv_cell:2,0", csv));
        Assert.Equal("7", WorkflowDataTransform.Apply("csv_cell:2,1", csv));
    }

    [Fact]
    public void Apply_CsvCell_RejectsAnOutOfRangeRow()
        => Assert.Throws<ArgumentException>(() => WorkflowDataTransform.Apply("csv_cell:99,0", "a,b\nc,d"));

    [Fact]
    public void Apply_CsvCell_RejectsAnOutOfRangeColumn()
        => Assert.Throws<ArgumentException>(() => WorkflowDataTransform.Apply("csv_cell:0,99", "a,b\nc,d"));

    [Fact]
    public void Apply_RejectsAnUnknownOp()
        => Assert.Throws<ArgumentException>(() => WorkflowDataTransform.Apply("eval:1+1", "irrelevant"));
}

public class WorkflowHttpStepsTests
{
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _loop;
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }

        public LoopbackServer(Func<HttpListenerRequest, (int Status, string? Location, string Body)> respond)
        {
            Port = GetFreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                    catch (HttpListenerException) { return; }
                    catch (ObjectDisposedException) { return; }
                    var (status, location, body) = respond(ctx.Request);
                    ctx.Response.StatusCode = status;
                    if (location != null) ctx.Response.Headers.Add("Location", location);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    ctx.Response.OutputStream.Close();
                }
            });
        }

        private static int GetFreePort()
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            try { await _loop.ConfigureAwait(false); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsTheBodyForAnAllowedHost()
    {
        await using var server = new LoopbackServer(_ => (200, null, "hello from loopback"));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var (status, bodyText, finalUrl) = await WorkflowHttpSteps.ExecuteAsync(
            client, $"http://127.0.0.1:{server.Port}/", ["127.0.0.1"], "GET", null, 5000, default);
        Assert.Equal(200, status);
        Assert.Equal("hello from loopback", bodyText);
        Assert.Contains("127.0.0.1", finalUrl);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsAHostNotInAllowedHosts_BeforeSendingAnyRequest()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        await Assert.ThrowsAsync<ArgumentException>(() =>
            WorkflowHttpSteps.ExecuteAsync(client, "http://not-allowed.example.invalid/", ["127.0.0.1"], "GET", null, 5000, default));
    }

    [Fact]
    public async Task ExecuteAsync_FollowsARedirectToAnAllowedHost()
    {
        await using var target = new LoopbackServer(_ => (200, null, "final body"));
        await using var redirector = new LoopbackServer(req =>
            (302, $"http://127.0.0.1:{target.Port}/", ""));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var (status, bodyText, finalUrl) = await WorkflowHttpSteps.ExecuteAsync(
            client, $"http://127.0.0.1:{redirector.Port}/", ["127.0.0.1"], "GET", null, 5000, default);
        Assert.Equal(200, status);
        Assert.Equal("final body", bodyText);
        Assert.Contains(target.Port.ToString(), finalUrl);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsARedirectToAHostNotInAllowedHosts()
    {
        await using var redirector = new LoopbackServer(_ => (302, "http://not-allowed.example.invalid/", ""));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            WorkflowHttpSteps.ExecuteAsync(client, $"http://127.0.0.1:{redirector.Port}/", ["127.0.0.1"], "GET", null, 5000, default));
        Assert.Contains("not-allowed.example.invalid", ex.Message);
    }
}

public class WorkflowLoopExpansionTests
{
    [Fact]
    public void Expand_ItemsBasedLoop_SubstitutesLoopItemAndLoopIndexPerIteration()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"id":"loop1","kind":"bounded_loop","items":["a","b","c"],"template":{"app":"","tool":"transform_data","args":{"value_ref":"{{loop_item}}","note":"iteration {{loop_index}}"}},"prerequisites":["seed"]}
            """);
        var expanded = WorkflowLoopExpansion.Expand("loop1", doc.RootElement);

        Assert.Equal(3, expanded.Count);
        Assert.Equal("loop1#0", expanded[0].Id);
        Assert.Equal("loop1#2", expanded[2].Id);
        Assert.All(expanded, spec => Assert.Equal("transform_data", spec.Tool));
        Assert.All(expanded, spec => Assert.Equal(["seed"], spec.Prerequisites));

        using var arg0 = System.Text.Json.JsonDocument.Parse(expanded[0].ArgsJson);
        Assert.Equal("a", arg0.RootElement.GetProperty("value_ref").GetString());
        Assert.Equal("iteration 0", arg0.RootElement.GetProperty("note").GetString());
        using var arg2 = System.Text.Json.JsonDocument.Parse(expanded[2].ArgsJson);
        Assert.Equal("c", arg2.RootElement.GetProperty("value_ref").GetString());
        Assert.Equal("iteration 2", arg2.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void Expand_CountBasedLoop_SubstitutesOnlyLoopIndex()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"id":"retry","kind":"bounded_loop","count":4,"template":{"app":"App","tool":"wait_for","args":{"label":"attempt-{{loop_index}}"}}}
            """);
        var expanded = WorkflowLoopExpansion.Expand("retry", doc.RootElement);

        Assert.Equal(4, expanded.Count);
        Assert.Equal("App", expanded[1].App);
        Assert.Empty(expanded[1].Prerequisites);
        using var arg1 = System.Text.Json.JsonDocument.Parse(expanded[1].ArgsJson);
        Assert.Equal("attempt-1", arg1.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public void Expand_SubstitutesNestedObjectsAndArraysRecursively()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"id":"l","kind":"bounded_loop","items":["x"],"template":{"tool":"t","args":{"outer":{"inner":["{{loop_item}}","static"]}}}}
            """);
        var expanded = WorkflowLoopExpansion.Expand("l", doc.RootElement);

        using var arg = System.Text.Json.JsonDocument.Parse(expanded[0].ArgsJson);
        var inner = arg.RootElement.GetProperty("outer").GetProperty("inner");
        Assert.Equal("x", inner[0].GetString());
        Assert.Equal("static", inner[1].GetString());
    }

    [Fact]
    public void Expand_CountAboveHardCap_ThrowsBeforeAnyNodeIsCreated()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"id":"huge","kind":"bounded_loop","count":51,"template":{"tool":"t","args":{}}}
            """);
        var ex = Assert.Throws<ArgumentException>(() => WorkflowLoopExpansion.Expand("huge", doc.RootElement));
        Assert.Contains("51", ex.Message);
        Assert.Contains("hard cap of 50", ex.Message);
    }

    [Fact]
    public void Expand_ItemsAboveHardCap_ThrowsBeforeAnyNodeIsCreated()
    {
        var items = string.Join(",", Enumerable.Range(0, 51).Select(i => $"\"i{i}\""));
        var json = "{\"id\":\"huge\",\"kind\":\"bounded_loop\",\"items\":[" + items + "],\"template\":{\"tool\":\"t\",\"args\":{}}}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => WorkflowLoopExpansion.Expand("huge", doc.RootElement));
    }

    [Fact]
    public void Expand_NegativeCount_IsRejected()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"id":"neg","kind":"bounded_loop","count":-1,"template":{"tool":"t","args":{}}}
            """);
        Assert.Throws<ArgumentException>(() => WorkflowLoopExpansion.Expand("neg", doc.RootElement));
    }

    [Fact]
    public void Expand_BothItemsAndCount_IsRejected()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"id":"both","kind":"bounded_loop","items":["a"],"count":1,"template":{"tool":"t","args":{}}}
            """);
        var ex = Assert.Throws<ArgumentException>(() => WorkflowLoopExpansion.Expand("both", doc.RootElement));
        Assert.Contains("exactly one", ex.Message);
    }

    [Fact]
    public void Expand_NeitherItemsNorCount_IsRejected()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"id":"neither","kind":"bounded_loop","template":{"tool":"t","args":{}}}
            """);
        Assert.Throws<ArgumentException>(() => WorkflowLoopExpansion.Expand("neither", doc.RootElement));
    }

    [Fact]
    public void Expand_MissingTemplate_IsRejected()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"id":"notpl","kind":"bounded_loop","count":1}
            """);
        Assert.Throws<ArgumentException>(() => WorkflowLoopExpansion.Expand("notpl", doc.RootElement));
    }

    [Fact]
    public void Expand_ZeroCount_ReturnsAnEmptyListNotAnError()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"id":"zero","kind":"bounded_loop","count":0,"template":{"tool":"t","args":{}}}
            """);
        var expanded = WorkflowLoopExpansion.Expand("zero", doc.RootElement);
        Assert.Empty(expanded);
    }
}
