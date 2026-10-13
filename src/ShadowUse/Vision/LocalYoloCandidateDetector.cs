// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Diagnostics;
using System.Text.Json;
using ShadowUse.Config;

namespace ShadowUse.Vision;

public sealed record LocalYoloUiProposal(
    string Key,
    string Role,
    string Label,
    int X,
    int Y,
    int Width,
    int Height,
    double Score);

public sealed record LocalYoloDetectionResult(
    int ImageWidth,
    int ImageHeight,
    IReadOnlyList<LocalYoloUiProposal> Detections,
    int DetectedCount,
    bool DetectionsTruncated,
    double DetectorMs);

public interface ILocalYoloCandidateDetector : IAsyncDisposable
{
    Task EnsureReadyAsync(CancellationToken ct = default);

    Task<LocalYoloDetectionResult> DetectAsync(
        byte[] jpeg,
        string frameId,
        int expectedImageWidth,
        int expectedImageHeight,
        CancellationToken ct = default);
}

/// <summary>One local Python/TorchScript YOLO worker, started on first use and reused for later frames.</summary>
public sealed class LocalYoloCandidateDetector(ShadowSettings settings) : ILocalYoloCandidateDetector
{
    private const int MaxRawDetections = 1024;
    private const int StartupTimeoutMs = 120_000;
    private const int RequestTimeoutMs = 90_000;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private Task<string>? stderrTask;
    private bool disposed;
    private int disposeStarted;

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is { HasExited: false }) return;
            if (process is not null) await DisposeWorkerAsync().ConfigureAwait(false);
            process = await StartWorkerAsync(ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<LocalYoloDetectionResult> DetectAsync(
        byte[] jpeg,
        string frameId,
        int expectedImageWidth,
        int expectedImageHeight,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        if (jpeg.Length is < 1 or > 32 * 1024 * 1024) throw new ArgumentException("The detector image must be 1 byte to 32 MiB.", nameof(jpeg));
        if (string.IsNullOrWhiteSpace(frameId) || frameId.Length > 200) throw new ArgumentException("frameId must contain 1-200 characters.", nameof(frameId));
        if (expectedImageWidth < 1 || expectedImageHeight < 1) throw new ArgumentOutOfRangeException(nameof(expectedImageWidth));

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is null)
                throw new InvalidOperationException("The local YOLO worker is not ready; take a fresh screenshot after worker startup.");
            if (process.HasExited)
            {
                var failure = WorkerFailure("The local YOLO worker exited before this screenshot was analyzed.");
                await DisposeWorkerAsync().ConfigureAwait(false);
                throw failure;
            }

            var active = process;
            var requestId = Guid.NewGuid().ToString("N");
            string request = JsonSerializer.Serialize(new
            {
                request_id = requestId,
                frame_id = frameId,
                image_base64 = Convert.ToBase64String(jpeg),
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeoutMs);
            try
            {
                await active.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
                await active.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                string? response = await active.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (response is null)
                {
                    var failure = WorkerFailure("The local YOLO worker exited before returning a response.");
                    await DisposeWorkerAsync().ConfigureAwait(false);
                    throw failure;
                }
                if (response.Length > 4 * 1024 * 1024) throw new InvalidDataException("The local YOLO worker response exceeded 4 MiB.");
                return ParseResponse(response, requestId, frameId, expectedImageWidth, expectedImageHeight);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await DisposeWorkerAsync().ConfigureAwait(false);
                throw new TimeoutException($"The local YOLO worker did not return within {RequestTimeoutMs} ms.");
            }
            catch (OperationCanceledException)
            {
                await DisposeWorkerAsync().ConfigureAwait(false);
                throw;
            }
            catch (IOException ex)
            {
                var failure = WorkerFailure("The local YOLO worker pipe failed: " + ex.Message);
                await DisposeWorkerAsync().ConfigureAwait(false);
                throw failure;
            }
        }
        finally { gate.Release(); }
    }

    private async Task<Process> StartWorkerAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.LocalUiYoloPythonExecutable))
            throw new InvalidOperationException("Set LocalUiYoloPythonExecutable in settings.json to an isolated Python environment with torch, torchvision, numpy, and Pillow installed.");
        if (string.IsNullOrWhiteSpace(settings.LocalUiYoloWeightsPath) || !Path.IsPathFullyQualified(settings.LocalUiYoloWeightsPath)
            || !File.Exists(settings.LocalUiYoloWeightsPath))
            throw new InvalidOperationException("Set LocalUiYoloWeightsPath in settings.json to an existing absolute path for the separately obtained MIT icon_detect_v3/model.pt weights.");
        if (settings.LocalUiYoloImageSize is not (640 or 960 or 1280 or 1536))
            throw new InvalidOperationException("LocalUiYoloImageSize must be 640, 960, 1280, or 1536.");
        string script = ResolveWorkerScript();
        if (!File.Exists(script)) throw new FileNotFoundException("The packaged local detector script was not found.", script);

        var start = new ProcessStartInfo
        {
            FileName = settings.LocalUiYoloPythonExecutable,
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--serve");
        start.ArgumentList.Add("--weights");
        start.ArgumentList.Add(settings.LocalUiYoloWeightsPath);
        start.ArgumentList.Add("--device");
        start.ArgumentList.Add(settings.LocalUiYoloDevice);
        start.ArgumentList.Add("--image-size");
        start.ArgumentList.Add(settings.LocalUiYoloImageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--confidence");
        start.ArgumentList.Add("0.001");
        start.ArgumentList.Add("--iou");
        start.ArgumentList.Add("0.45");
        start.ArgumentList.Add("--max-raw-detections");
        start.ArgumentList.Add(MaxRawDetections.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var child = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!child.Start())
        {
            child.Dispose();
            throw new InvalidOperationException("The configured Python executable did not start.");
        }
        var childStderr = child.StandardError.ReadToEndAsync();
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startupTimeout.CancelAfter(StartupTimeoutMs);
        try
        {
            string? ready = await child.StandardOutput.ReadLineAsync(startupTimeout.Token).ConfigureAwait(false);
            if (ready is null)
            {
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); } catch { }
                throw new InvalidOperationException("The local YOLO worker exited before it became ready. " + StderrSummary(childStderr));
            }
            using var document = JsonDocument.Parse(ready);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ready", out var readyValue) || readyValue.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("protocol_version", out var version) || version.GetInt32() != 1)
                throw new InvalidDataException("The local YOLO worker returned an invalid startup handshake.");
            stderrTask = childStderr;
            return child;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(child);
            throw new TimeoutException($"The local YOLO worker did not load and warm its model within {StartupTimeoutMs} ms.");
        }
        catch
        {
            Kill(child);
            throw;
        }
    }

    private static string ResolveWorkerScript()
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, "tools", "ui_candidate_detect.py");
        if (File.Exists(packaged)) return packaged;
        string workingDirectory = Path.Combine(Directory.GetCurrentDirectory(), "tools", "ui_candidate_detect.py");
        return Path.GetFullPath(workingDirectory);
    }

    private LocalYoloDetectionResult ParseResponse(string json, string requestId, string frameId, int width, int height)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The local YOLO worker response must be an object.");
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            throw new InvalidOperationException("Local YOLO inference failed: " + error.GetString());
        if (!ReadString(root, "request_id", out var returnedRequest) || returnedRequest != requestId
            || !ReadString(root, "frame_id", out var returnedFrame) || returnedFrame != frameId
            || !ReadInt(root, "image_width", out var returnedWidth) || returnedWidth != width
            || !ReadInt(root, "image_height", out var returnedHeight) || returnedHeight != height
            || !ReadInt(root, "detected_count", out int detectedCount) || detectedCount < 0
            || !root.TryGetProperty("detections_truncated", out var truncatedElement)
            || truncatedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("detections", out var detectionsElement)
            || detectionsElement.ValueKind != JsonValueKind.Array || detectionsElement.GetArrayLength() > MaxRawDetections)
            throw new InvalidDataException("The local YOLO response has a mismatched request/frame, image geometry, or detections list.");

        var candidates = new List<LocalYoloUiProposal>(detectionsElement.GetArrayLength());
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in detectionsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !ReadString(item, "key", out var key) || key.Length > 80 || !keys.Add(key)
                || !ReadString(item, "role", out var role) || role.Length > 80
                || !ReadString(item, "label", out var label) || label.Length > 240
                || !item.TryGetProperty("bbox", out var bbox) || bbox.ValueKind != JsonValueKind.Object
                || !ReadInt(bbox, "x", out int x) || !ReadInt(bbox, "y", out int y)
                || !ReadInt(bbox, "width", out int boxWidth) || !ReadInt(bbox, "height", out int boxHeight)
                || x < 0 || y < 0 || boxWidth < 1 || boxHeight < 1
                || (long)x + boxWidth > width || (long)y + boxHeight > height
                || !item.TryGetProperty("score", out var scoreElement) || !scoreElement.TryGetDouble(out double score)
                || !double.IsFinite(score) || score is < 0 or > 1)
                throw new InvalidDataException("A local YOLO detection has an invalid key, label, score, or out-of-frame bbox.");
            candidates.Add(new(key, role, label, x, y, boxWidth, boxHeight, score));
        }

        double detectorMs = root.TryGetProperty("latency", out var latency)
            && latency.ValueKind == JsonValueKind.Object
            && latency.TryGetProperty("total_ms", out var total)
            && total.TryGetDouble(out var measured) && double.IsFinite(measured) && measured >= 0
                ? measured : 0;
        return new(width, height, candidates, detectedCount, truncatedElement.GetBoolean(), detectorMs);
    }

    private static bool ReadString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool ReadInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private InvalidOperationException WorkerFailure(string prefix)
    {
        string stderr = stderrTask is { IsCompletedSuccessfully: true } ? stderrTask.Result : "";
        return new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? prefix : prefix + " " + stderr[..Math.Min(stderr.Length, 1_000)]);
    }

    private static string StderrSummary(Task<string> stderr)
        => stderr.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(stderr.Result)
            ? stderr.Result[..Math.Min(stderr.Result.Length, 1_000)] : "";

    private static void Kill(Process child)
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
        try { child.Dispose(); } catch { }
    }

    private async Task DisposeWorkerAsync()
    {
        if (process is not { } child) return;
        process = null;
        try { child.StandardInput.Close(); } catch { }
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch { Kill(child); }
        try { child.Dispose(); } catch { }
        stderrTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await DisposeWorkerAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }
}
