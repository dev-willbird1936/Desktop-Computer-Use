// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Config;

namespace ShadowUse.Automation;

/// <summary>Tunables for input_mode=humanized. Speeds multiply pace; TypoRate is the
/// per-letter chance of a self-corrected mistype.</summary>
public sealed record HumanizedOptions(double MouseSpeed = 1, double TypingSpeed = 1, double TypoRate = 0.015)
{
    public static HumanizedOptions From(ShadowSettings s) => new(s.HumanizedMouseSpeed, s.HumanizedTypingSpeed, s.HumanizedTypoRate);
}

public readonly record struct PathPoint(int X, int Y, int TMs);

/// <summary>Cursor path: sampled screen points with millisecond offsets, first point is the start.</summary>
public sealed record MousePlan(IReadOnlyList<PathPoint> Points, int DurationMs);

/// <summary>One key of a typing plan. Glyph "\b" is Backspace; any other single Unicode scalar is typed as itself.</summary>
public readonly record struct TypeStep(string Glyph, int HoldMs, int DelayAfterMs);

/// <summary>
/// Pure planners for human-seeming input (no Win32, seedable RNG). Cursor paths follow Fitts-law
/// durations along arc-length-parameterised Bézier sub-movements with overshoot and settling;
/// typing uses per-key hold/interval jitter, digraph effects, and self-correcting typos.
/// InteractiveInput plays the plans back through SendInput.
/// </summary>
public static class HumanPlanner
{
    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    private static double Uniform(double lo, double hi, Random rng) => lo + (hi - lo) * rng.NextDouble();

    // Fitts coefficients vary per person and per attempt — humans aren't servos.
    public static double FittsMs(double distance, double targetWidth, double speed, Random rng)
    {
        // Per-call jitter: same person varies ±15% each time they move.
        // Re-sample every call so the same action varies across repeats.
        double a = Uniform(200, 360, rng);
        double b = Uniform(120, 250, rng);
        return (a + b * Math.Log2(1 + distance / Math.Max(8, targetWidth))) / Math.Max(0.2, speed);
    }

    public static MousePlan PlanPath((int X, int Y) from, (int X, int Y) to, double targetWidth, double speed, Random rng, bool fast = false)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double dist = Hypot(dx, dy);
        var points = new List<PathPoint> { new(from.X, from.Y, 0) };
        if (dist < 2) return new MousePlan(points, 0);

        double ux = dx / dist, uy = dy / dist, px = -uy, py = ux;
        double duration = fast
            ? Uniform(65, 130, rng) * (1 + Math.Min(dist, 1600) / 1600 * 0.7) / Math.Max(0.2, speed)
            : FittsMs(dist, targetWidth, speed, rng) * Uniform(0.7, 1.4, rng);
        bool overshoot = !fast && rng.NextDouble() < 0.25;
        double overshootPx = overshoot ? dist * Uniform(0.03, 0.12, rng) : 0;

        int segments = fast ? 1 : 2 + rng.Next(3);
        var weights = Enumerable.Range(0, segments).Select(_ => 0.5 + rng.NextDouble()).ToArray();
        double weightSum = weights.Sum(), running = 0;
        var fractions = weights.Select(w => running += w / weightSum).ToArray();

        double now = 0;
        (double X, double Y) cursor = (from.X, from.Y);

        void RunSegment((double X, double Y) goal, double segMs)
        {
            double segDist = Hypot(goal.X - cursor.X, goal.Y - cursor.Y);
            if (segDist < 0.5) return;
            double offset = segDist * (fast ? Uniform(0.02, 0.1, rng) : Uniform(0.03, 0.35, rng)) * (rng.Next(2) == 0 ? 1 : -1);
            (double X, double Y) c1 = (cursor.X + px * offset * 0.5 + ux * segDist * Uniform(0.15, 0.35, rng), cursor.Y + py * offset * 0.5 + uy * segDist * Uniform(0.15, 0.35, rng));
            (double X, double Y) c2 = (goal.X + px * offset * 0.5 - ux * segDist * Uniform(0.15, 0.35, rng), goal.Y + py * offset * 0.5 - uy * segDist * Uniform(0.15, 0.35, rng));
            SampleBezier(points, cursor, c1, c2, goal, segMs, now, rng);
            now += segMs;
            cursor = goal;
        }

        for (int i = 0; i < segments; i++)
        {
            double f = fractions[i];
            double gx = from.X + dx * f, gy = from.Y + dy * f;
            if (i == segments - 1) { gx += ux * overshootPx; gy += uy * overshootPx; }
            RunSegment((gx, gy), duration * (f - (i == 0 ? 0 : fractions[i - 1])) * Uniform(0.7, 1.3, rng));
            if (i < segments - 1) now += Uniform(10, 120, rng);
            // 3-8% chance of a mid-path micro-pause (distraction, hesitation)
            if (i > 0 && i < segments - 1 && rng.NextDouble() < 0.05)
                now += Uniform(80, 280, rng);
        }
        if (overshoot) RunSegment((to.X, to.Y), Uniform(70, 350, rng));

        // 5% chance of a longer hesitation before settling (mistarget, second thought)
        if (!fast && rng.NextDouble() < 0.05) now += Uniform(120, 400, rng);

        int corrections = fast ? 0 : rng.Next(4);
        for (int i = 0; i < corrections; i++)
        {
            now += Uniform(8, 70, rng);
            points.Add(new((int)Math.Round(to.X + Uniform(-8, 8, rng)), (int)Math.Round(to.Y + Uniform(-8, 8, rng)), (int)Math.Round(now)));
        }
        if (points[^1].X != to.X || points[^1].Y != to.Y)
        {
            now += fast ? Uniform(2, 8, rng) : Uniform(6, 60, rng);
            points.Add(new(to.X, to.Y, (int)Math.Round(now)));
        }
        return new MousePlan(points, (int)Math.Round(now));
    }

    // Samples the curve by arc length so speed follows an asymmetric bell (peak near a third of
    // the way in): monotonic, teleport-free, and exactly on the goal at the end.
    private static void SampleBezier(List<PathPoint> output, (double X, double Y) p0, (double X, double Y) p1,
        (double X, double Y) p2, (double X, double Y) p3, double durMs, double t0, Random rng)
    {
        const int steps = 160, velocitySteps = 128;
        var pts = new (double X, double Y)[steps + 1];
        var cum = new double[steps + 1];
        pts[0] = p0;
        double total = 0;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps, u = 1 - t;
            double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
            pts[i] = (a * p0.X + b * p1.X + c * p2.X + d * p3.X, a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y);
            total += Hypot(pts[i].X - pts[i - 1].X, pts[i].Y - pts[i - 1].Y);
            cum[i] = total;
        }
        if (total < 0.5) return;
        for (int i = 0; i <= steps; i++) cum[i] /= total;

        // Velocity profile varies per movement, reflecting different muscle-group recruitment
        double rise = 0.8 + rng.NextDouble() * 1.0, fall = 1.8 + rng.NextDouble() * 1.4;
        var velocityCdf = new double[velocitySteps + 1];
        double velocityTotal = 0;
        for (int i = 0; i <= velocitySteps; i++)
        {
            double x = (double)i / velocitySteps;
            velocityTotal += Math.Pow(x, rise) * Math.Pow(1 - x, fall);
            velocityCdf[i] = velocityTotal;
        }
        for (int i = 0; i <= velocitySteps; i++) velocityCdf[i] /= velocityTotal;

        double ArcFractionAt(double tau)
        {
            double x = Math.Clamp(tau, 0, 1) * velocitySteps;
            int i = Math.Min((int)Math.Floor(x), velocitySteps - 1);
            return velocityCdf[i] + (velocityCdf[i + 1] - velocityCdf[i]) * (x - i);
        }

        (double X, double Y) PointAtArc(double s)
        {
            int lo = 0, hi = steps;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (cum[mid] < s) lo = mid + 1; else hi = mid;
            }
            int i = Math.Max(0, lo - 1);
            double span = cum[lo] - cum[i];
            double f = span <= 0 ? 0 : Math.Clamp((s - cum[i]) / span, 0, 1);
            return (pts[i].X + (pts[lo].X - pts[i].X) * f, pts[i].Y + (pts[lo].Y - pts[i].Y) * f);
        }

        // 2-5 ms between samples, like a 200-500 Hz gaming mouse
        for (double tau = Uniform(2, 5, rng) / durMs; tau < 1; tau += Uniform(2, 5, rng) / durMs)
        {
            var pt = PointAtArc(ArcFractionAt(tau));
            output.Add(new((int)Math.Round(pt.X), (int)Math.Round(pt.Y), (int)Math.Round(t0 + tau * durMs)));
        }
    }

    /// <summary>Picks a click point inside the box's inner region, biased toward the side the cursor approaches from.</summary>
    public static (int X, int Y) PickPoint(int left, int top, int width, int height, (int X, int Y) cursor, Random rng)
    {
        double padX = Math.Min(width * 0.18, Math.Max(3, width * 0.12));
        double padY = Math.Min(height * 0.18, Math.Max(3, height * 0.12));
        double x0 = left + padX, x1 = left + width - padX, y0 = top + padY, y1 = top + height - padY;
        return ((int)Math.Round(BiasedUnit(cursor.X, Math.Min(x0, x1), Math.Max(x0, x1), rng)),
                (int)Math.Round(BiasedUnit(cursor.Y, Math.Min(y0, y1), Math.Max(y0, y1), rng)));
    }

    private static double BiasedUnit(double from, double lo, double hi, Random rng)
    {
        double span = Math.Max(1, hi - lo), t = rng.NextDouble();
        if (from < lo) t *= t;
        else if (from > hi) t = 1 - (1 - t) * (1 - t);
        else t = Math.Clamp((from - lo) / span * 0.55 + t * 0.45, 0, 1);
        return lo + t * span;
    }

    // ---------- typing ----------

    private const string LeftHand = "qwertasdfgzxcvb12345`";
    private const string RightHand = "yuiophjklnm67890-=[]\\;',./";
    private const string ShiftedSymbols = "~!@#$%^&*()_+{}|:\"<>?";
    private static readonly Dictionary<char, string> NeighbourMap = (
        "q:wa w:qeas e:wrsd r:etdf t:ryfg y:tugh u:yihj i:uojk o:ipkl p:ol a:qszw s:awedx d:serfc f:drtgv g:ftyhb " +
        "h:gyujn j:huikm k:jiol l:kop z:asx x:zsdc c:xdfv v:cfgb b:vghn n:bhjm m:njk")
        .Split(' ').ToDictionary(e => e[0], e => e[2..]);

    private static double Gaussian(Random rng)
    {
        double u, v;
        do u = rng.NextDouble(); while (u == 0);
        do v = rng.NextDouble(); while (v == 0);
        return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
    }

    private static bool SameHand(char a, char b)
    {
        a = char.ToLowerInvariant(a); b = char.ToLowerInvariant(b);
        return (LeftHand.Contains(a) && LeftHand.Contains(b)) || (RightHand.Contains(a) && RightHand.Contains(b));
    }

    private static bool NeedsShift(char c) => char.IsUpper(c) || ShiftedSymbols.Contains(c);

    /// <summary>Plans keystrokes for <paramref name="text"/>. Replaying every step (Backspace removes the last
    /// character) always reproduces the text; typos are corrected before moving on.</summary>
    public static IReadOnlyList<TypeStep> PlanTyping(string text, double speed, double typoRate, Random rng, bool fast = false)
    {
        speed = Math.Clamp(speed, 0.25, 4);
        if (fast) typoRate = 0;
        var steps = new List<TypeStep>();
        // Per-session profile: some people type 85 wpm, others 130. Chosen once per call
        // so all keystrokes in this burst come from the same "person".
        double baseWpm = Uniform(70, 130, rng);
        double sessionWpm = baseWpm * Uniform(0.92, 1.08, rng);
        // Per-person hand bias: some favour left hand, some right — affects digraph timing
        double handBias = Uniform(0.85, 1.15, rng);
        int burstLeft = 0;
        string previous = "";
        bool previousShift = false;
        bool atWordStart = true;

        void Push(string glyph, int holdOverride = -1, int extraDelay = 0)
        {
            char c = glyph[0];
            bool single = glyph.Length == 1, alnum = single && char.IsAsciiLetterOrDigit(c);
            if (alnum) { if (burstLeft <= 0) burstLeft = 4 + rng.Next(8); burstLeft--; } else burstLeft = 0;
            double wpm = sessionWpm * (alnum ? Uniform(0.96, 1.12, rng) : Uniform(0.62, 0.82, rng));
            double charMs = fast ? 60_000 / (Math.Clamp(wpm * 2.4, 150, 260) * 5) : 60_000 / (Math.Clamp(wpm, 65, 110) * 5);
            double hold = fast ? Uniform(28, 55, rng) : alnum ? Uniform(62, 105, rng) : Uniform(68, 115, rng);
            double delay = charMs - hold + Uniform(-8, 8, rng);
            bool shift = single && NeedsShift(c);
            if (previous.Length == 1 && single)
            {
                if (previous[0] == c) delay += Uniform(6, 20, rng);
                else if (SameHand(previous[0], c)) delay += Uniform(3, 14, rng) * (LeftHand.Contains(c) ? handBias : 2 - handBias);
                if (shift != previousShift) delay += Uniform(6, 20, rng);
            }
            if (c == ' ') delay += Uniform(6, 22, rng);
            // 1% chance of a micro-hesitation mid-word (lost train of thought)
            if (!fast && alnum && rng.NextDouble() < 0.01) delay += Uniform(60, 200, rng);
            if (holdOverride >= 0) hold = holdOverride;
            hold = fast ? Math.Clamp(hold, 25, 60) : Math.Clamp(hold, 40, 120);
            delay = fast ? Math.Clamp(delay, 6, 60) + extraDelay : Math.Clamp(delay, 32, 150) + extraDelay;
            steps.Add(new(glyph, (int)Math.Round(Math.Max(fast ? 22 : 45, hold / speed)), (int)Math.Round(Math.Max(fast ? 5 : 12, delay / speed))));
            previous = glyph; previousShift = shift;
        }

        foreach (var rune in text.Replace("\r\n", "\n").EnumerateRunes())
        {
            string glyph = rune.ToString();
            char c = glyph[0];
            if (typoRate > 0 && !atWordStart && glyph.Length == 1 && char.IsAsciiLetter(c) && rng.NextDouble() < typoRate
                && NeighbourMap.TryGetValue(char.ToLowerInvariant(c), out var near))
            {
                char wrong = near[rng.Next(near.Length)];
                Push((char.IsUpper(c) ? char.ToUpperInvariant(wrong) : wrong).ToString(), extraDelay: (int)Uniform(70, 160, rng));
                Push("\b", holdOverride: (int)Uniform(55, 90, rng), extraDelay: (int)Uniform(50, 120, rng));
                previous = "";
            }
            Push(glyph);
            bool wordBreak = char.IsWhiteSpace(c);
            atWordStart = wordBreak;
            if (wordBreak && !fast)
            {
                double pause = rng.NextDouble();
                if (pause < 0.008) // 0.8% chance: sentence-ending pause (220-700ms)
                    steps[^1] = steps[^1] with { DelayAfterMs = steps[^1].DelayAfterMs + (int)Uniform(220, 700, rng) };
                else if (pause < 0.035) // 2.7% chance: normal word-break pause (90-180ms)
                    steps[^1] = steps[^1] with { DelayAfterMs = steps[^1].DelayAfterMs + (int)Uniform(90, 180, rng) };
            }
        }
        return steps;
    }

    public static string Replay(IEnumerable<TypeStep> steps)
    {
        var text = new System.Text.StringBuilder();
        foreach (var step in steps)
        {
            if (step.Glyph == "\b") { if (text.Length > 0) text.Length--; }
            else text.Append(step.Glyph);
        }
        return text.ToString();
    }

    // ---------- keys, wheel, clicks ----------

    /// <summary>Delay after each wheel notch: brisk with jitter, occasional pauses, slowing near the end.</summary>
    public static IReadOnlyList<int> PlanWheelDelays(int notches, Random rng, bool fast = false)
    {
        var delays = new int[notches];
        for (int i = 0; i < notches; i++)
        {
            if (fast) { delays[i] = (int)Math.Round(Uniform(4, 18, rng)); continue; }
            double progress = notches <= 1 ? 1 : (double)i / (notches - 1);
            double d = Uniform(18, 80, rng) * (1 + Math.Max(0, progress - 0.65) * 4);
            if (rng.NextDouble() < 0.15) d += Uniform(100, 400, rng);
            delays[i] = (int)Math.Round(d);
        }
        return delays;
    }

    public static int Between(int lo, int hi, Random rng) => (int)Math.Round(Uniform(lo, hi, rng));

    /// <summary>Key hold time: log-normal-ish around 90 ms, never below 55 ms (at least a frame at 30 fps, two at 60).</summary>
    public static int KeyHoldMs(Random rng, bool fast = false)
        => fast ? (int)Uniform(28, 72, rng) : (int)Math.Clamp(Math.Exp(Math.Log(90) + 0.35 * Gaussian(rng)), 45, 220);

    /// <summary>Hand tremor while the cursor rests: rare 1-2 px nudges that self-correct.
    /// Tremor frequency and amplitude vary per person per session.</summary>
    public static IReadOnlyList<(int TMs, int Dx, int Dy)> PlanTremor(int durationMs, Random rng)
    {
        var nudges = new List<(int, int, int)>();
        int ox = 0, oy = 0;
        // Per-session tremor aggressiveness
        double freq = Uniform(30, 250, rng);
        double prob = Uniform(0.25, 0.65, rng);
        for (double t = Uniform(40, 200, rng); t < durationMs - 40; t += freq)
        {
            if (rng.NextDouble() >= prob) continue;
            int dx = 0, dy = 0, sign = rng.Next(2) == 0 ? 1 : -1;
            if (rng.Next(2) == 0) dx = ox != 0 ? -ox : sign * (rng.Next(2) == 0 ? 1 : 2);
            else dy = oy != 0 ? -oy : sign * (rng.Next(2) == 0 ? 1 : 2);
            ox += dx; oy += dy;
            nudges.Add(((int)t, dx, dy));
        }
        if (ox != 0 || oy != 0) nudges.Add((Math.Max(0, durationMs - 15), -ox, -oy));
        return nudges;
    }
}
