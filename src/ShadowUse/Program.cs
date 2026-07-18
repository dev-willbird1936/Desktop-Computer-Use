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
                o.ServerInfo = new() { Name = "dcu", Version = "0.1.0" };
                o.ServerInstructions = """
                    Background computer control for Windows. All actions are focus-free:
                    UIA patterns or posted window messages — the real cursor never moves,
                    the foreground never changes, and target apps never count as focused.
                    A cosmetic virtual cursor shows actions to the user.

                    Workflow: list_apps → get_app_state(app) → act by element id
                    (click/type_text/press_key/scroll/set_value/drag) → re-snapshot.
                    Coordinate clicks are guarded: if the window moved since the last
                    snapshot, you must re-snapshot first. Use wait_for to poll without
                    snapshots, execute_sequence to batch steps server-side.
                    """;
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        var host = builder.Build();
        host.Services.GetRequiredService<VirtualCursorOverlay>(); // warm the overlay thread
        try
        {
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        finally
        {
            host.Services.GetRequiredService<VirtualCursorOverlay>().Dispose();
            host.Services.GetRequiredService<UiaThread>().Dispose();
        }
    }

    private static int Doctor()
    {
        Console.Error.WriteLine("shadow-use doctor");
        var err = Safety.Guard.CheckInteractiveDesktop();
        Console.Error.WriteLine(err == null ? "  interactive desktop: OK" : $"  interactive desktop: {err}");
        try
        {
            using var t = new UiaThread();
            var apps = t.InvokeAsync(() => System.Diagnostics.Process.GetProcesses()
                .Count(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } })).Result;
            Console.Error.WriteLine($"  UIA thread: OK ({apps} apps with windows)");
        }
        catch (Exception ex) { Console.Error.WriteLine($"  UIA thread: FAIL {ex.Message}"); return 1; }
        return err == null ? 0 : 1;
    }
}
