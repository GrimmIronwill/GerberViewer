using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace GerberViewer;

public sealed class GerberLayer : INotifyPropertyChanged
{
    private string _name = "";
    private Color _color;
    private double _opacity = 0.85;
    private bool _isVisible = true;
    private bool _isEditing;

    public required string Name
    {
        get => _name;
        set
        {
            // Пустое имя не принимаем: остаётся прежнее.
            string name = value.Trim();

            if (name.Length == 0 || name == _name)
                return;

            _name = name;
            OnPropertyChanged();
        }
    }

    public required string FilePath { get; init; }

    public required Geometry Geometry { get; init; }

    public required string Description { get; init; }

    public required Color Color
    {
        get => _color;
        set
        {
            if (_color == value)
                return;

            _color = value;
            UpdateBrush();
            OnPropertyChanged();
        }
    }

    // Непрозрачность заливки слоя, 0.05–1.
    public double Opacity
    {
        get => _opacity;
        set
        {
            value = Math.Clamp(value, 0.05, 1);

            if (_opacity == value)
                return;

            _opacity = value;
            UpdateBrush();
            OnPropertyChanged();
        }
    }

    // Готовая замороженная кисть для отрисовки во вьюпорте.
    public Brush ColorBrush { get; private set; } = Brushes.Transparent;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
                return;

            _isVisible = value;
            OnPropertyChanged();
        }
    }

    // Состояние интерфейса: строка слоя сейчас переименовывается.
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value)
                return;

            _isEditing = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void UpdateBrush()
    {
        var brush = new SolidColorBrush(_color)
        {
            Opacity = _opacity
        };

        brush.Freeze();

        ColorBrush = brush;
        OnPropertyChanged(nameof(ColorBrush));
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
