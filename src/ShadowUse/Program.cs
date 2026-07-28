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

        var builder = Host.CreateApplicationBuilder(args);

        // stdout is the MCP channel — all logging goes to stderr
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var uiaThread = new UiaThread();
        builder.Services.AddSingleton(uiaThread);
        builder.Services.AddSingleton<UiaService>();
        builder.Services.AddSingleton(new BackgroundInput(uiaThread));
        builder.Services.AddSingleton(Config.ShadowSettings.Load());
        builder.Services.AddSingleton(_ => VirtualCursorOverlay.Instance);

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "dcu", Version = "0.1.1" };
                o.ServerInstructions = """
                    Background computer control for Windows. All actions are focus-free:
                    UIA patterns or posted window messages — the real cursor never moves,
                    the foreground never changes, and target apps never count as focused.
                    A cosmetic virtual cursor shows actions to the user.

                    Workflow: list_apps → get_app_state(app) → act by element id
                    (click/type_text/press_key/scroll/set_value/drag) → re-snapshot.
                    Element ids are tied to the snapshot that produced them: if an id is
                    unknown, or its element has since vanished, re-run get_app_state.
                    Raw x,y coordinate clicks are NOT guarded against the window having
                    moved unless the EnableBoundsGuard setting is turned on (off by
                    default) — prefer element ids when precision matters. Use wait_for
                    to poll without snapshots, execute_sequence to batch steps server-side.
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
