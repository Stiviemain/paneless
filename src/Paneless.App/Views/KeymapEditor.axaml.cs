using Paneless.App.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Paneless.Core;

namespace Paneless.App.Views;

/// <summary>Place keys on a screenshot of the device. Tap keys: a finger goes down while the key is held.
/// Joystick: a finger is dragged from the centre in the direction of the pressed movement keys.</summary>
public partial class KeymapEditor : Window
{
    enum Placing { None, Tap, Joystick }

    static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#664f8cff"));
    static readonly IBrush FillSelected = new SolidColorBrush(Color.Parse("#88e8b75a"));
    static readonly IBrush Edge = new SolidColorBrush(Color.Parse("#eceef2"));
    static readonly IBrush EdgeSelected = new SolidColorBrush(Color.Parse("#e8b75a"));

    readonly AppServices? _s;
    readonly string _serial = "";
    readonly DeviceRow? _row;
    readonly Action? _changed;
    KeymapProfile? _profile;
    KeyMapping? _selected;
    Placing _placing;
    bool _dragging;
    bool _loading;
    double _w = 1080, _h = 2400;

    public KeymapEditor() => InitializeComponent();

    public KeymapEditor(AppServices services, string serial, DeviceRow row, Action? changed) : this()
    {
        _s = services; _serial = serial; _row = row; _changed = changed;
        TitleText.Text = Loc.F("Key mapping: {0}", row.Name);
        UseBox.Content = Loc.F("Use this profile for {0}", row.Name);
        JoyKeysBox.ItemsSource = new[] { Loc.T("W A S D"), Loc.T("Arrow keys") };

        NewButton.Click += (_, _) => NewProfile();
        DeleteButton.Click += async (_, _) => await DeleteProfileAsync();
        AddTapButton.Click += (_, _) => StartPlacing(Placing.Tap);
        AddJoyButton.Click += (_, _) => StartPlacing(Placing.Joystick);
        RemoveButton.Click += (_, _) => RemoveSelected();
        ShotButton.Click += async (_, _) => await LoadScreenshotAsync();
        ProfileBox.SelectionChanged += (_, _) => { if (!_loading) SelectProfile(ProfileBox.SelectedItem as ProfileItem); };
        NameBox.TextChanged += (_, _) =>
        {
            if (_loading || _profile == null) return;
            _profile.Name = NameBox.Text?.Trim() is { Length: > 0 } n ? n : Loc.T("Profile");
            Save(refreshList: true);
        };
        UseBox.IsCheckedChanged += (_, _) =>
        {
            if (_loading || _profile == null || _row?.Sn == null) return;
            if (UseBox.IsChecked == true) _s!.Keymaps.Data.ActiveProfile[_row.Sn] = _profile.Id;
            else _s!.Keymaps.Data.ActiveProfile.Remove(_row.Sn);
            Save();
        };
        JoyKeysBox.SelectionChanged += (_, _) =>
        {
            if (_loading || _selected is not { Kind: MappingKind.Joystick } j) return;
            (j.Up, j.Left, j.Down, j.Right) = JoyKeysBox.SelectedIndex == 1 ? ("Up", "Left", "Down", "Right") : ("W", "A", "S", "D");
            Save(); Redraw();
        };
        RadiusSlider.ValueChanged += (_, _) =>
        {
            if (_loading || _selected is not { Kind: MappingKind.Joystick } j) return;
            j.Radius = RadiusSlider.Value; Save(); Redraw();
        };

        Overlay.PointerPressed += OnOverlayPressed;
        Overlay.PointerMoved += OnOverlayMoved;
        Overlay.PointerReleased += (_, _) => { if (_dragging) { _dragging = false; Save(); } };
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);

        Opened += async (_, _) =>
        {
            if (services.Demo) return;
            RefreshProfiles();
            await LoadScreenshotAsync();
        };
        UpdateHint();
    }

    sealed record ProfileItem(KeymapProfile Profile) { public override string ToString() => Profile.Name; }

    /// <summary>Testing aid: a sample landscape layout on a blank screen; nothing is saved.</summary>
    public void ShowDemo()
    {
        _w = 2400; _h = 1080;
        Surface.Width = _w; Surface.Height = _h;
        Surface.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#2d3b52"), 0), new GradientStop(Color.Parse("#1b1c21"), 1) },
        };
        LoadingText.IsVisible = false;
        var p = new KeymapProfile { Name = Loc.T("Shooter"), Landscape = true };
        p.Mappings.Add(new KeyMapping { Kind = MappingKind.Joystick, X = 0.16, Y = 0.7, Radius = 0.13 });
        foreach (var (key, x, y) in new[] { ("Space", 0.9, 0.62), ("F", 0.7, 0.4), ("R", 0.79, 0.85), ("LeftShift", 0.83, 0.78), ("1", 0.45, 0.88), ("2", 0.55, 0.88), ("M", 0.93, 0.12) })
            p.Mappings.Add(new KeyMapping { Key = key, X = x, Y = y });
        _loading = true;
        ProfileBox.ItemsSource = new[] { new ProfileItem(p) };
        ProfileBox.SelectedIndex = 0;
        _loading = false;
        _profile = p;
        NameBox.Text = p.Name;
        UseBox.IsChecked = true;
        _selected = p.Mappings[2];
        Redraw(); UpdateHint();
        StatusText.Text = Loc.T("8 keys in this profile. Changes are saved straight away.");
    }

    KeymapStore Store => _s!.Keymaps;

    // ---------------------------------------------------------------- profiles

    void RefreshProfiles()
    {
        _loading = true;
        var items = Store.Data.Profiles.Select(p => new ProfileItem(p)).ToList();
        ProfileBox.ItemsSource = items;
        var active = Store.ProfileFor(_row?.Sn) ?? _profile ?? Store.Data.Profiles.FirstOrDefault();
        ProfileBox.SelectedItem = items.FirstOrDefault(i => i.Profile == active);
        _loading = false;
        SelectProfile(ProfileBox.SelectedItem as ProfileItem);
    }

    void SelectProfile(ProfileItem? item)
    {
        _loading = true;
        _profile = item?.Profile;
        _selected = null;
        NameBox.Text = _profile?.Name ?? "";
        NameBox.IsEnabled = DeleteButton.IsEnabled = UseBox.IsEnabled = AddTapButton.IsEnabled = AddJoyButton.IsEnabled = _profile != null;
        UseBox.IsChecked = _profile != null && _row?.Sn != null && Store.Data.ActiveProfile.GetValueOrDefault(_row.Sn) == _profile.Id;
        _loading = false;
        Redraw();
        UpdateHint();
        CheckOrientation();
    }

    void NewProfile()
    {
        var p = new KeymapProfile { Name = $"Profile {Store.Data.Profiles.Count + 1}", Landscape = _w > _h };
        Store.Data.Profiles.Add(p);
        if (_row?.Sn != null && !Store.Data.ActiveProfile.ContainsKey(_row.Sn)) Store.Data.ActiveProfile[_row.Sn] = p.Id;
        _profile = p;
        Save();
        RefreshProfiles();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    async Task DeleteProfileAsync()
    {
        if (_profile == null) return;
        if (!await ConfirmWindow.AskAsync(this, Loc.T("Delete profile"), Loc.F("Delete the key profile \"{0}\"?", _profile.Name), null)) return;
        Store.Data.Profiles.Remove(_profile);
        foreach (var k in Store.Data.ActiveProfile.Where(kv => kv.Value == _profile.Id).Select(kv => kv.Key).ToList())
            Store.Data.ActiveProfile.Remove(k);
        _profile = null;
        Save();
        RefreshProfiles();
    }

    void Save(bool refreshList = false)
    {
        if (_s?.Demo == true) return;   // demo/snapshot mode never touches the real key profiles
        try { Store.Save(); } catch (Exception ex) { StatusText.Text = Loc.T("Couldn't save: ") + ex.Message; }
        if (refreshList)
        {
            _loading = true;
            var sel = _profile;
            var items = Store.Data.Profiles.Select(p => new ProfileItem(p)).ToList();
            ProfileBox.ItemsSource = items;
            ProfileBox.SelectedItem = items.FirstOrDefault(i => i.Profile == sel);
            _loading = false;
        }
        _changed?.Invoke();
    }

    // ---------------------------------------------------------------- screenshot

    async Task LoadScreenshotAsync()
    {
        if (_s?.Adb == null) { LoadingText.Text = Loc.T("No device connection (demo mode)."); return; }
        LoadingText.IsVisible = true;
        LoadingText.Text = Loc.T("Taking a screenshot of the device…");
        var png = await _s.Adb.ScreencapAsync(_serial);
        if (png == null) { LoadingText.Text = Loc.T("Couldn't get a screenshot. Is the device unlocked?"); return; }
        using var ms = new MemoryStream(png);
        var bmp = new Bitmap(ms);
        Shot.Source = bmp;
        _w = bmp.PixelSize.Width; _h = bmp.PixelSize.Height;
        Surface.Width = _w; Surface.Height = _h;
        LoadingText.IsVisible = false;
        Redraw();
        CheckOrientation();
    }

    void CheckOrientation()
    {
        if (_profile == null || Shot.Source == null) { StatusText.Text = ""; return; }
        var landscape = _w > _h;
        StatusText.Text = _profile.Mappings.Count > 0 && landscape != _profile.Landscape
            ? Loc.F("This profile was made in {0}, but the device is in {1} now. Rotate it (toolbar) and take a new screenshot.", (_profile.Landscape ? Loc.T("landscape") : Loc.T("portrait")), (landscape ? Loc.T("landscape") : Loc.T("portrait")))
            : Loc.Count(_profile.Mappings.Count, "{0} key in this profile. Changes are saved straight away.", "{0} keys in this profile. Changes are saved straight away.");
        if (_profile.Mappings.Count == 0) _profile.Landscape = landscape;
    }

    // ---------------------------------------------------------------- placing and editing

    void StartPlacing(Placing what)
    {
        if (_profile == null) return;
        _placing = what;
        UpdateHint();
    }

    void UpdateHint()
    {
        HintText.Foreground = _placing != Placing.None || (_selected?.Kind == MappingKind.Tap) ? Paneless.App.Theme.Blue : Paneless.App.Theme.Dim;
        HintText.Text = _profile == null ? Loc.T("Click \"New profile\" to start a key layout for this device.")
            : _placing == Placing.Tap ? Loc.T("Click on the screenshot where the button is. Esc cancels.")
            : _placing == Placing.Joystick ? Loc.T("Click the centre of the game's movement joystick. Esc cancels.")
            : _selected?.Kind == MappingKind.Tap ? Loc.F("Press the key for this spot{0}. Drag to move it.", (_selected.Key.Length > 0 ? Loc.F(" (now: {0})", _selected.Key) : ""))
            : _selected?.Kind == MappingKind.Joystick ? Loc.T("Joystick selected: choose its keys and size above, or drag it to move.")
            : Loc.T("Add keys with the buttons above. Click a key to select it, press a key to change it, drag to move it.");
        JoyPanel.IsVisible = _selected?.Kind == MappingKind.Joystick;
        RemoveButton.IsEnabled = _selected != null;
        if (_selected is { Kind: MappingKind.Joystick } j)
        {
            _loading = true;
            JoyKeysBox.SelectedIndex = j.Up == "Up" ? 1 : 0;
            RadiusSlider.Value = j.Radius;
            _loading = false;
        }
    }

    void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_profile == null) return;
        var p = e.GetPosition(Overlay);
        if (_placing != Placing.None)
        {
            var m = new KeyMapping
            {
                Kind = _placing == Placing.Joystick ? MappingKind.Joystick : MappingKind.Tap,
                X = Math.Clamp(p.X / _w, 0, 1), Y = Math.Clamp(p.Y / _h, 0, 1),
            };
            if (_profile.Mappings.Count == 0) _profile.Landscape = _w > _h;
            _profile.Mappings.Add(m);
            _selected = m;
            _placing = Placing.None;
            Save(); Redraw(); UpdateHint(); CheckOrientation();
            return;
        }
        // Clicking empty space deselects.
        _selected = null;
        Redraw(); UpdateHint();
    }

    void OnMarkerPressed(KeyMapping m, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _selected = m;
        _placing = Placing.None;
        _dragging = true;
        e.Pointer.Capture(Overlay);
        Redraw(); UpdateHint();
    }

    void OnOverlayMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _selected == null) return;
        var p = e.GetPosition(Overlay);
        _selected.X = Math.Clamp(p.X / _w, 0, 1);
        _selected.Y = Math.Clamp(p.Y / _h, 0, 1);
        Redraw();
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _placing = Placing.None; _selected = null; Redraw(); UpdateHint(); e.Handled = true; return; }
        if (_selected is not { Kind: MappingKind.Tap } m || FocusManager?.GetFocusedElement() is TextBox) return;
        e.Handled = true;
        var name = AvaloniaKeys.Name(e.Key);
        if (name == null) { StatusText.Text = Loc.F("{0} can't be used for key mapping.", e.Key); return; }
        m.Key = name;
        var clash = _profile!.Mappings.Any(o => o != m && o.Keys.Contains(name, StringComparer.OrdinalIgnoreCase));
        StatusText.Text = clash ? Loc.F("{0} is also used by another key here; both will be pressed together.", name) : Loc.F("Set to {0}.", name);
        Save(); Redraw(); UpdateHint();
    }

    void RemoveSelected()
    {
        if (_profile == null || _selected == null) return;
        _profile.Mappings.Remove(_selected);
        _selected = null;
        Save(); Redraw(); UpdateHint(); CheckOrientation();
    }

    /// <summary>Marker label: short enough to fit in the circle.</summary>
    static string Short(string key) => key switch
    {
        "" => "?",
        "LeftShift" => "Shift", "RightShift" => "RShift", "LeftCtrl" => "Ctrl", "RightCtrl" => "RCtrl",
        "LeftAlt" => "Alt", "RightAlt" => "RAlt", "Backspace" => "Bksp", "CapsLock" => "Caps",
        "PageUp" => "PgUp", "PageDown" => "PgDn", "Insert" => "Ins", "Delete" => "Del",
        _ => key,
    };

    // ---------------------------------------------------------------- drawing (in screenshot pixels; the Viewbox scales it)

    void Redraw()
    {
        Overlay.Children.Clear();
        if (_profile == null) return;
        var unit = Math.Min(_w, _h);
        foreach (var m in _profile.Mappings)
        {
            var selected = m == _selected;
            Control marker;
            double size;
            if (m.Kind == MappingKind.Tap)
            {
                size = unit * 0.085;
                marker = new Border
                {
                    Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
                    Background = selected ? FillSelected : Fill,
                    BorderBrush = selected ? EdgeSelected : Edge, BorderThickness = new Thickness(unit * 0.005),
                    Child = new TextBlock
                    {
                        Text = Short(m.Key), Foreground = Brushes.White, FontWeight = FontWeight.SemiBold,
                        FontSize = unit * (Short(m.Key).Length > 3 ? 0.022 : 0.034),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                };
            }
            else
            {
                size = m.Radius * unit * 2;
                var label = m.Up == "Up" ? "Arrows" : $"{m.Up}{m.Left}{m.Down}{m.Right}";
                marker = new Grid
                {
                    Width = size, Height = size,
                    Children =
                    {
                        new Ellipse
                        {
                            Fill = selected ? FillSelected : Fill, Stroke = selected ? EdgeSelected : Edge,
                            StrokeThickness = unit * 0.005, StrokeDashArray = [4, 3],
                        },
                        new TextBlock
                        {
                            Text = label, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = unit * 0.03,
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                };
            }
            marker.Cursor = new Cursor(StandardCursorType.SizeAll);
            Canvas.SetLeft(marker, m.X * _w - size / 2);
            Canvas.SetTop(marker, m.Y * _h - size / 2);
            var captured = m;
            marker.PointerPressed += (_, e) => OnMarkerPressed(captured, e);
            Overlay.Children.Add(marker);
        }
    }
}

/// <summary>Avalonia key → the key names used in key profiles (see KeyNames).</summary>
public static class AvaloniaKeys
{
    public static string? Name(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return k.ToString();
        if (k >= Key.D0 && k <= Key.D9) return ((int)(k - Key.D0)).ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return "Num" + (int)(k - Key.NumPad0);
        if (k >= Key.F1 && k <= Key.F12) return "F" + (k - Key.F1 + 1);
        return k switch
        {
            Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab", Key.Back => "Backspace", Key.CapsLock => "CapsLock",
            Key.LeftShift => "LeftShift", Key.RightShift => "RightShift", Key.LeftCtrl => "LeftCtrl", Key.RightCtrl => "RightCtrl",
            Key.LeftAlt => "LeftAlt", Key.RightAlt => "RightAlt",
            Key.Left => "Left", Key.Up => "Up", Key.Right => "Right", Key.Down => "Down",
            Key.Insert => "Insert", Key.Delete => "Delete", Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.OemTilde => "`", Key.OemMinus => "-", Key.OemPlus => "=", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\", Key.OemSemicolon => ";", Key.OemQuotes => "'", Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/",
            _ => null,
        };
    }
}
