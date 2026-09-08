using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace GerberViewer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<GerberLayer> _layers = [];

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
    }

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

        OpenButton.IsEnabled = false;
        ClearButton.IsEnabled = false;

        var errors = new List<string>();
        var warnings = new List<string>();
        int loaded = 0;

        try
        {
            foreach (string filePath in dialog.FileNames)
            {
                string name = Path.GetFileName(filePath);

                StatusText.Text = $"Чтение: {name}...";

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

                    Color color =
                        LayerColors[_layers.Count % LayerColors.Length];

                    // Небольшая прозрачность позволяет видеть
                    // наложение разных слоёв.
                    var brush = new SolidColorBrush(color)
                    {
                        Opacity = 0.85
                    };

                    brush.Freeze();

                    Rect bounds = result.Geometry.Bounds;

                    string size = bounds.IsEmpty
                        ? "пустое изображение"
                        : $"{bounds.Width:0.###} × {bounds.Height:0.###} мм";

                    _layers.Add(new GerberLayer
                    {
                        Name = name,
                        FilePath = filePath,
                        Geometry = result.Geometry,
                        ColorBrush = brush,
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
            OpenButton.IsEnabled = true;
            ClearButton.IsEnabled = true;
        }
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        Viewport.FitToView();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _layers.Clear();
        StatusText.Text = "Все слои удалены.";
    }
}
