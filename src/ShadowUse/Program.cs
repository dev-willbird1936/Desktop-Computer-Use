// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShadowUse.Automation;
using ShadowUse.Overlay;

namespace ShadowUse;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--doctor"))
            return Doctor();

        var settings = Config.ShadowSettings.Load();
        InputPace.GlobalFast = settings.FastInput;
        InteractiveInput.GameProfile = settings.GameMode;
        var builder = Host.CreateApplicationBuilder(args);

        // stdout is the MCP channel — all logging goes to stderr
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var uiaThread = new UiaThread();
        builder.Services.AddSingleton(uiaThread);
        builder.Services.AddSingleton<WindowUiaWorkers>();
        builder.Services.AddSingleton<UiaService>();
        builder.Services.AddSingleton<BackgroundInput>();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<ShadowUse.Vision.GameFrameStore>();
        builder.Services.AddSingleton(_ => VirtualCursorOverlay.Instance);

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "dcu", Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown" };
                o.ServerInstructions = """
                    Windows computer control. Default input_mode=background uses UIA patterns
                    or posted messages without moving the real cursor; focus-changing UIA
                    fallbacks remain possible. A cosmetic virtual cursor hides after one idle minute.
                    input_mode=foreground explicitly activates the selected window and uses the
                    real cursor/keyboard through SendInput. UseRealCursor changes that default.
                    HumanizedInput shapes foreground motion/timing; FastInput shortens pacing.
                    You can override mode and pace on each action without editing settings.
                    input_mode=humanized explicitly selects humanized foreground input even
                    when the HumanizedInput setting is off. foreground_plain disables it for one call.
                    Games/raw-input apps commonly require foreground mode. game_* tools work
                    without a UIA tree and are always callable; GameMode selects the game input
                    profile, not a tool-access gate. Use game_screenshot and pass its
                    source_frame_id for image coordinates. For GPU capture that fails in background,
                    game_screenshot(bring_to_front=true) explicitly activates the selected window.
                    Never infer success from dispatch:
                    check effect or then_screenshot. Use bounded game_key holds; do not leave
                    keys down across agent calls.
                    Multiple agents can observe and act on separate windows. Element ids survive
                    subsequent snapshots within bounded history, but are specific to this server.
                    Same-window actions/sequences queue across DCU processes; real-cursor input
                    also queues for the one shared system cursor. No persistent controller claim.

                    Workflow: list_apps → select by window_id when needed → get_app_state(app) → act by element id
                    (click/type_text/press_key/scroll/set_value/drag) → re-snapshot.
                    Element ids are tied to the snapshot that produced them: if an id is
                    unknown, or its element has since vanished, re-run get_app_state.
                    Snapshot-based raw coordinates translate with window movement unless
                    EnableBoundsGuard is on, which rejects changed bounds. Prefer element
                    ids for responsive layouts and after resize. Use wait_for to poll
                    without snapshots, execute_sequence to batch steps server-side.
                    """;
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        var host = builder.Build();
        // Capture cleanup services BEFORE RunAsync — the host disposes the service
        // provider on exit, so resolving them in finally would throw ObjectDisposedException
        // and mask a clean shutdown with exit code 1.
        var overlay = host.Services.GetRequiredService<VirtualCursorOverlay>(); // warms the overlay thread
        var uia = host.Services.GetRequiredService<UiaThread>();
        try
        {
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        finally
        {
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
