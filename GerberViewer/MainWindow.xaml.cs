using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GerberViewer;

public partial class MainWindow : Window
{
    // Столько пикселей занимает 1 мм при масштабе 100 % (96 DPI).
    private const double PixelsPerMillimeter = 96 / 25.4;

    private readonly ObservableCollection<GerberLayer> _layers = [];

    private bool _isLoading;

    private static readonly Color[] LayerColors =
    [
        Color.FromRgb(80, 220, 130),
        Color.FromRgb(255, 100, 110),
        Color.FromRgb(90, 160, 255),
        Color.FromRgb(255, 205, 80),
        Color.FromRgb(200, 120, 255),
        Color.FromRgb(80, 225, 230),
        Color.FromRgb(255, 155, 75),
        Color.FromRgb(220, 225, 235)
    ];

    public MainWindow()
    {
        InitializeComponent();

        LayersList.ItemsSource = _layers;
        Viewport.SetLayers(_layers);

        _layers.CollectionChanged += (_, _) => UpdateLayerSummary();
        Viewport.ViewChanged += (_, _) => UpdateZoomText();

        UpdateLayerSummary();
        UpdateZoomText();
    }

    // ------------------------------------------------------------
    // Загрузка файлов
    // ------------------------------------------------------------

    private async void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите файлы Gerber",
            Multiselect = true,
            CheckFileExists = true,

            // Расширение не определяет формат содержимого.
            Filter =
                "Все файлы (*.*)|*.*|" +
                "Gerber (*.gbr;*.ger;*.gtl;*.gbl;*.gts;*.gbs;*.gto;*.gbo;*.gko;*.gm1)|" +
                "*.gbr;*.ger;*.gtl;*.gbl;*.gts;*.gbs;*.gto;*.gbo;*.gko;*.gm1"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        await LoadFilesAsync(dialog.FileNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects =
            !_isLoading && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;

        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_isLoading ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        string[] files = paths.Where(File.Exists).ToArray();

        if (files.Length == 0)
        {
            StatusText.Text = "Среди перетащенных объектов нет файлов.";
            return;
        }

        await LoadFilesAsync(files);
    }

    private async Task LoadFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (_isLoading || filePaths.Count == 0)
            return;

        _isLoading = true;
        UpdateCommandStates();

        var errors = new List<string>();
        var warnings = new List<string>();
        int loaded = 0;

        try
        {
            foreach (string filePath in filePaths)
            {
                string name = Path.GetFileName(filePath);

                StatusText.Text = $"Чтение: {name}…";

                try
                {
                    // Парсер и геометрические операции не блокируют UI.
                    GerberParseResult result = await Task.Run(
                        () => GerberParser.ParseFile(filePath));

                    /* Диагностический запуск с пропуском неизвестных расширенных команд
                    GerberParseResult result = await Task.Run(
                        () => GerberParser.ParseFile(
                            filePath,
                            ignoreUnknownExtendedCommands: true));
                    */

                    foreach (string warning in result.Warnings)
                    {
                        warnings.Add($"{name}\n{warning}");
                    }

                    Rect bounds = result.Geometry.Bounds;

                    string size = bounds.IsEmpty
                        ? "пустое изображение"
                        : $"{bounds.Width:0.###} × {bounds.Height:0.###} мм";

                    _layers.Add(new GerberLayer
                    {
                        Name = name,
                        FilePath = filePath,
                        Geometry = result.Geometry,
                        Color = LayerColors[_layers.Count % LayerColors.Length],
                        Description =
                            $"{size}; объектов: {result.PrimitiveCount}" +
                            (result.Warnings.Count > 0
                                ? $"; ⚠ предупреждений: {result.Warnings.Count}"
                                : "")
                    });

                    loaded++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{name}\n{ex.Message}");
                }
            }

            if (loaded > 0)
                Viewport.FitToView();

            StatusText.Text =
                $"Загружено: {loaded}. " +
                $"Всего слоёв: {_layers.Count}. " +
                $"Ошибок: {errors.Count}. " +
                $"Предупреждений: {warnings.Count}.";

            if (errors.Count > 0)
            {
                MessageBox.Show(
                    this,
                    string.Join("\n\n", errors),
                    "Не удалось загрузить некоторые файлы",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            if (warnings.Count > 0)
            {
                MessageBox.Show(
                    this,
                    string.Join("\n\n", warnings),
                    "Предупреждения при чтении Gerber",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            _isLoading = false;
            UpdateCommandStates();
        }
    }

    // ------------------------------------------------------------
    // Панель инструментов
    // ------------------------------------------------------------

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        Viewport.FitToView();
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var layer in _layers)
            layer.IsVisible = true;
    }

    private void HideAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var layer in _layers)
            layer.IsVisible = false;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
            return;

        _layers.Clear();
        StatusText.Text = "Все слои удалены.";
    }

    // ------------------------------------------------------------
    // Действия со слоем
    // ------------------------------------------------------------

    private static GerberLayer? LayerOf(object sender)
    {
        return (sender as FrameworkElement)?.DataContext as GerberLayer;
    }

    private void RemoveLayer_Click(object sender, RoutedEventArgs e)
    {
        if (LayerOf(sender) is { } layer)
            RemoveLayer(layer);
    }

    private void RemoveLayer(GerberLayer layer)
    {
        if (!_layers.Remove(layer))
            return;

        StatusText.Text = $"Слой «{layer.Name}» удалён. Всего слоёв: {_layers.Count}.";
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        BeginRename(LayerOf(sender));
    }

    private void Solo_Click(object sender, RoutedEventArgs e)
    {
        if (LayerOf(sender) is not { } layer)
            return;

        foreach (var other in _layers)
            other.IsVisible = ReferenceEquals(other, layer);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        MoveLayer(LayerOf(sender), -1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        MoveLayer(LayerOf(sender), +1);
    }

    private void MoveLayer(GerberLayer? layer, int delta)
    {
        if (layer is null)
            return;

        int oldIndex = _layers.IndexOf(layer);
        int newIndex = oldIndex + delta;

        if (oldIndex < 0 || newIndex < 0 || newIndex >= _layers.Count)
            return;

        _layers.Move(oldIndex, newIndex);

        LayersList.SelectedItem = layer;
        LayersList.ScrollIntoView(layer);
    }

    private void LayerItem_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        // Правый щелчок выделяет строку перед показом меню.
        if (sender is ListBoxItem item)
            item.IsSelected = true;
    }

    private void LayersList_KeyDown(object sender, KeyEventArgs e)
    {
        // Клавиши внутри поля ввода и кнопок обрабатывают они сами.
        if (e.OriginalSource is TextBox or ButtonBase)
            return;

        if (LayersList.SelectedItem is not GerberLayer layer)
            return;

        switch (e.Key)
        {
            case Key.F2:
                BeginRename(layer);
                e.Handled = true;
                break;

            case Key.Delete:
                RemoveLayer(layer);
                e.Handled = true;
                break;

            case Key.Space:
                layer.IsVisible = !layer.IsVisible;
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------
    // Переименование
    // ------------------------------------------------------------

    private void BeginRename(GerberLayer? layer)
    {
        if (layer is null)
            return;

        foreach (var other in _layers)
            other.IsEditing = false;

        LayersList.SelectedItem = layer;
        layer.IsEditing = true;
    }

    private void LayerName_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        BeginRename(LayerOf(sender));
        e.Handled = true;
    }

    private void NameEditor_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox editor || !editor.IsVisible)
            return;

        // Фокус ставим после того, как поле реально появится на экране.
        editor.Dispatcher.InvokeAsync(
            () =>
            {
                editor.Focus();
                editor.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void NameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor ||
            editor.DataContext is not GerberLayer layer)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                CommitRename(editor, layer);
                e.Handled = true;
                break;

            case Key.Escape:
                CancelRename(editor, layer);
                e.Handled = true;
                break;
        }
    }

    private void NameEditor_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox editor &&
            editor.DataContext is GerberLayer { IsEditing: true } layer)
        {
            CommitRename(editor, layer, moveFocus: false);
        }
    }

    private void CommitRename(
        TextBox editor,
        GerberLayer layer,
        bool moveFocus = true)
    {
        var binding = editor.GetBindingExpression(TextBox.TextProperty);

        if (string.IsNullOrWhiteSpace(editor.Text))
            binding?.UpdateTarget();
        else
            binding?.UpdateSource();

        layer.IsEditing = false;

        if (moveFocus)
            LayersList.Focus();
    }

    private void CancelRename(TextBox editor, GerberLayer layer)
    {
        editor.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();

        layer.IsEditing = false;
        LayersList.Focus();
    }

    // ------------------------------------------------------------
    // Цвет слоя
    // ------------------------------------------------------------

    private void PaletteColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.Background is not SolidColorBrush brush ||
            LayerOf(sender) is not { } layer)
        {
            return;
        }

        layer.Color = brush.Color;
        CloseParentPopup(button);
    }

    private static void CloseParentPopup(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is FrameworkElement { Parent: Popup popup })
            {
                popup.IsOpen = false;
                return;
            }

            element = VisualTreeHelper.GetParent(element);
        }
    }

    // ------------------------------------------------------------
    // Строка состояния и вспомогательные обновления
    // ------------------------------------------------------------

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        Point world = Viewport.ScreenToWorld(e.GetPosition(Viewport));

        CursorText.Text =
            $"X {world.X,10:0.000}  Y {world.Y,10:0.000} мм";
    }

    private void Viewport_MouseLeave(object sender, MouseEventArgs e)
    {
        CursorText.Text = "X —          Y —          мм";
    }

    private void UpdateZoomText()
    {
        double percent = Viewport.Scale / PixelsPerMillimeter * 100;

        ZoomText.Text = percent >= 10
            ? $"{percent:0} %"
            : $"{percent:0.0} %";
    }

    private void UpdateLayerSummary()
    {
        int count = _layers.Count;

        LayersHeader.Text = count == 0 ? "Слои" : $"Слои · {count}";

        EmptyHint.Visibility = count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateCommandStates();
    }

    private void UpdateCommandStates()
    {
        bool hasLayers = _layers.Count > 0;

        OpenButton.IsEnabled = !_isLoading;
        FitButton.IsEnabled = hasLayers && !_isLoading;
        ShowAllButton.IsEnabled = hasLayers && !_isLoading;
        HideAllButton.IsEnabled = hasLayers && !_isLoading;
        ClearButton.IsEnabled = hasLayers && !_isLoading;

        LoadingBar.Visibility = _isLoading
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
