using System.Drawing;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ShadowUse.Automation;
using ShadowUse.Config;
using ShadowUse.Jev;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

/// <summary>Opt-in Windows smoke test for real UIA enumeration and native Jev request mapping.</summary>
public sealed class NativeUiaJevIntegrationTests
{
    [Fact]
    public async Task ExposedControlsReachFakeJevWithScreenAndWindowBounds()
    {
        if (Environment.GetEnvironmentVariable("DCU_NATIVE_UIA_JEV_SMOKE") != "1")
            throw Xunit.Sdk.SkipException.ForSkip("Set DCU_NATIVE_UIA_JEV_SMOKE=1 to open a small non-activating native UI fixture.");

        string title = $"ShadowUse UIA Jev Fixture {Guid.NewGuid():N}";
        var formReady = new TaskCompletionSource<Form>(TaskCreationOptions.RunContinuationsAsynchronously);
        Form? fixture = null;
        var thread = new Thread(() =>
        {
            try
            {
                fixture = new UiaFixtureForm(title);
                fixture.Shown += (_, _) => formReady.TrySetResult(fixture);
                Application.Run(fixture);
            }
            catch (Exception ex)
            {
                formReady.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "ShadowUse.NativeUiaJevFixture",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        string keyName = $"DCU_NATIVE_UIA_JEV_TEST_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(keyName, "fake-key");
        try
        {
            var form = await formReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var uiaThread = new UiaThread();
            using var uia = new UiaService(uiaThread);
            using var handler = new CapturingJevHandler();
            using var http = new HttpClient(handler);
            var controller = new JevController(http, new JevOptions
            {
                Mode = JevMode.Assist,
                ApiKeyEnvironmentVariable = keyName,
                TimeoutMs = 5_000,
                MaxRetries = 0,
            });
            var actionCatalog = new AppActionCatalog();
            Assert.True(actionCatalog.Import(JsonSerializer.Serialize(new
            {
                app_id = "fixture-software", app_version = "1", profile = "default", keyboard_layout = "us",
                actions = new[]
                {
                    new { action_id = "toggle_notifications", label = "Toggle notifications", key_sequence = new[] { "ctrl+n" },
                        source_kind = "user_confirmed", source_reference = "native fixture", evidence_status = "user_confirmed" },
                },
            })).Imported);
            var tools = new ComputerUseTools(
                uia,
                new BackgroundInput(uiaThread),
                new ShadowSettings { EnableDesktopCheck = false },
                controller,
                new WorkflowValueStore(),
                appActionCatalog: actionCatalog,
                screenStates: new ScreenStateStore());

            var result = await tools.JevDecide(form.Text, "Inspect the available controls");
            using var resultJson = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.True(resultJson.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("STOP", resultJson.RootElement.GetProperty("operation").GetString());
            Assert.Equal(1, handler.Calls);

            using var request = JsonDocument.Parse(handler.RequestJson!);
            var root = request.RootElement;
            var state = root.GetProperty("state");
            Assert.True(state.GetProperty("candidate_set_complete").GetBoolean());
            var candidates = state.GetProperty("candidates").EnumerateArray().ToArray();
            Assert.True(candidates.Length >= 8, $"Expected button, text field, checkbox, combo box, radio buttons, slider, tabs, and action button; received {candidates.Length} candidates.");

            string[] names = candidates.Select(candidate => candidate.GetProperty("name").GetString() ?? "").ToArray();
            foreach (string expected in new[] { "Continue", "Search", "notifications", "Language", "Small", "Medium", "Volume", "Apply", "General", "Audio" })
                Assert.Contains(names, name => name.Contains(expected, StringComparison.OrdinalIgnoreCase));

            foreach (var candidate in candidates)
            {
                var metadata = candidate.GetProperty("metadata");
                Assert.Equal("uia", metadata.GetProperty("source").GetString());
                Assert.Equal("screen_pixels", metadata.GetProperty("coordinate_space").GetString());
                Assert.Equal("uia_current_bounding_rectangle", metadata.GetProperty("coordinate_provenance").GetString());
                var screen = metadata.GetProperty("bbox");
                var screenCopy = metadata.GetProperty("screen_bbox");
                var window = metadata.GetProperty("window_bbox");
                Assert.Equal(screen.GetProperty("x").GetInt32(), screenCopy.GetProperty("x").GetInt32());
                Assert.Equal(screen.GetProperty("y").GetInt32(), screenCopy.GetProperty("y").GetInt32());
                Assert.True(screen.GetProperty("width").GetInt32() > 0);
                Assert.True(screen.GetProperty("height").GetInt32() > 0);
                Assert.True(window.GetProperty("x").GetInt32() >= 0);
                Assert.True(window.GetProperty("y").GetInt32() >= 0);
            }

            handler.SelectCatalogAction = true;
            var appActionResult = await tools.JevDecide(form.Text, "Toggle notifications", app_id: "fixture-software",
                app_version: "1", keyboard_layout: "us");
            using var appActionJson = JsonDocument.Parse(JsonSerializer.Serialize(appActionResult));
            var appActionRoot = appActionJson.RootElement;
            Assert.True(appActionRoot.GetProperty("ok").GetBoolean());
            Assert.StartsWith("APP_ACTION_", appActionRoot.GetProperty("operation").GetString());
            Assert.Equal("toggle_notifications", appActionRoot.GetProperty("app_action").GetProperty("action_id").GetString());
            Assert.False(appActionRoot.GetProperty("app_action").GetProperty("dispatch_enabled").GetBoolean());
            Assert.Equal(["ctrl+n"], appActionRoot.GetProperty("app_action").GetProperty("key_sequence").EnumerateArray()
                .Select(value => value.GetString() ?? "").ToArray());
            Assert.Equal(2, handler.Calls);
            using var appActionRequest = JsonDocument.Parse(handler.RequestJson!);
            Assert.Contains(appActionRequest.RootElement.GetProperty("state").GetProperty("key_bindings").EnumerateArray(), binding =>
            {
                string? code = binding.TryGetProperty("code", out var lowerCode) ? lowerCode.GetString()
                    : binding.TryGetProperty("Code", out var upperCode) ? upperCode.GetString() : null;
                return code == appActionRoot.GetProperty("operation").GetString();
            });
            Assert.Contains("Semantic screen id=", appActionRequest.RootElement.GetProperty("state").GetProperty("context").GetString());

            handler.SelectCatalogAction = false;
            handler.SelectUiaKey = true;
            var uiaKeyResult = await tools.JevDecide(form.Text, "Use the UIA-reported shortcut", app_id: "fixture-software",
                app_version: "1", keyboard_layout: "us");
            using var uiaKeyJson = JsonDocument.Parse(JsonSerializer.Serialize(uiaKeyResult));
            Assert.True(uiaKeyJson.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(handler.UiaKeyOptionSeen,
                "The fixture's mnemonic should be exposed as a current UI Automation access key.");
            Assert.Equal("uia_access_key", uiaKeyJson.RootElement.GetProperty("app_action").GetProperty("source_kind").GetString());
            Assert.False(uiaKeyJson.RootElement.GetProperty("app_action").GetProperty("dispatch_enabled").GetBoolean());
            Assert.Equal(3, handler.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
            if (fixture is { IsDisposed: false, IsHandleCreated: true })
                fixture.BeginInvoke((Action)fixture.Close);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The non-activating UIA fixture did not close.");
        }
    }

    private sealed class UiaFixtureForm : Form
    {
        private const int WsExNoActivate = 0x08000000;

        public UiaFixtureForm(string title)
        {
            Text = title;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(120, 120);
            ClientSize = new Size(640, 420);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            ControlBox = false;
            Controls.Add(new Button
            {
                Name = "continueButton",
                Text = "&Continue",
                AccessibleName = "Continue",
                Location = new Point(20, 20),
                Size = new Size(130, 42),
            });
            Controls.Add(new TextBox
            {
                Name = "searchField",
                AccessibleName = "Search",
                Location = new Point(20, 78),
                Size = new Size(240, 32),
            });
            Controls.Add(new CheckBox
            {
                Name = "notificationsCheckbox",
                Text = "Enable notifications",
                AccessibleName = "Enable notifications",
                Location = new Point(20, 132),
                AutoSize = true,
            });
            var language = new ComboBox
            {
                Name = "languageSelector",
                AccessibleName = "Language",
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(20, 188),
                Size = new Size(220, 28),
            };
            language.Items.AddRange(["English", "French"]);
            language.SelectedIndex = 0;
            Controls.Add(language);
            Controls.Add(new RadioButton
            {
                Name = "smallOption",
                Text = "Small",
                AccessibleName = "Small",
                Location = new Point(20, 234),
                AutoSize = true,
                Checked = true,
            });
            Controls.Add(new RadioButton
            {
                Name = "mediumOption",
                Text = "Medium",
                AccessibleName = "Medium",
                Location = new Point(20, 264),
                AutoSize = true,
            });
            Controls.Add(new TrackBar
            {
                Name = "volumeSlider",
                AccessibleName = "Volume",
                Minimum = 0,
                Maximum = 100,
                Value = 50,
                Location = new Point(280, 72),
                Size = new Size(280, 48),
            });
            var tabs = new TabControl
            {
                Name = "settingsTabs",
                AccessibleName = "Settings tabs",
                Location = new Point(280, 144),
                Size = new Size(300, 190),
            };
            tabs.TabPages.Add(new TabPage("General") { AccessibleName = "General" });
            tabs.TabPages.Add(new TabPage("Audio") { AccessibleName = "Audio" });
            Controls.Add(tabs);
            Controls.Add(new Button
            {
                Name = "applyButton",
                Text = "Apply",
                AccessibleName = "Apply",
                Location = new Point(480, 360),
                Size = new Size(100, 36),
            });
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

    private sealed class CapturingJevHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool SelectCatalogAction { get; set; }
        public bool SelectUiaKey { get; set; }
        public bool UiaKeyOptionSeen { get; private set; }
        public string? RequestJson { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var body = JsonDocument.Parse(RequestJson);
            var criteria = body.RootElement.GetProperty("questions").GetProperty("operation").GetProperty("criteria");
            var operationNames = criteria.EnumerateObject().Select(property => property.Name).ToArray();
            string? uiaKey = operationNames.FirstOrDefault(name => name.StartsWith("UIA_KEY_", StringComparison.Ordinal));
            UiaKeyOptionSeen |= uiaKey is not null;
            string choice = SelectUiaKey && uiaKey is not null ? uiaKey
                : SelectCatalogAction
                    ? operationNames.First(name => name.StartsWith("APP_ACTION_", StringComparison.Ordinal))
                    : "STOP";
            var probabilities = criteria.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Name == choice ? 1.0 : 0.0,
                StringComparer.Ordinal);
            var response = JsonSerializer.Serialize(new
            {
                model = "fake-native-uia",
                answers = new
                {
                    operation = new { choice, confidence = 1.0, probabilities },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
