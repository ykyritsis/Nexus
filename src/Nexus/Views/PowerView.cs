using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nexus.Providers;

namespace Nexus.Views;

/// <summary>
/// Power page, opened from the power button in the corner. Restart, Shut down and Sign out ask for
/// a second press, so a stray A can't end a session.
/// </summary>
public sealed class PowerView : UserControl, INexusView
{
    readonly App _app;
    readonly ShellWindow _shell;
    readonly Dictionary<Button, (string glyph, string title, string? confirm, Action act)> _tiles = new();
    Button? _armed;

    public string Title => "Power";

    public PowerView(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        var grid = new Grid { ColumnSpacing = 14, VerticalAlignment = VerticalAlignment.Center };
        bool xboxSupported = SystemStatus.XboxModeSupported();
        var items = new List<(string glyph, string title, string? confirm, Action act)>
        {
            ("", "Sleep", null, () => { _shell.Hide(restoreFocus: false); Power.Sleep(); }),
            ("", "Restart", "Press again to restart", Power.Restart),
            ("", "Shut down", "Press again to shut down", Power.ShutDown),
            ("", "Lock", null, () => { _shell.Hide(restoreFocus: false); Power.Lock(); }),
            ("", "Sign out", "Press again to sign out", Power.SignOut),
        };
        if (xboxSupported)
            items.Add(("", SystemStatus.XboxModeActive() ? "Leave Xbox mode" : "Xbox mode", null,
                () => { _shell.Hide(); SystemStatus.ToggleXboxMode(); }));

        for (int i = 0; i < items.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var tile = new Button { Style = (Style)Application.Current.Resources["TileStyle"], Height = 140 };
            var item = items[i];
            Tiles.Set(tile, item.glyph, item.title, null);
            tile.Click += (_, _) => Press(tile);
            tile.GotFocus += (_, _) => { if (_armed != null && _armed != tile) Disarm(); };
            _tiles[tile] = item;
            Grid.SetColumn(tile, i);
            grid.Children.Add(tile);
        }
        Content = grid;
    }

    void Press(Button tile)
    {
        var (glyph, title, confirm, act) = _tiles[tile];
        if (confirm != null && _armed != tile)
        {
            Disarm();
            _armed = tile;
            Tiles.Set(tile, glyph, title, confirm, active: true);
            return;
        }
        act();
    }

    void Disarm()
    {
        if (_armed is null) return;
        var (glyph, title, _, _) = _tiles[_armed];
        Tiles.Set(_armed, glyph, title, null);
        _armed = null;
    }

    public void OnShown() { }
    public void OnHidden() => Disarm();
}
