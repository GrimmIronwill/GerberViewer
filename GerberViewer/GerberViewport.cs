using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GerberViewer;

public sealed class GerberViewport : FrameworkElement
{
    private static readonly Brush BackgroundBrush =
        new SolidColorBrush(Color.FromRgb(22, 25, 31));

    private readonly HashSet<GerberLayer> _subscribedLayers = [];

    private ObservableCollection<GerberLayer>? _layers;

    // Пикселей на миллиметр.
    private double _scale = 10;

    // Положение начала мировых координат на экране.
    private Vector _offset = new(100, 100);

    private bool _isPanning;
    private Point _lastMousePosition;

    public GerberViewport()
    {
        ClipToBounds = true;
        Focusable = true;

        BackgroundBrush.Freeze();
    }

    public void SetLayers(ObservableCollection<GerberLayer> layers)
    {
        if (_layers is not null)
            _layers.CollectionChanged -= LayersChanged;

        foreach (var layer in _subscribedLayers)
            layer.PropertyChanged -= LayerChanged;

        _subscribedLayers.Clear();

        _layers = layers;
        _layers.CollectionChanged += LayersChanged;

        UpdateSubscriptions();
        InvalidateVisual();
    }

    private void LayersChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        UpdateSubscriptions();
        InvalidateVisual();
    }

    private void UpdateSubscriptions()
    {
        if (_layers is null)
            return;

        // Обрабатывает в том числе ObservableCollection.Clear().
        foreach (var layer in _subscribedLayers.ToArray())
        {
            if (_layers.Contains(layer))
                continue;

            layer.PropertyChanged -= LayerChanged;
            _subscribedLayers.Remove(layer);
        }

        foreach (var layer in _layers)
        {
            if (!_subscribedLayers.Add(layer))
                continue;

            layer.PropertyChanged += LayerChanged;
        }
    }

    private void LayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        dc.DrawRectangle(
            BackgroundBrush,
            null,
            new Rect(RenderSize));

        if (_layers is null)
            return;

        var matrix = new Matrix(
            _scale, 0,
            0, -_scale,
            _offset.X, _offset.Y);

        dc.PushTransform(new MatrixTransform(matrix));

        foreach (var layer in _layers)
        {
            if (!layer.IsVisible)
                continue;

            dc.DrawGeometry(
                layer.ColorBrush,
                null,
                layer.Geometry);
        }

        dc.Pop();
    }

    public void FitToView()
    {
        if (_layers is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var bounds = Rect.Empty;

        foreach (var layer in _layers)
        {
            if (!layer.IsVisible)
                continue;

            bounds.Union(layer.Geometry.Bounds);
        }

        if (bounds.IsEmpty)
        {
            InvalidateVisual();
            return;
        }

        const double margin = 30;

        double availableWidth = Math.Max(1, ActualWidth - margin * 2);
        double availableHeight = Math.Max(1, ActualHeight - margin * 2);

        double width = Math.Max(bounds.Width, 0.001);
        double height = Math.Max(bounds.Height, 0.001);

        _scale = Math.Clamp(
            Math.Min(availableWidth / width, availableHeight / height),
            0.001,
            100_000);

        double centerX = bounds.Left + bounds.Width / 2;
        double centerY = bounds.Top + bounds.Height / 2;

        _offset = new Vector(
            ActualWidth / 2 - centerX * _scale,
            ActualHeight / 2 + centerY * _scale);

        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        Point mouse = e.GetPosition(this);

        double requestedFactor = Math.Pow(1.15, e.Delta / 120.0);
        double newScale = Math.Clamp(
            _scale * requestedFactor,
            0.001,
            100_000);

        double actualFactor = newScale / _scale;

        // Точка мира под курсором остаётся на том же месте.
        _offset = new Vector(
            mouse.X - (mouse.X - _offset.X) * actualFactor,
            mouse.Y - (mouse.Y - _offset.Y) * actualFactor);

        _scale = newScale;

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            FitToView();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _isPanning = true;
            _lastMousePosition = e.GetPosition(this);

            CaptureMouse();
            Cursor = Cursors.SizeAll;

            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_isPanning)
            return;

        Point current = e.GetPosition(this);
        Vector delta = current - _lastMousePosition;

        _offset += delta;
        _lastMousePosition = current;

        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.ChangedButton is not
            (MouseButton.Middle or MouseButton.Right))
        {
            return;
        }

        _isPanning = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        _isPanning = false;
        Cursor = Cursors.Arrow;
    }
}
