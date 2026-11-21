using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Paneless.App.ViewModels;

namespace Paneless.App.Views;

/// <summary>The Wall's pointer handling: a click mirrors, dragging a tile moves it, dragging its corner resizes it.
/// A tile button captures the pointer when pressed, so the list watches its events on the way down (tunnel),
/// before the button turns the release into a click.</summary>
public partial class WallPage : UserControl
{
    const double DragThreshold = 8;

    enum Mode { None, Pressed, Moving, Resizing }

    Mode _mode;
    WallTile? _tile;
    WallTile? _lastTarget;
    Point _start;
    double _startWidth;
    bool _swallowClick;

    public WallPage()
    {
        InitializeComponent();
        TileList.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TileList.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        TileList.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        TileList.AddHandler(Button.ClickEvent, OnClick);
    }

    WallViewModel? Vm => DataContext as WallViewModel;

    static Button? TileButton(object? source) =>
        (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { } b && b.Classes.Contains("tile") ? b : null;

    void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        End();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual v) return;
        // The Wake button inside a tile is a button of its own.
        if (v.FindAncestorOfType<Button>(includeSelf: true) is { } b && !b.Classes.Contains("tile")) return;
        if (TileButton(v)?.DataContext is not WallTile tile) return;
        _tile = tile;
        _start = e.GetPosition(this);
        _startWidth = tile.Width;
        _mode = v.FindAncestorOfType<Border>(includeSelf: true) is { } grip && grip.Classes.Contains("grip") ? Mode.Resizing : Mode.Pressed;
    }

    void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_tile == null || Vm is not { } vm || _mode == Mode.None) return;
        // Moving a tile can rebuild its button, which drops the pointer capture; a release outside the list is then
        // never seen here. The button no longer being held ends the drag.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { vm.SaveLayout(); End(); return; }
        var p = e.GetPosition(this);
        if (_mode == Mode.Resizing)
        {
            // Measured from where the drag began on the page, which doesn't move while the tile grows.
            vm.ResizeTile(_tile, _startWidth + (p.X - _start.X));
            return;
        }
        if (_mode == Mode.Pressed)
        {
            if (Math.Abs(p.X - _start.X) < DragThreshold && Math.Abs(p.Y - _start.Y) < DragThreshold) return;
            _mode = Mode.Moving;
            _tile.IsDragging = true;
        }
        // The tile under the pointer gives up its place. Only once per tile entered, so tiles of different sizes
        // can't swap back and forth while the pointer rests.
        var target = TileButton(TileList.InputHitTest(e.GetPosition(TileList)))?.DataContext as WallTile;
        if (target == null || target == _tile || target == _lastTarget) return;
        _lastTarget = target;
        vm.MoveTile(_tile, vm.Tiles.IndexOf(target));
    }

    void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_mode is Mode.Moving or Mode.Resizing)
        {
            Vm?.SaveLayout();
            // The button raises Click right after this (if the pointer is still on it): that isn't a click.
            _swallowClick = true;
            Dispatcher.UIThread.Post(() => _swallowClick = false, DispatcherPriority.Background);
        }
        End();
    }

    void End()
    {
        if (_tile != null) _tile.IsDragging = false;
        _tile = null; _lastTarget = null; _mode = Mode.None;
    }

    void OnClick(object? sender, RoutedEventArgs e)
    {
        if (TileButton(e.Source) is not { DataContext: WallTile tile } button || !ReferenceEquals(e.Source, button)) return;
        if (_swallowClick) { _swallowClick = false; return; }
        tile.Wall.MirrorCommand.Execute(tile);
    }
}
