// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Drawing;

namespace ShadowUse.Vision;

/// <summary>A bounded grayscale view of a captured game frame for pixel tracking.</summary>
public sealed record GameTrackingFrame(
    string FrameId,
    DateTimeOffset Timestamp,
    string? TargetLifetime,
    int Width,
    int Height,
    double ImageScale,
    int PixelWidth,
    int PixelHeight,
    byte[] Pixels);

public sealed record PatchTrackingResult(string State, Rectangle Box, double Confidence, double Margin, string? Reason);

/// <summary>Single-object, fixed-template grayscale tracker. Coordinates use the tracking frame's pixel space.</summary>
public sealed class PatchTracker
{
    private const int TemplateSide = 32;
    private const int SearchRadius = 24;
    private const double MinimumConfidence = 0.72;
    private const double MinimumMargin = 0.04;
    private readonly double[] _centeredTemplate;
    private readonly double _templateNorm;
    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _frameWidth;
    private readonly int _frameHeight;
    private readonly int _windowWidth;
    private readonly int _windowHeight;
    private readonly string? _targetLifetime;
    private Rectangle _lastBox;

    private PatchTracker(GameTrackingFrame frame, Rectangle box, double[] centeredTemplate, double templateNorm, int sampleWidth, int sampleHeight)
    {
        _frameWidth = frame.PixelWidth;
        _frameHeight = frame.PixelHeight;
        _windowWidth = frame.Width;
        _windowHeight = frame.Height;
        _targetLifetime = frame.TargetLifetime;
        _lastBox = box;
        _centeredTemplate = centeredTemplate;
        _templateNorm = templateNorm;
        _sampleWidth = sampleWidth;
        _sampleHeight = sampleHeight;
    }

    public Rectangle LastBox => _lastBox;

    public static bool TryCreate(GameTrackingFrame frame, Rectangle box, out PatchTracker? tracker, out string? error)
    {
        tracker = null;
        error = null;
        if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || frame.Pixels.Length != frame.PixelWidth * frame.PixelHeight)
        {
            error = "The source frame has no valid tracking pixels.";
            return false;
        }
        if (box.Width < 8 || box.Height < 8 || Rectangle.Intersect(box, new Rectangle(0, 0, frame.PixelWidth, frame.PixelHeight)) != box)
        {
            error = "Select a fully visible object region at least 8 by 8 tracking pixels.";
            return false;
        }

        int sampleWidth = Math.Min(TemplateSide, box.Width), sampleHeight = Math.Min(TemplateSide, box.Height);
        var template = new byte[sampleWidth * sampleHeight];
        double mean = 0;
        for (int y = 0; y < sampleHeight; y++)
            for (int x = 0; x < sampleWidth; x++)
            {
                int sx = box.X + Math.Min(box.Width - 1, (2 * x + 1) * box.Width / (2 * sampleWidth));
                int sy = box.Y + Math.Min(box.Height - 1, (2 * y + 1) * box.Height / (2 * sampleHeight));
                byte value = frame.Pixels[sy * frame.PixelWidth + sx];
                template[y * sampleWidth + x] = value;
                mean += value;
            }
        mean /= template.Length;
        var centered = new double[template.Length];
        double norm = 0;
        for (int i = 0; i < template.Length; i++)
        {
            centered[i] = template[i] - mean;
            norm += centered[i] * centered[i];
        }
        if (norm / template.Length < 25)
        {
            error = "The selected region has too little visual texture to track reliably.";
            return false;
        }

        tracker = new PatchTracker(frame, box, centered, norm, sampleWidth, sampleHeight);
        return true;
    }

    public PatchTrackingResult Update(GameTrackingFrame frame)
    {
        if (frame.TargetLifetime != _targetLifetime || frame.Width != _windowWidth || frame.Height != _windowHeight
            || frame.PixelWidth != _frameWidth || frame.PixelHeight != _frameHeight
            || frame.Pixels.Length != frame.PixelWidth * frame.PixelHeight)
            return new PatchTrackingResult("lost", _lastBox, 0, 0, "target_or_frame_geometry_changed");

        int maxX = frame.PixelWidth - _lastBox.Width, maxY = frame.PixelHeight - _lastBox.Height;
        int left = Math.Max(0, _lastBox.X - SearchRadius), right = Math.Min(maxX, _lastBox.X + SearchRadius);
        int top = Math.Max(0, _lastBox.Y - SearchRadius), bottom = Math.Min(maxY, _lastBox.Y + SearchRadius);
        var candidates = new List<(double Score, int X, int Y)>();
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                candidates.Add((Score(frame.Pixels, frame.PixelWidth, x, y), x, y));
        if (candidates.Count == 0)
            return new PatchTrackingResult("lost", _lastBox, 0, 0, "no_valid_search_region");
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var best = candidates[0];
        int ambiguityDistance = Math.Max(4, Math.Min(_lastBox.Width, _lastBox.Height) / 2);
        (double Score, int X, int Y) second = default;
        bool hasSecond = false;
        foreach (var candidate in candidates)
            if (Distance(candidate.X, candidate.Y, best.X, best.Y) >= ambiguityDistance)
            {
                second = candidate;
                hasSecond = true;
                break;
            }
        double margin = hasSecond ? best.Score - second.Score : 1;
        double confidence = Math.Clamp((best.Score + 1) / 2, 0, 1);
        if (confidence < MinimumConfidence)
            return new PatchTrackingResult("uncertain", _lastBox, confidence, margin, "patch_match_below_confidence_threshold");
        if (hasSecond && margin < MinimumMargin)
            return new PatchTrackingResult("uncertain", _lastBox, confidence, margin, "ambiguous_patch_match");

        _lastBox = new Rectangle(best.X, best.Y, _lastBox.Width, _lastBox.Height);
        return new PatchTrackingResult("tracking", _lastBox, confidence, margin, null);
    }

    private static int Distance(int ax, int ay, int bx, int by)
    {
        int dx = ax - bx, dy = ay - by;
        return (int)Math.Sqrt((long)dx * dx + (long)dy * dy);
    }

    private double Score(byte[] pixels, int frameWidth, int x, int y)
    {
        int count = _sampleWidth * _sampleHeight;
        double sum = 0, sumSquares = 0, cross = 0;
        for (int sy = 0; sy < _sampleHeight; sy++)
        {
            int sourceY = y + Math.Min(_lastBox.Height - 1, (2 * sy + 1) * _lastBox.Height / (2 * _sampleHeight));
            int offset = sourceY * frameWidth + x;
            int rowOffset = sy * _sampleWidth;
            for (int sx = 0; sx < _sampleWidth; sx++)
            {
                int sourceX = Math.Min(_lastBox.Width - 1, (2 * sx + 1) * _lastBox.Width / (2 * _sampleWidth));
                double value = pixels[offset + sourceX];
                sum += value;
                sumSquares += value * value;
                cross += _centeredTemplate[rowOffset + sx] * value;
            }
        }
        double variance = sumSquares - sum * sum / count;
        if (variance < 1) return -1;
        return Math.Clamp(cross / Math.Sqrt(_templateNorm * variance), -1, 1);
    }
}

public sealed class GameTrackingSession(string id, IntPtr hwnd, string targetLifetime, PatchTracker tracker, DateTimeOffset now)
{
    private readonly CancellationTokenSource _stop = new();
    public string Id { get; } = id;
    public IntPtr Hwnd { get; } = hwnd;
    public string TargetLifetime { get; } = targetLifetime;
    public PatchTracker Tracker { get; } = tracker;
    public object SyncRoot { get; } = new();
    public CancellationToken StopToken => _stop.Token;
    public DateTimeOffset LastUsed { get; internal set; } = now;
    public PatchTrackingResult LastResult { get; internal set; } = new("tracking", tracker.LastBox, 1, 1, null);
    public Rectangle LastImageBox { get; internal set; }
    public string? LastFrameId { get; internal set; }
    internal void Cancel() => _stop.Cancel();
}

/// <summary>Bounded, target-lifetime-scoped collection of explicit tracking sessions.</summary>
public sealed class GameTrackStore
{
    public const int MaxSessions = 8;
    private static readonly TimeSpan SessionIdleLimit = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, GameTrackingSession> _sessions = new(StringComparer.Ordinal);

    public bool TryStart(IntPtr hwnd, string lifetime, PatchTracker tracker, out GameTrackingSession? session, out string? error)
    {
        lock (_gate)
        {
            PruneNoLock(DateTimeOffset.UtcNow);
            if (_sessions.Count >= MaxSessions)
            {
                session = null;
                error = $"The tracking-session limit ({MaxSessions}) has been reached; stop an unused track first.";
                return false;
            }
            string id = Guid.NewGuid().ToString("N");
            session = new GameTrackingSession(id, hwnd, lifetime, tracker, DateTimeOffset.UtcNow);
            _sessions.Add(id, session);
            error = null;
            return true;
        }
    }

    public bool TryGet(string? id, string lifetime, out GameTrackingSession? session, out string? error)
    {
        lock (_gate)
        {
            PruneNoLock(DateTimeOffset.UtcNow);
            if (id == null || !_sessions.TryGetValue(id, out session))
            {
                session = null;
                error = "The track_id is unknown or expired.";
                return false;
            }
            if (!string.Equals(session.TargetLifetime, lifetime, StringComparison.Ordinal))
            {
                error = "The track belongs to a different target lifetime.";
                session = null;
                return false;
            }
            session.LastUsed = DateTimeOffset.UtcNow;
            error = null;
            return true;
        }
    }

    public bool IsCurrent(GameTrackingSession session)
    {
        lock (_gate)
        {
            PruneNoLock(DateTimeOffset.UtcNow);
            if (!_sessions.TryGetValue(session.Id, out var current) || !ReferenceEquals(current, session)) return false;
            current.LastUsed = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public bool Stop(string? id, string lifetime)
    {
        lock (_gate)
        {
            if (id == null || !_sessions.TryGetValue(id, out var session) || session.TargetLifetime != lifetime) return false;
            _sessions.Remove(id);
            session.Cancel();
            return true;
        }
    }

    private void PruneNoLock(DateTimeOffset now)
    {
        foreach (var id in _sessions.Where(p => now - p.Value.LastUsed > SessionIdleLimit).Select(p => p.Key).ToArray())
        {
            var session = _sessions[id];
            _sessions.Remove(id);
            session.Cancel();
        }
    }
}
