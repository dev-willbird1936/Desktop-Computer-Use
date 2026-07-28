// Copyright (c) 2026 dev-willbird1936
namespace WindowStressFixture;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var instance = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 1;
        var left = args.Length > 1 && int.TryParse(args[1], out parsed) ? parsed : 80;
        var top = args.Length > 2 && int.TryParse(args[2], out parsed) ? parsed : 120;
        var windowCount = args.Length > 3 && int.TryParse(args[3], out parsed) ? Math.Max(1, parsed) : 1;
        if (windowCount == 1)
        {
            Application.Run(new StressForm(instance, left, top));
            return;
        }

        for (int index = 1; index <= windowCount; index++)
        {
            int controlId = instance * 10 + index;
            var form = new StressForm(
                controlId,
                left + (index - 1) * 440,
                top,
                $"DCU Stress Fixture {instance}.{index}");
            form.Show();
        }
        Application.Run();
    }
}

internal sealed class StressForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private readonly TextBox _status;
    private int _count;
    private int _surfaceEventCount;

    public StressForm(int instance, int left, int top, string? title = null)
    {
        Text = title ?? $"DCU Stress Fixture {instance}";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(left, top);
        Size = new Size(420, 330);

        var heading = new Label
        {
            Text = $"DCU isolated stress fixture {instance}",
            AutoSize = true,
            Location = new Point(20, 18),
        };
        _status = new TextBox
        {
            Name = $"Status{instance}",
            AccessibleName = $"Status {instance}",
            ReadOnly = true,
            Text = "last: none",
            Location = new Point(20, 45),
            Size = new Size(360, 28),
        };
        var surface = new Panel
        {
            Name = $"StressSurface{instance}",
            AccessibleName = $"Stress surface {instance}",
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(235, 245, 255),
            Location = new Point(20, 80),
            Size = new Size(360, 150),
        };
        surface.MouseDown += (_, eventArgs) =>
        {
            _status.Text = $"event {++_surfaceEventCount}: {eventArgs.Button.ToString().ToLowerInvariant()} {eventArgs.X},{eventArgs.Y}";
        };

        var increment = new Button
        {
            Name = $"Increment{instance}",
            AccessibleName = $"Increment {instance}",
            Text = $"Increment {instance}",
            Location = new Point(20, 245),
            Size = new Size(120, 32),
        };
        increment.Click += (_, _) => _status.Text = $"count: {++_count}";

        var input = new TextBox
        {
            Name = $"Input{instance}",
            AccessibleName = $"Input {instance}",
            Location = new Point(155, 248),
            Size = new Size(225, 28),
        };

        Controls.AddRange([heading, _status, surface, increment, input]);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate;
            return parameters;
        }
    }
}
