using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace GerberViewer;

public sealed class GerberLayer : INotifyPropertyChanged
{
    private bool _isVisible = true;

    public required string Name { get; init; }

    public required string FilePath { get; init; }

    public required Geometry Geometry { get; init; }

    public required Brush ColorBrush { get; init; }

    public required string Description { get; init; }

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}