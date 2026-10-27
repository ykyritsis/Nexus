using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.Services;
using Windows.Gaming.Input;

namespace Nexus.Views;

/// <summary>
/// Controllers page (from the controller chip in the corner): each Xbox controller in its player
/// slot with battery and a rumble test, plus any other controller Windows sees (DualSense and so on)
/// with its battery where it reports one.
/// </summary>
public sealed class ControllersView : UserControl, INexusView
{
    readonly App _app;
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherQueueTimer _poll;
    string _key = "";

    public string Title => "Controllers";
    public string? YHint => "Test rumble";

    static ResourceDictionary Res => Application.Current.Resources;

    public ControllersView(App app)
    {
        _app = app;
        var open = Pill("", "Xbox Accessories", () => { _app.Shell.Hide(restoreFocus: false); Launch.Open(@"shell:AppsFolder\Microsoft.XboxDevices_8wekyb3d8bbwe!App"); });
        var bt = Pill("", "Pair a controller", () => { _app.Shell.Hide(restoreFocus: false); Launch.Open("ms-settings:bluetooth"); });
        _bridgeButton = Pill("\uE72E", "", BridgeClick);
        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _bridgeButton, bt, open } };
        var bridgeInfo = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, Spacing = 1,
            Children =
            {
                new TextBlock { Text = "Lock game input while the guide is open", Style = (Style)Res["BodyStrongTextBlockStyle"], FontSize = 13 },
                _bridgeStatus,
            },
        };
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pills, 1);
        footer.Children.Add(bridgeInfo);
        footer.Children.Add(pills);
        var root = new Grid { RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroller = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(4), Content = _row,
        };
        Grid.SetRow(footer, 1);
        root.Children.Add(scroller);
        root.Children.Add(footer);
        Content = root;

        _poll = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(2);
        _poll.Tick += (_, _) => Refresh();
    }

    public void OnShown()
    {
        _key = "";
        Refresh();
        _poll.Start();
        _app.InputBridge.StatusChanged += UpdateBridge;
        UpdateBridge();
    }

    public void OnHidden()
    {
        _poll.Stop();
        _app.InputBridge.StatusChanged -= UpdateBridge;
    }

    // ---------------- input lock (InputBridge) ----------------

    readonly TextBlock _bridgeStatus = new() { Style = (Style)Res["CaptionTextBlockStyle"], Foreground = (Brush)Res["SubtleTextBrush"], TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button _bridgeButton;

    void UpdateBridge()
    {
        var bridge = _app.InputBridge;
        _bridgeStatus.Text = bridge.Status;
        string label = !InputBridge.DriversInstalled ? "Get drivers"
            : !bridge.Ready ? "Set up"
            : _app.Settings.LockGameInput ? "On" : "Off";
        ((TextBlock)((StackPanel)_bridgeButton.Content).Children[1]).Text = label;
    }

    async void BridgeClick()
    {
        var bridge = _app.InputBridge;
        if (!InputBridge.DriversInstalled)
        {
            // Both are free, signed drivers by Nefarius; installing needs an admin prompt, so it's the user's step.
            _app.Shell.Hide(restoreFocus: false);
            Launch.Open(InputBridge.HidHideUrl);
            Launch.Open(InputBridge.ViGEmBusUrl);
        }
        else if (!bridge.Ready)
        {
            _app.Shell.Hide(restoreFocus: false); // the admin prompt needs the screen
            await bridge.SetUpAsync();
        }
        else await bridge.SetEnabledAsync(!_app.Settings.LockGameInput);
        UpdateBridge();
    }

    sealed record Pad(string Key, string Name, string Glyph, string Battery, int Slot);

    void Refresh()
    {
        var pads = new List<Pad>();
        for (int slot = 0; slot < 4; slot++)
        {
            if (!_app.Gamepad.IsConnected(slot)) continue;
            var b = _app.Gamepad.Battery(slot);
            string battery = b is null ? "Battery unknown" : b.Value.wired ? "Wired" : b.Value.level switch { 0 => "Battery empty", 1 => "Battery low", 2 => "Battery medium", _ => "Battery full" };
            pads.Add(new Pad($"x{slot}:{battery}", $"Xbox controller", "", battery, slot));
        }
        // Everything else Windows knows about (XInput pads appear here too; skip those).
        try
        {
            foreach (var c in RawGameController.RawGameControllers)
            {
                var name = c.DisplayName;
                if (string.IsNullOrWhiteSpace(name) || name.Contains("Xbox", StringComparison.OrdinalIgnoreCase)) continue;
                string battery = "Battery unknown";
                var report = c.TryGetBatteryReport();
                if (report?.RemainingCapacityInMilliwattHours is int rem && report.FullChargeCapacityInMilliwattHours is int full && full > 0)
                    battery = $"Battery {rem * 100 / full}%";
                else if (!c.IsWireless) battery = "Wired";
                pads.Add(new Pad($"r{c.NonRoamableId}:{battery}", name, "", battery, -1));
            }
        }
        catch (Exception ex) { Log.Error("listing controllers", ex); }

        var key = string.Join("|", pads.Select(p => p.Key));
        if (key == _key) return;
        _key = key;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as UIElement;
        int at = focused is null ? -1 : _row.Children.IndexOf(focused);
        _row.Children.Clear();
        if (pads.Count == 0)
        {
            if (at >= 0) _app.Shell.FocusContent();
            _row.Children.Add(new TextBlock { Text = "No controllers connected.", Style = (Style)Res["BodyTextBlockStyle"], Foreground = (Brush)Res["SubtleTextBrush"] });
            return;
        }
        foreach (var p in pads) _row.Children.Add(Card(p));
        if (at >= 0 && _row.Children[Math.Min(at, _row.Children.Count - 1)] is Control again) again.Focus(FocusState.Keyboard);
    }

    Button Card(Pad p)
    {
        var badge = new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(24), HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)Res["IconCircleBrush"], Child = new FontIcon { Glyph = p.Glyph, FontSize = 22 },
        };
        var player = new TextBlock
        {
            Text = p.Slot >= 0 ? $"Player {p.Slot + 1}" : "Controller", Style = (Style)Res["CaptionTextBlockStyle"],
            Foreground = (Brush)Res["SubtleTextBrush"], HorizontalAlignment = HorizontalAlignment.Center,
        };
        var name = new TextBlock { Text = p.Name, Style = (Style)Res["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 130, FontSize = 13 };
        var battery = new TextBlock { Text = p.Battery, Style = (Style)Res["CaptionTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center };
        var card = new Button
        {
            Style = (Style)Res["TileStyle"], Width = 148, Height = 156, CornerRadius = new CornerRadius(24), Padding = new Thickness(10),
            Content = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { badge, player, name, battery } },
            Tag = p.Slot,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, $"{p.Name}, {p.Battery}");
        // A or click: rumble it, so you can tell which controller is which.
        card.Click += (_, _) => { if (p.Slot >= 0) _ = _app.Gamepad.RumbleAsync(p.Slot); };
        return card;
    }

    static Button Pill(string glyph, string text, Action click)
    {
        var b = new Button
        {
            Style = (Style)Res["GlassButtonStyle"], MinHeight = 36, Padding = new Thickness(12, 0, 14, 0),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = glyph, FontSize = 14 }, new TextBlock { Text = text, FontSize = 13 } } },
        };
        b.Click += (_, _) => click();
        return b;
    }

    public bool HandleY(DependencyObject? focused)
    {
        if (focused is Button { Tag: int slot } && slot >= 0) { _ = _app.Gamepad.RumbleAsync(slot); return true; }
        return false;
    }
}
