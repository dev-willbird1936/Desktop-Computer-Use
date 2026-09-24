// Copyright (c) 2026 dev-willbird1936
using System.Net;
using System.Windows.Forms;

internal sealed class LoginRegressionForm : Form
{
    private const string Username = "fixture-user";
    private const string Password = "synthetic-password-42!";

    public LoginRegressionForm()
    {
        Text = "DCU owned Jev-off login fixture";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(120, 120);
        ClientSize = new Size(420, 240);

        var user = new TextBox
        {
            Name = "FixtureUsername",
            AccessibleName = "Fixture username",
            Location = new Point(20, 20),
            Width = 370,
        };
        var password = new TextBox
        {
            Name = "FixturePassword",
            AccessibleName = "Fixture password",
            UseSystemPasswordChar = true,
            Location = new Point(20, 60),
            Width = 370,
        };
        var status = new TextBox
        {
            Name = "FixtureStatus",
            AccessibleName = "Fixture status",
            ReadOnly = true,
            Text = "signed out",
            Location = new Point(20, 150),
            Width = 370,
        };
        var login = new Button
        {
            Name = "FixtureLogin",
            AccessibleName = "Fixture sign in",
            Text = "Sign in",
            Location = new Point(20, 100),
            Width = 150,
        };
        login.Click += (_, _) =>
        {
            status.Text = user.Text == Username && password.Text == Password
                ? "authenticated: fixture-user"
                : "login rejected";
        };
        // F10: a genuinely disabled control -- clicking it must be refused before dispatch,
        // never silently attempted. Deliberately never gains a Click handler: if a refusal
        // check ever regressed and let a click through, this would have no visible effect to
        // hide the bug behind, only a click that reached a live UIA Invoke on a disabled button.
        var disabledAction = new Button
        {
            Name = "FixtureDisabledAction",
            AccessibleName = "Fixture disabled action",
            Text = "Disabled action",
            Enabled = false,
            Location = new Point(20, 180),
            Width = 150,
        };
        Controls.AddRange([user, password, login, status, disabledAction]);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000;
            return parameters;
        }
    }
}

/// <summary>F05 visual-grounding fixture: a single owner-drawn Panel with no UIA-exposed
/// text or actionable children (GDI+ drawing only), so structured evidence is genuinely
/// inadequate and get_app_state must fall back to OCR. Draws a unique "Save" label and two
/// duplicate "Cancel" labels (to exercise the uniqueness confidence heuristic) plus a live
/// "Count: N" counter that increments on a real click inside the Save region. When
/// DCU_CANVAS_LOG_PATH is set, every accepted Save click is also appended to that file as an
/// out-of-band oracle the test can read without going back through OCR or UIA.</summary>
internal sealed class CanvasRegressionForm : Form
{
    private static readonly Rectangle SaveRect = new(30, 20, 120, 40);
    private static readonly Rectangle Cancel1Rect = new(190, 20, 120, 40);
    private static readonly Rectangle Cancel2Rect = new(30, 80, 120, 40);
    private readonly Panel _canvas;
    private readonly string? _logPath = Environment.GetEnvironmentVariable("DCU_CANVAS_LOG_PATH");
    private int _clickCount;

    public CanvasRegressionForm()
    {
        Text = "DCU owned visual-grounding canvas fixture";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(120, 120);
        ClientSize = new Size(420, 250);

        _canvas = new Panel
        {
            Name = "FixtureCanvas",
            AccessibleName = "Fixture canvas",
            BackColor = Color.FromArgb(230, 232, 236),
            Location = new Point(10, 10),
            Size = new Size(400, 200),
        };
        _canvas.Paint += OnPaint;
        _canvas.MouseDown += OnMouseDown;
        Controls.Add(_canvas);

        // F42-T02 fixture support: a real UIA-exposed label with no Invoke/Toggle/Select
        // pattern (SupportedOperations excludes "click") in the SAME window as the OCR-only
        // canvas above, so route_click has a genuine native target to fall back FROM. Its own
        // 30+ char name also keeps it out of the get_app_state "structured evidence adequate"
        // gate (that check requires Actions.Length > 0, which a plain label never has).
        var caption = new Label
        {
            Name = "CanvasCaption",
            AutoSize = false,
            Location = new Point(10, 215),
            Size = new Size(400, 24),
            Text = "This caption describes the canvas above but cannot itself be clicked.",
        };
        Controls.Add(caption);
    }

    private void OnPaint(object? sender, PaintEventArgs e)
    {
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", 13F, FontStyle.Bold);
        using var counterFont = new Font("Consolas", 13F, FontStyle.Bold); // slashed zero, avoids an OCR 0/O misread
        using var textBrush = new SolidBrush(Color.FromArgb(20, 20, 20));
        using var saveBrush = new SolidBrush(Color.FromArgb(205, 222, 245));
        using var cancelBrush = new SolidBrush(Color.FromArgb(245, 218, 205));
        using var borderPen = new Pen(Color.FromArgb(110, 120, 140), 2);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        e.Graphics.Clear(_canvas.BackColor);

        e.Graphics.FillRectangle(saveBrush, SaveRect);
        e.Graphics.DrawRectangle(borderPen, SaveRect);
        e.Graphics.DrawString("Save", font, textBrush, SaveRect, format);

        e.Graphics.FillRectangle(cancelBrush, Cancel1Rect);
        e.Graphics.DrawRectangle(borderPen, Cancel1Rect);
        e.Graphics.DrawString("Cancel", font, textBrush, Cancel1Rect, format);

        e.Graphics.FillRectangle(cancelBrush, Cancel2Rect);
        e.Graphics.DrawRectangle(borderPen, Cancel2Rect);
        e.Graphics.DrawString("Cancel", font, textBrush, Cancel2Rect, format);

        e.Graphics.DrawString($"Count: {_clickCount}", counterFont, textBrush, new Point(30, 145));
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (!SaveRect.Contains(e.Location)) return;
        _clickCount++;
        _canvas.Invalidate();
        _canvas.Update();
        if (_logPath != null)
        {
            try { File.AppendAllText(_logPath, $"{DateTimeOffset.UtcNow:O} save-click count={_clickCount}{Environment.NewLine}"); }
            catch { /* best-effort oracle log; never break the fixture UI over it */ }
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000;
            return parameters;
        }
    }
}

internal sealed class AmbiguityRegressionForm : Form
{
    private const int DuplicateCount = 2;
    private readonly List<Button> _retired = [];
    private readonly TextBox _command;
    private readonly TextBox _status = new()
    {
        Name = "FixtureStatus",
        AccessibleName = "Fixture status",
        ReadOnly = true,
        Text = "untouched",
        Location = new Point(20, 190),
        Width = 440,
    };
    private Button[] _buttons = [];
    private bool _rebuilt;

    public AmbiguityRegressionForm()
    {
        Text = "DCU owned locator ambiguity fixture";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(120, 120);
        ClientSize = new Size(500, 235);

        _command = new TextBox
        {
            Name = "FixtureCommand",
            AccessibleName = "Fixture command",
            Location = new Point(20, 20),
            Width = 440,
        };
        _command.TextChanged += (_, _) =>
        {
            if (_rebuilt || !_command.Text.Equals("rebuild", StringComparison.Ordinal))
                return;

            _rebuilt = true;
            foreach (var button in _buttons)
            {
                Controls.Remove(button);
                button.Hide();
                _retired.Add(button);
            }
            AddButtons();
            Controls.Add(new Label
            {
                Name = "RebuildComplete",
                AccessibleName = "Rebuild complete",
                Text = "Rebuild complete",
                AutoSize = true,
                Location = new Point(210, 145),
            });
            Controls.Add(new Label
            {
                Name = "RebuildReplacementCount",
                AccessibleName = $"{DuplicateCount} duplicate replacements",
                Text = $"{DuplicateCount} duplicate replacements",
                AutoSize = true,
                Location = new Point(210, 165),
            });
        };
        Controls.AddRange([_command, _status]);
        AddButtons();
    }

    private void AddButtons()
    {
        _buttons = Enumerable.Range(1, DuplicateCount).Select(row =>
        {
            var button = new Button
            {
                Name = "DuplicateAction",
                AccessibleName = "Duplicate action",
                Text = "Act",
                Location = new Point(20, 35 + row * 40),
                Width = 150,
            };
            button.Click += (_, _) => _status.Text = $"row {row} acted";
            return button;
        }).ToArray();
        Controls.AddRange(_buttons);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var button in _retired)
                button.Dispose();
        base.Dispose(disposing);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000;
            return parameters;
        }
    }
}

/// <summary>F13 fixture: a native UIA-observable ListBox reachable through TWO routes -- an
/// ordinary GUI button click, and a loopback-only HTTP "documented API" (DCU_API_MIXING_PORT env
/// var picks the port; a fixed default is used if unset) that mutates the SAME underlying list
/// directly. Proves an API-created item is visible via UIA after a fresh snapshot with the same
/// verified postcondition as the GUI route (F13-T01), and gives a caller a real target for an
/// intentionally-unavailable-API fallback-to-GUI proof (F13-T02).</summary>
internal sealed class ApiMixingRegressionForm : Form
{
    private readonly ListBox _items = new()
    {
        Name = "FixtureItems",
        AccessibleName = "Fixture items",
        Location = new Point(20, 20),
        Size = new Size(360, 100),
    };
    private readonly HttpListener? _listener;
    private readonly Thread? _listenerThread;
    private readonly int _port;

    public ApiMixingRegressionForm()
    {
        Text = "DCU owned API/GUI mixing fixture";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(120, 120);
        ClientSize = new Size(420, 220);

        _items.Items.Add("seed-item");

        var input = new TextBox
        {
            Name = "FixtureNewItemName",
            AccessibleName = "Fixture new item name",
            Location = new Point(20, 130),
            Width = 250,
        };
        var addButton = new Button
        {
            Name = "FixtureAddViaGui",
            AccessibleName = "Fixture add via gui",
            Text = "Add via GUI",
            Location = new Point(280, 128),
            Width = 100,
        };
        addButton.Click += (_, _) =>
        {
            if (input.Text.Length > 0)
                _items.Items.Add(input.Text);
        };
        Controls.AddRange([_items, input, addButton]);

        _port = int.TryParse(Environment.GetEnvironmentVariable("DCU_API_MIXING_PORT"), out var configured) ? configured : 58734;
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _listenerThread = new Thread(ListenLoop) { IsBackground = true };
            _listenerThread.Start();
        }
        catch (HttpListenerException)
        {
            // Port already bound (e.g. a stale prior instance) -- the fixture still runs; the
            // caller's API route will fail to connect, which is itself a valid F13-T02 scenario.
            _listener = null;
        }
    }

    private void ListenLoop()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            var name = ctx.Request.QueryString["name"] ?? "";
            var added = false;
            if (name.Length > 0 && IsHandleCreated)
            {
                try
                {
                    Invoke(() => { _items.Items.Add(name); added = true; });
                }
                catch (ObjectDisposedException) { /* form closing mid-request */ }
                catch (InvalidOperationException) { /* handle destroyed mid-request */ }
            }
            var body = System.Text.Encoding.UTF8.GetBytes(added ? "ok" : "rejected");
            ctx.Response.StatusCode = added ? 200 : 400;
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.OutputStream.Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _listener?.Stop();
            _listener?.Close();
        }
        base.Dispose(disposing);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000;
            return parameters;
        }
    }
}
