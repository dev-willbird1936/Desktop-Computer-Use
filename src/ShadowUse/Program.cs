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
using ShadowUse.Vision;

namespace ShadowUse;

internal static class Program
{
    private const string ServerInstructions = """
        Background computer control for Windows. By default, background_mode is strict_background:
        UIA patterns or posted window messages. The real cursor never moves and the
        foreground never changes. A cosmetic virtual cursor shows actions.

        HUMANIZED MODE
        For games and apps that require real input (Unreal Engine, Unity, DirectInput, Raw Input),
        set background_mode to humanized on click, type_text, press_key, scroll, and drag.
        This mode brings the target window to the foreground, moves the real cursor, and injects
        real hardware-level input via SendInput. The game receives real WM_INPUT events.
        Cursor paths are curved, with jitter, overshoot, tremor, and variable timing.
        Typing uses per-key timing, digraph effects, and self-corrected typos.
        The user cursor and foreground window are restored afterward.

        If the user says humanized, or the target is a game, always pass background_mode=humanized.
        Do not omit it. An omitted value is strict_background unless settings.json has HumanizedInput true.
        strict_background only posts window messages. Unreal and Unity ignore those.

        IF HUMANIZED INPUT IS IGNORED
        1. The call omitted background_mode, so DCU posted messages. Pass background_mode=humanized explicitly.
        2. The game is elevated and DCU is not. Run dcu.exe as Administrator when the game is.
        3. The game window is minimized or on another desktop. It must be visible on this desktop.
        4. Games have no UIA tree. A success result does not prove the world changed. Compare screenshots.
        5. A locked or recentred cursor (mouse-look) is not moved to screen x,y. Use drag for relative aim.

        GAME MODE
        When settings.json has GameMode true, game_* tools are listed. Prefer them for games over get_app_state
        (which is large and has no UIA tree for games). game_screenshot returns a small JPEG and image_scale;
        game_click, game_move, game_drag, game_scroll take pixels of that screenshot. game_key does tap, hold, down,
        up, and repeat, with chords such as shift+w. game_ocr reads text, game_bars reads HUD bars, game_wait waits
        for text or picture changes. Actions report effect as frame_changed, minor_change, no_change, or unverified.
        no_change means the game showed nothing new, not that an error occurred. Add then_screenshot=true to see the result
        in the same call. Mouse buttons are keys in game_key (lmb, rmb, mmb; rmb+lmb). pace=fast shortens timing for one call;
        observe=text,motion adds OCR text and world shift to the result. Input is real, and humanized when HumanizedInput is on. If game_* tools are missing, ask the
        user to enable GameMode and restart DCU.
        Send an input once, then wait for the result: pass progress_box or expected_effect with observe_timeout_ms (default 1500,
        up to 60000) for slow menus and loads instead of repeating the input. camera_translation in motion means the view scrolled,
        not that the avatar moved. game_wait returns the last frame on timeout, and a stopped game sequence attaches one.
        execute_sequence game results are compact: shared metadata appears once under shared.

        WHICH TOGGLES
        GameMode: playing a game or a window with no UIA tree. HumanizedInput: input must look human; usually wanted for games.
        FastInput: lower latency needed; usually wanted for games (pace=fast for one call). JevMode controls recommendation versus
        execution for supported desktop/browser actions; GameMode does not enable Jev or authorize input.
        Experimental BTD2 Jev actions require EnableExperimentalGameJevAct=true in the isolated experimental MCP settings,
        JevMode=fast_loop, DecisionBackend=jev, model jev-1.14.0 or jev-preview, a caller-bounded action list, and execute=true to dispatch.
        Preview with execute=false first. Click menus use one image request for Choice/Noul/Box; key-only menus send state/history without an image. max_actions defaults to 1; an opt-in serial run is limited to 4 unique actions and 60 seconds, uses a fresh frame before each decision, and does not verify after each dispatched action. Input sent is not proof of success. Static-menu region stability is one-step only, explicitly opt-in and keeps post-action verification; it never overrides unavailable frame evidence or detected camera motion. Refusals require reobserve, not retries. Score cutoffs are configured in the isolated settings.json: ExperimentalGameJevActChoiceThreshold (default 0.85), ExperimentalGameJevActClickThreshold (default 0.90), and ExperimentalGameJevActAmbiguityMargin (default 0.10). Zero disables only that score cutoff; all structural, model, frame, region, target, and ownership checks remain.

        Tool list: only the core desktop loop is listed. More capabilities (browser, workflows,
        subgoals, memory, files, HTTP, grids, voice, review, isolation, demonstrations, Jev) are
        found with search_tools, inspected with describe_tool, and run with call_tool.

        Workflow: list_apps, then get_app_state(app), then act by element id
        (click, type_text, press_key, scroll, set_value, drag), then re-snapshot.
        App selectors accept names, title substrings, PIDs, window_id, and target_id.
        Explicit window_id and target_id selectors never fall through to fuzzy matching.
        Element ids belong to the snapshot that produced them. If an id is unknown, re-run get_app_state.
        Prefer element ids over raw coordinates. Use wait_for to poll and execute_sequence to batch steps.
        """;

    private static async Task<int> Main(string[] args)
    {
        _ = RuntimeIdentification.Current;
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

    // Game mode owns the injection backend and the fast input profile; with it off DCU keeps plain SendInput and the cautious profile.
    private static void ConfigureGameMode(ShadowSettings s)
    {
        ToolCatalog.GameModeEnabled = s.GameMode;
        InteractiveInput.GameProfile = s.GameMode;
        InputPace.GlobalFast = s.FastInput;
        if (!s.GameMode) return;
        InteractiveInput.ConfigureBackend(new InputBackendConfig
        {
            Mode = Enum.TryParse<InputBackendKind>(s.InputBackendMode, true, out var mode) ? mode : InputBackendKind.SendInput,
            DriverKind = Enum.TryParse<KernelDriverKind>(s.KernelDriverKind, true, out var kind) ? kind : KernelDriverKind.Interception,
            HardwareComPort = s.HardwareComPort,
            HardwareBaudRate = s.HardwareBaudRate,
            AutoAttachPid = s.MemoryWriteTargetPid,
        });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => InteractiveInput.ShutdownInput();
    }

    // Shared DI registrations: HostApplicationBuilder.Services and WebApplicationBuilder.Services
    // are both IServiceCollection, so the exact same DCU service graph backs either transport --
    // F44-R02's "same tools, not a reduced safe subset" is true by construction, not a second
    // maintained tool registration.
    private static void ConfigureCommonServices(IServiceCollection services, ShadowSettings settings)
    {
        ConfigureGameMode(settings);
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
        services.AddSingleton<GameTrackStore>();
        string localStateDirectory = Path.Combine(ShadowSettings.LocalApplicationDataFolder(), "shadow-use");
        services.AddSingleton(_ => new AppActionCatalog(Path.Combine(localStateDirectory, "action-catalogs.json")));
        services.AddSingleton(_ => new ScreenStateStore(persistencePath: Path.Combine(localStateDirectory, "screen-map.json")));
        services.AddSingleton<ILocalYoloCandidateDetector, LocalYoloCandidateDetector>();
        services.AddSingleton<IScreenDiscoveryBackend, LocalScreenDiscoveryBackend>();
        services.AddSingleton<ScreenDiscoveryService>();
        services.AddSingleton<GameTools>();
        services.AddSingleton<IGameJevActionFrameSource, GameJevActionFrameSource>();
        services.AddSingleton<IGameJevActionDispatcher, GameJevActionDispatcher>();
        services.AddSingleton<ImageUiCandidateTools>();
        services.AddSingleton<GameJevTools>();
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
            return new OpenAiResponsesDecisionBackend(new HttpClient(), new OpenAiDecisionOptions
            {
                Model = s.OpenAiModel,
                TimeoutMs = s.OpenAiTimeoutMs,
            });
        });
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
        services.AddSingleton(sp => new DecisionBackendRouter(
            sp.GetRequiredService<ShadowSettings>(), sp.GetRequiredService<JevController>(),
            sp.GetRequiredService<OpenAiResponsesDecisionBackend>()));
        // Explicit registration: ComputerUseTools optionally takes BrowserUseTools (F29 cross-adapter
        // workflow dispatch) as a constructor parameter with a null default. WithToolsFromAssembly()
        // below registers tool types for MCP invocation, but DI silently falls back to that null
        // default for any OTHER constructor's parameter unless the type is registered here too.
        services.AddSingleton<BrowserUseTools>();
    }

    // Shared MCP registration for both transports. tools/list shows the core loop, the discovery
    // gateway, and session-loaded tools (ToolCatalog); tools/call still resolves the full catalogue.
    private static IMcpServerBuilder AddDcuMcpServer(IServiceCollection services, ShadowSettings settings) =>
        services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "dcu", Version = RuntimeIdentification.Current.Version };
                o.ServerInstructions = ServerInstructions + "\n" + RuntimeIdentification.InstructionsSuffix(RuntimeIdentification.Current);
                o.Capabilities = new() { Tools = new() { ListChanged = true } };
            })
            .WithToolsFromAssembly()
            .WithRequestFilters(f => f.AddListToolsFilter(next => async (ctx, ct) =>
            {
                var result = await next(ctx, ct).ConfigureAwait(false);
                if (ctx.Server.ServerOptions.ToolCollection is { } tools) ToolCatalog.ApplyAnnotations(tools);
                if (settings.ToolExposure != "all")
                    result.Tools = result.Tools.Where(t => ToolCatalog.IsVisible(ctx.Server, t.Name, exposeAll: false)).ToList();
                return result;
            }));

    private static async Task<int> RunStdioAsync(string[] args, ShadowSettings settings)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout is the MCP channel — all logging goes to stderr
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        ConfigureCommonServices(builder.Services, settings);

        AddDcuMcpServer(builder.Services, settings)
            .WithStdioServerTransport();

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

        AddDcuMcpServer(builder.Services, settings)
            .WithHttpTransport();

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
            try { InteractiveInput.ShutdownInput(); } catch { /* best-effort cleanup */ }
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
