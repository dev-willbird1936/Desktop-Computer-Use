// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ShadowUse.Automation;
using ShadowUse.Browser;
using ShadowUse.Config;
using ShadowUse.Jev;
using ShadowUse.Overlay;
using ShadowUse.Tools;

namespace ShadowUse;

internal static class Program
{
    private const string ServerInstructions = """
        Background computer control for Windows. All actions are focus-free:
        UIA patterns or posted window messages — the real cursor never moves,
        the foreground never changes, and target apps never count as focused.
        A cosmetic virtual cursor shows actions and hides after one idle minute.

        Workflow: list_apps → select by target_id for a lifetime-bound window, or by window_id
        for exact-window compatibility, when needed → get_app_state(app) → act by element id
        (click/type_text/press_key/scroll/set_value/drag) → re-snapshot.
        App selectors accept names, title substrings, PIDs, window_id, and target_id;
        explicit window_id and target_id selectors never fall through to fuzzy matching.
        Element ids are tied to the snapshot that produced them: if an id is
        unknown, or its element has since vanished, re-run get_app_state.
        Snapshot-based raw coordinates translate with window movement unless
        EnableBoundsGuard is on, which rejects changed bounds. Prefer element
        ids for responsive layouts and after resize. Use wait_for to poll
        without snapshots, execute_sequence to batch steps server-side.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--doctor"))
            return Doctor();

        // F44: read settings ONCE, before choosing a transport, rather than letting each builder
        // path load settings.json independently. A non-empty RemoteTransportUrl is the sole,
        // explicit opt-in for the HTTP path (F44-R01) -- the default (null/empty) never touches
        // WebApplication/Kestrel/ASP.NET Core at runtime at all (F44-T02).
        var settings = ShadowSettings.Load();
        return string.IsNullOrWhiteSpace(settings.RemoteTransportUrl)
            ? await RunStdioAsync(args, settings).ConfigureAwait(false)
            : await RunHttpAsync(args, settings).ConfigureAwait(false);
    }

    // Shared DI registrations: HostApplicationBuilder.Services and WebApplicationBuilder.Services
    // are both IServiceCollection, so the exact same DCU service graph backs either transport --
    // F44-R02's "same tools, not a reduced safe subset" is true by construction, not a second
    // maintained tool registration.
    private static void ConfigureCommonServices(IServiceCollection services, ShadowSettings settings)
    {
        var uiaThread = new UiaThread();
        services.AddSingleton(uiaThread);
        services.AddSingleton<TargetRegistry>();
        services.AddSingleton<NativeTargetMonitor>();
        services.AddSingleton<UiaService>();
        services.AddSingleton(new BackgroundInput(uiaThread));
        services.AddSingleton(settings);
        services.AddSingleton(_ => VirtualCursorOverlay.Instance);
        services.AddSingleton<WorkflowValueStore>();
        services.AddSingleton<ActionResolutionCache>();
        services.AddSingleton<LoopGuard>();
        services.AddSingleton<ExperienceStore>();
        services.AddSingleton<JevInspector>();
        services.AddSingleton<WorkflowRunner>();
        services.AddSingleton<ResourceLeaseRegistry>();
        services.AddSingleton<DemonstrationStore>();
        services.AddSingleton<HelperStore>();
        services.AddSingleton<WorkspaceStore>();
        services.AddSingleton<SearchSequencer>();
        services.AddSingleton<ModelRoutingRegistry>();
        services.AddSingleton<SubgoalStore>();
        services.AddSingleton<CompositionStore>();
        services.AddSingleton<ReviewRoomStore>();
services.AddSingleton<TextGenerationCache>();
services.AddSingleton<GridPredictionStore>();
services.AddSingleton<VoiceTurnController>();
services.AddSingleton<IsolatedEnvironmentRegistry>();
        services.AddSingleton(sp =>
        {
            var s = sp.GetRequiredService<ShadowSettings>();
            return new RunTimeline(s.EnableRunTimeline, s.RunTimelinePath);
        });
        services.AddSingleton<BrowserSessionManager>();
        services.AddSingleton(sp =>
        {
            var s = sp.GetRequiredService<ShadowSettings>();
            var options = new JevOptions
            {
                Mode = s.JevMode switch
                {
                    "assist" => JevMode.Assist,
                    "fast_loop" => JevMode.FastLoop,
                    _ => JevMode.Off,
                },
                Model = s.JevModel,
                BaseUrl = s.JevBaseUrl,
                TimeoutMs = s.JevTimeoutMs,
            };
            return new JevController(new HttpClient(), options);
        });
        // Explicit registration: ComputerUseTools optionally takes BrowserUseTools (F29 cross-adapter
        // workflow dispatch) as a constructor parameter with a null default. WithToolsFromAssembly()
        // below registers tool types for MCP invocation, but DI silently falls back to that null
        // default for any OTHER constructor's parameter unless the type is registered here too.
        services.AddSingleton<BrowserUseTools>();
    }

    private static async Task<int> RunStdioAsync(string[] args, ShadowSettings settings)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout is the MCP channel — all logging goes to stderr
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        ConfigureCommonServices(builder.Services, settings);

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "dcu", Version = "0.1.1" };
                o.ServerInstructions = ServerInstructions;
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        var host = builder.Build();
        return await RunHostAsync(host.Services, () => host.RunAsync()).ConfigureAwait(false);
    }

    private static async Task<int> RunHttpAsync(string[] args, ShadowSettings settings)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Same reasoning as stdio: DCU's own stdout stays free of framework noise, though HTTP
        // mode does not use stdout as a protocol channel the way stdio mode does.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        ConfigureCommonServices(builder.Services, settings);

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "dcu", Version = "0.1.1" };
                o.ServerInstructions = ServerInstructions;
            })
            .WithHttpTransport()
            .WithToolsFromAssembly();

        var app = builder.Build();
        app.MapMcp();
        Console.Error.WriteLine(
            $"DCU remote MCP transport enabled at {settings.RemoteTransportUrl} -- exposes the full desktop/browser tool set, " +
            "not a reduced subset. Transport authentication (VPN, reverse proxy, firewall) is the operator's responsibility.");
        return await RunHostAsync(app.Services, () => app.RunAsync(settings.RemoteTransportUrl)).ConfigureAwait(false);
    }

    // Capture cleanup services BEFORE RunAsync — the host disposes the service provider on exit,
    // so resolving them in finally would throw ObjectDisposedException and mask a clean shutdown
    // with exit code 1. Shared by both transports so cleanup ordering can never drift between them.
    private static async Task<int> RunHostAsync(IServiceProvider services, Func<Task> runAsync)
    {
        var overlay = services.GetRequiredService<VirtualCursorOverlay>(); // warms the overlay thread
        var uia = services.GetRequiredService<UiaThread>();
        var nativeTargetMonitor = services.GetRequiredService<NativeTargetMonitor>();
        var browserSessions = services.GetRequiredService<BrowserSessionManager>();
        try
        {
            await runAsync().ConfigureAwait(false);
            return 0;
        }
        finally
        {
            try { browserSessions.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* best-effort cleanup */ }
            try { nativeTargetMonitor.Dispose(); } catch { /* best-effort cleanup */ }
            try { overlay.Dispose(); } catch { /* best-effort cleanup */ }
            try { uia.Dispose(); } catch { /* best-effort cleanup */ }
        }
    }

    private static int Doctor()
    {
        Console.Error.WriteLine("Desktop Computer Use (DCU) doctor");
        var err = Safety.Guard.CheckInteractiveDesktop();
        Console.Error.WriteLine(err == null ? "  interactive desktop: OK" : $"  interactive desktop: {err}");
        try
        {
            using var t = new UiaThread();
            var apps = t.InvokeAsync(() =>
            {
                var processes = System.Diagnostics.Process.GetProcesses();
                try
                {
                    return processes.Count(p =>
                    {
                        try { return p.MainWindowHandle != IntPtr.Zero; }
                        catch { return false; }
                    });
                }
                finally
                {
                    foreach (var process in processes) process.Dispose();
                }
            }).Result;
            Console.Error.WriteLine($"  UIA thread: OK ({apps} apps with windows)");
        }
        catch (Exception ex) { Console.Error.WriteLine($"  UIA thread: FAIL {ex.Message}"); return 1; }
        return err == null ? 0 : 1;
    }
}
