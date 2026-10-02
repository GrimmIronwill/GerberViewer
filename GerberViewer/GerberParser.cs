using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace GerberViewer;

public sealed record GerberParseResult(
    Geometry Geometry,
    int PrimitiveCount,
    IReadOnlyList<string> Warnings);

public sealed partial class GerberParser
{
    private readonly List<string> _warnings = [];

    // Один раз предупреждаем о превышении разрядности FS.
    // Число дробных разрядов при этом не изменяем.
    private bool _coordinateWidthWarningAdded;

    private bool _ignoreUnknownExtendedCommands;
    private int _commandNumber;

    // Устаревшая команда IR относится ко всему изображению,
    // а не к отдельной апертуре.
    private double _imageRotation;

    private const string DecimalPattern =
        @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)";

    private static readonly Regex LegacyOffsetRegex = new(
        @"^OF(?:A(" + DecimalPattern + @"))?(?:B(" +
        DecimalPattern + @"))?$",
        RegexOptions.Compiled);

    private static readonly Regex LegacyScaleRegex = new(
        @"^SF(?:A(" + DecimalPattern + @"))?(?:B(" +
        DecimalPattern + @"))?$",
        RegexOptions.Compiled);

    private static readonly Regex LegacyMirrorRegex = new(
        @"^MI(?:A([01]))?(?:B([01]))?$",
        RegexOptions.Compiled);

    private void AddWarning(string message)
    {
        _warnings.Add($"Команда №{_commandNumber}: {message}");
    }


    // Допуск аппроксимации геометрии, мм.
    private const double Tolerance = 0.002;

    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private static readonly Regex FormatRegex = new(
        @"^FS([LT])([AI])X(\d)(\d)Y(\d)(\d)$",
        RegexOptions.Compiled);

    private static readonly Regex ApertureRegex = new(
        @"^ADD(\d+)([A-Za-z_$\.][A-Za-z0-9_$\.]*)(?:,(.*))?$",
        RegexOptions.Compiled);

    private static readonly Regex WordRegex = new(
        @"([A-Z])([+-]?(?:\d+(?:\.\d*)?|\.\d+))",
        RegexOptions.Compiled);

    private readonly Dictionary<int, Aperture> _apertures = [];

    private Geometry _image = Geometry.Empty;

    private Point _position;

    private Aperture? _selectedAperture;

    private bool _hasFormat;
    private bool _hasUnits;

    private bool _trailingZeroSuppression;

    private int _xInteger;
    private int _xFraction;
    private int _yInteger;
    private int _yFraction;

    // Все выходные координаты переводятся в миллиметры.
    private double _unitFactor = 1;

    private int _interpolation = 1;
    private int _operation = 2;

    private bool _multiQuadrant;
    private bool _dark = true;
    private bool _ended;

    private PathGeometry? _region;
    private PathFigure? _contour;

    private int _primitiveCount;

    private sealed record Aperture(
        char Kind,
        double Width,
        double Height,
        int Vertices,
        double Rotation,
        double HoleDiameter,
        Geometry? MacroGeometry = null);

    private readonly record struct Command(
        bool Extended,
        string Text);

    public static GerberParseResult ParseFile(
        string path,
        bool ignoreUnknownExtendedCommands = false)
    {
        return ParseText(
            File.ReadAllText(path),
            ignoreUnknownExtendedCommands);
    }

    public static GerberParseResult ParseText(
        string text,
        bool ignoreUnknownExtendedCommands = false)
    {
        ArgumentNullException.ThrowIfNull(text);

        var parser = new GerberParser
        {
            _ignoreUnknownExtendedCommands =
                ignoreUnknownExtendedCommands
        };

        return parser.Parse(text);
    }


    private GerberParseResult Parse(string text)
    {
        int commandNumber = 0;

        foreach (Command command in Tokenize(text))
        {
            if (_ended)
                break;

            commandNumber++;
            _commandNumber = commandNumber;

            try
            {
                if (command.Extended)
                    ParseExtended(command.Text);
                else
                    ParseStandard(command.Text);
            }
            catch (Exception ex) when (
                ex is FormatException or
                NotSupportedException or
                ArgumentException or
                OverflowException)
            {
                string preview = command.Text.Length <= 140
                    ? command.Text
                    : command.Text[..140] + "...";

                throw new FormatException(
                    $"Команда №{commandNumber}: {preview}\n{ex.Message}",
                    ex);
            }
        }

        if (_region is not null)
            throw new FormatException("Регион G36 не закрыт командой G37.");

        if (!_ended)
            throw new FormatException("Не найдена команда завершения M02.");

        if (_primitiveCount == 0)
            throw new FormatException("Файл не содержит поддерживаемой графики.");

        FlushImageBatch();

        if (_imageRotation != 0)
        {
            // Обёртка позволяет не изменять исходную геометрию,
            // которая в отдельных случаях может быть заморожена.
            var rotatedImage = new GeometryGroup
            {
                FillRule = FillRule.Nonzero,
                Transform = new RotateTransform(_imageRotation)
            };

            rotatedImage.Children.Add(_image);
            _image = rotatedImage;
        }

        _image.Freeze();

        return new GerberParseResult(
            _image,
            _primitiveCount,
            _warnings.ToArray());

    }

    private static IEnumerable<Command> Tokenize(string text)
    {
        int i = 0;

        while (i < text.Length)
        {
            while (i < text.Length &&
                   (char.IsWhiteSpace(text[i]) || text[i] == '\uFEFF'))
            {
                i++;
            }

            if (i >= text.Length)
                yield break;

            if (text[i] == '%')
            {
                int end = text.IndexOf('%', i + 1);

                if (end < 0)
                    throw new FormatException("Не закрыт блок %...%.");

                string block = text[(i + 1)..end];

                if (!block.TrimEnd().EndsWith('*'))
                {
                    throw new FormatException(
                        "Команда внутри %...% должна завершаться символом '*'.");
                }

                if (block.TrimStart().StartsWith("AM", StringComparison.Ordinal))
                {
                    // AM занимает весь блок %...%.
                    // Внутренние '*' разделяют операторы макроса,
                    // а не самостоятельные расширенные команды.
                    yield return new Command(true, block.Trim());
                }
                else
                {
                    foreach (string part in block.Split('*'))
                    {
                        string command = part.Trim();

                        if (command.Length > 0)
                            yield return new Command(true, command);
                    }
                }


                i = end + 1;
            }
            else
            {
                int end = text.IndexOf('*', i);

                if (end < 0)
                {
                    throw new FormatException(
                        "Обычная команда не завершена символом '*'.");
                }

                string command = text[i..end].Trim();

                if (command.Length > 0)
                    yield return new Command(false, command);

                i = end + 1;
            }
        }
    }

    private static string RemoveWhitespace(string text)
    {
        var result = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c))
                result.Append(c);
        }

        return result.ToString();
    }

    private bool TryParseAdditionalExtended(string command)
    {
        // Устаревшие имена изображения и уровня.
        // На геометрию не влияют.
        if (command.StartsWith("IN", StringComparison.Ordinal) ||
            command.StartsWith("LN", StringComparison.Ordinal))
        {
            return true;
        }

        // Поворот всего изображения.
        if (command.StartsWith("IR", StringComparison.Ordinal))
        {
            EnsureImageHeader(command);

            double angle = ParseNumber(command[2..]);

            if (angle != 0 &&
                angle != 90 &&
                angle != 180 &&
                angle != 270)
            {
                throw new NotSupportedException(
                    "Для IR поддерживаются углы 0, 90, 180 и 270 градусов.");
            }

            _imageRotation = angle;
            return true;
        }

        // Обычное соответствие осей.
        if (command == "ASAXBY")
        {
            EnsureImageHeader(command);
            return true;
        }

        if (command.StartsWith("AS", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "Перестановка осей AS пока не поддерживается. " +
                "Допустима только ASAXBY.");
        }

        // Устаревшее смещение изображения.
        // Пока поддерживаем только отсутствие смещения.
        if (command.StartsWith("OF", StringComparison.Ordinal))
        {
            EnsureImageHeader(command);

            Match match = LegacyOffsetRegex.Match(command);

            if (!match.Success ||
                (!match.Groups[1].Success && !match.Groups[2].Success))
            {
                throw new FormatException("Неверная команда OF.");
            }

            double a = match.Groups[1].Success
                ? ParseNumber(match.Groups[1].Value)
                : 0;

            double b = match.Groups[2].Success
                ? ParseNumber(match.Groups[2].Value)
                : 0;

            if (a != 0 || b != 0)
            {
                throw new NotSupportedException(
                    "Ненулевое смещение OF пока не поддерживается.");
            }

            return true;
        }

        // Устаревшее масштабирование изображения.
        if (command.StartsWith("SF", StringComparison.Ordinal))
        {
            EnsureImageHeader(command);

            Match match = LegacyScaleRegex.Match(command);

            if (!match.Success ||
                (!match.Groups[1].Success && !match.Groups[2].Success))
            {
                throw new FormatException("Неверная команда SF.");
            }

            double a = match.Groups[1].Success
                ? ParseNumber(match.Groups[1].Value)
                : 1;

            double b = match.Groups[2].Success
                ? ParseNumber(match.Groups[2].Value)
                : 1;

            if (a != 1 || b != 1)
            {
                throw new NotSupportedException(
                    "Масштабирование SF, отличное от 1, " +
                    "пока не поддерживается.");
            }

            return true;
        }

        // Устаревшее зеркальное отражение изображения.
        if (command.StartsWith("MI", StringComparison.Ordinal))
        {
            EnsureImageHeader(command);

            Match match = LegacyMirrorRegex.Match(command);

            if (!match.Success ||
                (!match.Groups[1].Success && !match.Groups[2].Success))
            {
                throw new FormatException("Неверная команда MI.");
            }

            bool mirrorA =
                match.Groups[1].Success &&
                match.Groups[1].Value == "1";

            bool mirrorB =
                match.Groups[2].Success &&
                match.Groups[2].Value == "1";

            if (mirrorA || mirrorB)
            {
                throw new NotSupportedException(
                    "Зеркальное отражение MI пока не поддерживается.");
            }

            return true;
        }

        // Принимаем также LR0.0, LR+0 и аналогичные записи.
        if (command.StartsWith("LR", StringComparison.Ordinal))
        {
            EnsureOutsideRegion();

            double rotation = ParseNumber(command[2..]);

            if (rotation != 0)
            {
                throw new NotSupportedException(
                    "Поворот апертуры LR, отличный от 0, " +
                    "пока не поддерживается.");
            }

            return true;
        }

        // Принимаем LS1.0, LS1.000 и аналогичные записи.
        if (command.StartsWith("LS", StringComparison.Ordinal))
        {
            EnsureOutsideRegion();

            double scale = ParseNumber(command[2..]);

            if (scale != 1)
            {
                throw new NotSupportedException(
                    "Масштабирование апертуры LS, отличное от 1, " +
                    "пока не поддерживается.");
            }

            return true;
        }

        if (command.StartsWith("LM", StringComparison.Ordinal))
        {
            EnsureOutsideRegion();

            if (command != "LMN")
            {
                throw new NotSupportedException(
                    "Зеркальное отражение апертуры LM " +
                    "пока не поддерживается.");
            }

            return true;
        }

        // Закрытие step-and-repeat.
        // Безопасно, поскольку открытие активного SR этот парсер
        // по-прежнему отклоняет.
        if (command == "SR")
        {
            EnsureOutsideRegion();
            return true;
        }

        return false;
    }

    private void EnsureImageHeader(string command)
    {
        EnsureOutsideRegion();

        // Эти устаревшие параметры обрабатываются как параметры
        // всего изображения. Изменять их после рисования не разрешаем.
        if (_primitiveCount != 0)
        {
            throw new NotSupportedException(
                $"Команда {command} поддерживается только " +
                "до первой графической операции.");
        }
    }


    private void ParseExtended(string text)
    {
        if (text.StartsWith("AM", StringComparison.Ordinal))
        {
            EnsureOutsideRegion();
            ParseMacroDefinition(text);
            return;
        }

        // Атрибуты X2 не изменяют графику.
        if (text.StartsWith("TF", StringComparison.Ordinal) ||
            text.StartsWith("TA", StringComparison.Ordinal) ||
            text.StartsWith("TO", StringComparison.Ordinal) ||
            text.StartsWith("TD", StringComparison.Ordinal))
        {
            return;
        }

        string command = RemoveWhitespace(text);
        if (TryParseAdditionalExtended(command))
            return;

        if (command.StartsWith("FS", StringComparison.Ordinal))
        {
            Match match = FormatRegex.Match(command);

            if (!match.Success)
                throw new NotSupportedException("Неподдерживаемый формат FS.");

            if (match.Groups[2].Value != "A")
            {
                throw new NotSupportedException(
                    "Инкрементальные координаты пока не поддерживаются.");
            }

            _trailingZeroSuppression = match.Groups[1].Value == "T";

            _xInteger = int.Parse(match.Groups[3].Value, Invariant);
            _xFraction = int.Parse(match.Groups[4].Value, Invariant);
            _yInteger = int.Parse(match.Groups[5].Value, Invariant);
            _yFraction = int.Parse(match.Groups[6].Value, Invariant);

            if (_xInteger is < 1 or > 6 ||
                _yInteger is < 1 or > 6 ||
                _xFraction > 6 ||
                _yFraction > 6)
            {
                throw new NotSupportedException(
                    "Поддерживаются форматы координат с 1–6 целыми " +
                    "и 0–6 дробными разрядами.");
            }

            _hasFormat = true;
            return;
        }

        switch (command)
        {
            case "MOMM":
                SetUnits(1);
                return;

            case "MOIN":
                SetUnits(25.4);
                return;

            case "LPD":
                EnsureOutsideRegion();
                _dark = true;
                return;

            case "LPC":
                EnsureOutsideRegion();
                _dark = false;
                return;

            case "IPPOS":
                return;

            case "IPNEG":
                throw new NotSupportedException(
                    "Отрицательная полярность всего изображения IPNEG " +
                    "не поддерживается.");

            // Нейтральные преобразования.
            case "LMN":
            case "LR0":
            case "LS1":
                return;
        }

        if (command.StartsWith("ADD", StringComparison.Ordinal))
        {
            ParseAperture(command);
            return;
        }

        if (command.StartsWith("SR", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "Step-and-repeat SR пока не поддерживается.");
        }

        // Известные команды, которые могут менять геометрию,
        // нельзя превращать в безобидные предупреждения.
        if (command.StartsWith("AB", StringComparison.Ordinal) ||
            command.StartsWith("IJ", StringComparison.Ordinal) ||
            command.StartsWith("IO", StringComparison.Ordinal) ||
            command.StartsWith("KO", StringComparison.Ordinal) ||
            command.StartsWith("IP", StringComparison.Ordinal) ||
            command.StartsWith("LP", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Команда %{command}% может изменять геометрию " +
                "и пока не поддерживается.");
        }

        if (_ignoreUnknownExtendedCommands)
        {
            AddWarning(
                $"Пропущена неизвестная расширенная команда %{command}%. " +
                "Корректность изображения не гарантируется.");

            return;
        }

        throw new NotSupportedException(
            $"Неподдерживаемая расширенная команда: %{command}%.");
    }

    private void SetUnits(double factor)
    {
        _unitFactor = factor;
        _hasUnits = true;
    }

    private void ParseAperture(string command)
    {
        if (!_hasUnits)
        {
            throw new FormatException(
                "До определения апертур необходимо указать MOMM или MOIN.");
        }

        Match match = ApertureRegex.Match(command);

        if (!match.Success)
            throw new FormatException("Неверное определение апертуры.");

        int id = int.Parse(match.Groups[1].Value, Invariant);
        string template = match.Groups[2].Value;

        if (id < 10)
            throw new FormatException("Номер апертуры должен быть >= 10.");

        if (template is not ("C" or "R" or "O" or "P"))
        {
            ParseMacroAperture(
                id,
                template,
                match.Groups[3].Value);

            return;
        }

        double[] values = match.Groups[3].Value
            .Split('X', StringSplitOptions.None)
            .Select(ParseNumber)
            .ToArray();

        char kind = template[0];

        double width;
        double height;
        double hole = 0;
        double rotation = 0;
        int vertices = 0;

        switch (kind)
        {
            case 'C':
                RequireParameterCount(values, 1, 2);

                width = height = values[0] * _unitFactor;

                if (values.Length == 2)
                    hole = values[1] * _unitFactor;

                break;

            case 'R':
            case 'O':
                RequireParameterCount(values, 2, 3);

                width = values[0] * _unitFactor;
                height = values[1] * _unitFactor;

                if (values.Length == 3)
                    hole = values[2] * _unitFactor;

                break;

            case 'P':
                RequireParameterCount(values, 2, 4);

                width = height = values[0] * _unitFactor;

                if (values[1] != Math.Truncate(values[1]) ||
                    values[1] is < 3 or > 12)
                {
                    throw new FormatException(
                        "Число вершин апертуры P должно быть от 3 до 12.");
                }

                vertices = (int)values[1];

                if (values.Length >= 3)
                    rotation = values[2];

                if (values.Length == 4)
                    hole = values[3] * _unitFactor;

                break;

            default:
                throw new NotSupportedException();
        }

        if (width <= 0 || height <= 0 || hole < 0)
            throw new FormatException("Неверные размеры апертуры.");

        double maxHole = kind == 'P'
            ? width * Math.Cos(Math.PI / vertices)
            : Math.Min(width, height);

        if (hole > 0 && hole >= maxHole)
        {
            throw new FormatException(
                "Отверстие должно помещаться внутри апертуры.");
        }

        if (_apertures.ContainsKey(id))
        {
            throw new FormatException(
                $"Апертура D{id} уже определена.");
        }

        _apertures.Add(
            id,
            new Aperture(
                kind,
                width,
                height,
                vertices,
                rotation,
                hole));
    }

    private static void RequireParameterCount(
        double[] values,
        int min,
        int max)
    {
        if (values.Length < min || values.Length > max)
        {
            throw new NotSupportedException(
                "Неподдерживаемый набор параметров апертуры. " +
                "Поддерживается только круглое внутреннее отверстие.");
        }
    }

    private void ParseStandard(string text)
    {
        if (text.StartsWith("G04", StringComparison.Ordinal) ||
            text.StartsWith("G4 ", StringComparison.Ordinal) ||
            text == "G4")
        {
            return;
        }

        string command = RemoveWhitespace(text);

        if (command is "M02" or "M2")
        {
            EnsureOutsideRegion();
            _ended = true;
            return;
        }

        string? x = null;
        string? y = null;
        string? i = null;
        string? j = null;

        int? d = null;

        int consumed = 0;

        foreach (Match word in WordRegex.Matches(command))
        {
            if (word.Index != consumed)
                throw new FormatException("Не удалось разобрать команду.");

            consumed += word.Length;

            char letter = word.Groups[1].Value[0];
            string value = word.Groups[2].Value;

            switch (letter)
            {
                case 'G':
                    HandleGCode(ParseInteger(value));
                    break;

                case 'D':
                    if (d is not null)
                        throw new FormatException("Повторный D-код.");

                    d = ParseInteger(value);
                    break;

                case 'X':
                    SetCoordinateWord(ref x, value, 'X');
                    break;

                case 'Y':
                    SetCoordinateWord(ref y, value, 'Y');
                    break;

                case 'I':
                    SetCoordinateWord(ref i, value, 'I');
                    break;

                case 'J':
                    SetCoordinateWord(ref j, value, 'J');
                    break;

                case 'N':
                    // Номер строки старых генераторов.
                    _ = ParseInteger(value);
                    break;

                default:
                    throw new NotSupportedException(
                        $"Слово '{letter}' не поддерживается.");
            }
        }

        if (consumed != command.Length || consumed == 0)
            throw new FormatException("Не удалось разобрать команду.");

        bool hasCoordinates =
            x is not null || y is not null ||
            i is not null || j is not null;

        if (d is >= 10)
        {
            if (_region is not null)
            {
                throw new NotSupportedException(
                    "Выбор апертуры внутри региона не поддерживается.");
            }

            if (hasCoordinates)
            {
                throw new NotSupportedException(
                    "Выбор апертуры и координаты в одном блоке " +
                    "не поддерживаются.");
            }

            if (!_apertures.TryGetValue(d.Value, out var aperture))
                throw new FormatException($"Апертура D{d} не определена.");

            _selectedAperture = aperture;
            return;
        }

        if (d is not null)
        {
            if (d.Value is < 1 or > 3)
                throw new NotSupportedException($"D{d} не поддерживается.");

            _operation = d.Value;
        }

        if (!hasCoordinates && d is null)
            return;

        if (!_hasFormat || !_hasUnits)
        {
            throw new FormatException(
                "До координат необходимо указать FS и MO.");
        }

        var target = new Point(
            x is null ? _position.X : ReadCoordinate(x, true),
            y is null ? _position.Y : ReadCoordinate(y, false));

        if (_operation != 1 && (i is not null || j is not null))
        {
            throw new FormatException(
                "Смещения I/J допустимы только при рисовании дуги.");
        }

        switch (_operation)
        {
            case 2:
                _position = target;
                _contour = null;
                return;

            case 3:
                EnsureOutsideRegion();

                AddGeometry(
                    BuildFlash(GetSelectedAperture(), target));

                _position = target;
                return;

            case 1:
                {
                    List<Point> points;

                    if (_interpolation == 1)
                    {
                        if (i is not null || j is not null)
                        {
                            throw new FormatException(
                                "I/J указаны при линейной интерполяции.");
                        }

                        points = [_position, target];
                    }
                    else
                    {
                        points = BuildArc(
                            _position,
                            target,
                            i is null ? 0 : ReadCoordinate(i, true),
                            j is null ? 0 : ReadCoordinate(j, false));
                    }

                    if (_region is not null)
                        AppendRegion(points);
                    else
                        AddGeometry(BuildStroke(GetSelectedAperture(), points));

                    _position = target;
                    return;
                }
        }
    }

    private static void SetCoordinateWord(
        ref string? destination,
        string value,
        char name)
    {
        if (destination is not null)
            throw new FormatException($"Повторная координата {name}.");

        destination = value;
    }

    private void HandleGCode(int code)
    {
        switch (code)
        {
            case 1:
            case 2:
            case 3:
                _interpolation = code;
                return;

            case 36:
                if (_region is not null)
                    throw new FormatException("Вложенный регион G36.");

                _region = new PathGeometry
                {
                    FillRule = FillRule.EvenOdd
                };

                _contour = null;
                return;

            case 37:
                if (_region is null)
                    throw new FormatException("G37 без G36.");

                if (_region.Figures.Count == 0)
                    throw new FormatException("Пустой регион.");

                Geometry region = _region;

                _region = null;
                _contour = null;

                AddGeometry(region);
                return;

            case 54:
                // Старый префикс выбора апертуры.
                return;

            case 70:
                SetUnits(25.4);
                return;

            case 71:
                SetUnits(1);
                return;

            case 74:
                _multiQuadrant = false;
                return;

            case 75:
                _multiQuadrant = true;
                return;

            case 90:
                return;

            case 91:
                throw new NotSupportedException(
                    "Инкрементальные координаты G91 не поддерживаются.");

            default:
                throw new NotSupportedException(
                    $"G{code:D2} не поддерживается.");
        }
    }

    private double ReadCoordinate(string text, bool xAxis)
    {
        int integerDigits = xAxis ? _xInteger : _yInteger;
        int fractionDigits = xAxis ? _xFraction : _yFraction;

        // Некоторые генераторы записывают явную десятичную точку.
        if (text.Contains('.'))
        {
            double explicitValue = ParseNumber(text) * _unitFactor;

            if (!double.IsFinite(explicitValue))
            {
                throw new FormatException(
                    $"Числовое переполнение в координате '{text}'.");
            }

            return explicitValue;
        }

        if (text.Length == 0)
            throw new FormatException("Пустая координата.");

        bool negative = text[0] == '-';

        string digits = text[0] is '+' or '-'
            ? text[1..]
            : text;

        if (digits.Length == 0 ||
            digits.Any(c => c is < '0' or > '9'))
        {
            throw new FormatException(
                $"Неверная координата: '{text}'.");
        }

        int totalDigits = integerDigits + fractionDigits;

        if (digits.Length > totalDigits)
        {
            if (_trailingZeroSuppression)
            {
                // При подавлении конечных нулей не пытаемся
                // автоматически исправлять несоответствие формату.
                throw new FormatException(
                    $"Координата '{text}' длиннее формата FS " +
                    $"{integerDigits}.{fractionDigits} " +
                    "при подавлении конечных нулей.");
            }

            // Режим L: десятичная точка определяется количеством
            // дробных разрядов. Дополнительные цифры относятся
            // к целой части, а не отбрасываются справа.
            if (!_coordinateWidthWarningAdded)
            {
                char axis = xAxis ? 'X' : 'Y';

                AddWarning(
                    $"Координата {axis}{text} превышает объявленную " +
                    $"разрядность FS {integerDigits}.{fractionDigits}. " +
                    "Разрешены дополнительные целые разряды; " +
                    "число дробных разрядов сохранено. " +
                    "Проверьте формат координат исходного файла.");

                _coordinateWidthWarningAdded = true;
            }
        }

        if (_trailingZeroSuppression)
            digits = digits.PadRight(totalDigits, '0');

        double value = ParseNumber(digits);

        value /= Math.Pow(10, fractionDigits);

        if (negative)
            value = -value;

        value *= _unitFactor;

        if (!double.IsFinite(value))
        {
            throw new FormatException(
                $"Числовое переполнение в координате '{text}'.");
        }

        return value;
    }

    private List<Point> BuildArc(
        Point start,
        Point end,
        double i,
        double j)
    {
        double coordinateTolerance =
            2 * _unitFactor * Math.Max(
                Math.Pow(10, -_xFraction),
                Math.Pow(10, -_yFraction));

        double radiusTolerance =
            Math.Max(Tolerance, coordinateTolerance);

        Point center;

        if (_multiQuadrant)
        {
            // G75: I/J — знаковые смещения от начала дуги.
            center = new Point(
                start.X + i,
                start.Y + j);
        }
        else
        {
            center = FindSingleQuadrantCenter(
                start,
                end,
                i,
                j,
                radiusTolerance);
        }

        double radius = (start - center).Length;
        double endRadius = (end - center).Length;

        if (!double.IsFinite(radius) ||
            !double.IsFinite(endRadius) ||
            radius <= 0)
        {
            throw new FormatException("Неверный радиус дуги.");
        }

        if (Math.Abs(radius - endRadius) > radiusTolerance)
        {
            throw new FormatException(
                "Начало и конец дуги имеют разные радиусы.");
        }

        double startAngle = Math.Atan2(
            start.Y - center.Y,
            start.X - center.X);

        double sweep = GetArcSweep(start, end, center);

        int count = SegmentCount(radius, Math.Abs(sweep));

        var points = new List<Point>(count + 1)
    {
        start
    };

        for (int n = 1; n < count; n++)
        {
            double angle =
                startAngle + sweep * n / count;

            points.Add(new Point(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle)));
        }

        points.Add(end);

        return points;
    }

    private Point FindSingleQuadrantCenter(
        Point start,
        Point end,
        double i,
        double j,
        double radiusTolerance)
    {
        if (start == end)
        {
            throw new FormatException(
                "Полная окружность недопустима в режиме G74. " +
                "Для неё требуется G75.");
        }

        double absI = Math.Abs(i);
        double absJ = Math.Abs(j);

        Point? bestCenter = null;
        double bestError = double.PositiveInfinity;

        var visited = new HashSet<Point>();

        foreach (int signX in new[] { -1, 1 })
        {
            foreach (int signY in new[] { -1, 1 })
            {
                var candidate = new Point(
                    start.X + signX * absI,
                    start.Y + signY * absJ);

                if (!visited.Add(candidate))
                    continue;

                double radius = (start - candidate).Length;
                double endRadius = (end - candidate).Length;

                if (!double.IsFinite(radius) ||
                    !double.IsFinite(endRadius) ||
                    radius <= 0)
                {
                    continue;
                }

                double error = Math.Abs(radius - endRadius);

                if (error > radiusTolerance)
                    continue;

                double sweep = Math.Abs(
                    GetArcSweep(start, end, candidate));

                // Небольшой запас для округления координат.
                // Ограничиваем его, чтобы не принимать явно
                // многоквадрантные дуги на маленьких радиусах.
                double angularTolerance = Math.Min(
                    0.01,
                    radiusTolerance / radius);

                if (sweep > Math.PI / 2 + angularTolerance)
                    continue;

                if (error < bestError)
                {
                    bestError = error;
                    bestCenter = candidate;
                }
            }
        }

        return bestCenter
            ?? throw new FormatException(
                "Не удалось определить центр дуги G74: " +
                "проверьте I/J, направление и угол дуги.");
    }

    private double GetArcSweep(
        Point start,
        Point end,
        Point center)
    {
        double startAngle = Math.Atan2(
            start.Y - center.Y,
            start.X - center.X);

        double endAngle = Math.Atan2(
            end.Y - center.Y,
            end.X - center.X);

        double sweep = endAngle - startAngle;

        if (_interpolation == 2)
        {
            while (sweep >= 0)
                sweep -= Math.Tau;
        }
        else
        {
            while (sweep <= 0)
                sweep += Math.Tau;
        }

        return sweep;
    }


    private static int SegmentCount(double radius, double sweep)
    {
        // Ограничение угла также сохраняет форму очень маленьких окружностей.
        double maxAngle = Math.Min(
            Math.PI / 16,
            2 * Math.Acos(Math.Clamp(1 - Tolerance / radius, -1, 1)));

        if (!double.IsFinite(maxAngle) || maxAngle <= 0)
            throw new FormatException("Слишком большой радиус.");

        double count = Math.Ceiling(sweep / maxAngle);

        if (count > 65_536)
        {
            throw new NotSupportedException(
                "Слишком сложная дуга для текущего допуска аппроксимации.");
        }

        return Math.Max(1, (int)count);
    }

    private void AppendRegion(List<Point> points)
    {
        if (_region is null)
            throw new InvalidOperationException();

        if (_contour is null)
        {
            _contour = new PathFigure
            {
                StartPoint = points[0],
                IsClosed = true,
                IsFilled = true
            };

            _region.Figures.Add(_contour);
        }

        _contour.Segments.Add(
            new PolyLineSegment(points.Skip(1), true));
    }

    private Aperture GetSelectedAperture()
    {
        return _selectedAperture
            ?? throw new FormatException("Не выбрана апертура.");
    }

    private void EnsureOutsideRegion()
    {
        if (_region is not null)
        {
            throw new FormatException(
                "Команда недопустима внутри региона G36/G37.");
        }
    }

    private static Geometry BuildFlash(Aperture aperture, Point position)
    {
        if (aperture.MacroGeometry is Geometry macroGeometry)
        {
            // Не изменяем общую замороженную геометрию апертуры.
            // Для каждой вспышки создаём только обёртку с переносом.
            var flash = new GeometryGroup
            {
                FillRule = FillRule.Nonzero,
                Transform = new TranslateTransform(position.X, position.Y)
            };

            flash.Children.Add(macroGeometry);

            return flash;
        }

        Geometry outer;

        var rect = new Rect(
            -aperture.Width / 2,
            -aperture.Height / 2,
            aperture.Width,
            aperture.Height);

        switch (aperture.Kind)
        {
            case 'C':
                outer = new EllipseGeometry(
                    new Point(),
                    aperture.Width / 2,
                    aperture.Width / 2);
                break;

            case 'R':
                outer = new RectangleGeometry(rect);
                break;

            case 'O':
                double radius =
                    Math.Min(aperture.Width, aperture.Height) / 2;

                outer = new RectangleGeometry(rect, radius, radius);
                break;

            case 'P':
                outer = PolygonGeometry(ApertureOutline(aperture));
                break;

            default:
                throw new NotSupportedException();
        }

        if (aperture.HoleDiameter > 0)
        {
            var hole = new EllipseGeometry(
                new Point(),
                aperture.HoleDiameter / 2,
                aperture.HoleDiameter / 2);

            outer = Geometry.Combine(
                outer,
                hole,
                GeometryCombineMode.Exclude,
                null,
                Tolerance,
                ToleranceType.Absolute);
        }

        outer.Transform = new TranslateTransform(position.X, position.Y);

        return outer;
    }

    private Geometry BuildStroke(
        Aperture aperture,
        List<Point> points)
    {

        if (aperture.MacroGeometry is not null)
        {
            throw new NotSupportedException(
                "Рисование D01 макроапертурой не поддерживается. " +
                "Макроапертуры поддерживаются для вспышек D03.");
        }

        if (aperture.HoleDiameter > 0)
        {
            throw new NotSupportedException(
                "Рисование D01 апертурой с отверстием не поддерживается. " +
                "Вспышки D03 таких апертур поддерживаются.");
        }

        if (aperture.Kind == 'C')
        {
            if (points.All(p => (p - points[0]).Length < 1e-12))
                return BuildFlash(aperture, points[0]);

            var figure = new PathFigure
            {
                StartPoint = points[0],
                IsClosed = false,
                IsFilled = false
            };

            figure.Segments.Add(
                new PolyLineSegment(points.Skip(1), true));

            var path = new PathGeometry();
            path.Figures.Add(figure);

            var pen = new Pen(Brushes.Black, aperture.Width)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };

            return path.GetWidenedPathGeometry(
                pen,
                Tolerance,
                ToleranceType.Absolute);
        }

        if (_interpolation != 1)
        {
            throw new NotSupportedException(
                "Дуги D01 поддерживаются только круглой апертурой.");
        }

        // Для выпуклой апертуры линейный проход — выпуклая оболочка
        // её копий в начале и конце отрезка.
        List<Point> outline = ApertureOutline(aperture);
        var translated = new List<Point>(outline.Count * 2);

        Point start = points[0];
        Point end = points[^1];

        foreach (Point point in outline)
        {
            translated.Add(new Point(
                point.X + start.X,
                point.Y + start.Y));

            translated.Add(new Point(
                point.X + end.X,
                point.Y + end.Y));
        }

        return PolygonGeometry(ConvexHull(translated));
    }

    private static List<Point> ApertureOutline(Aperture aperture)
    {
        double halfWidth = aperture.Width / 2;
        double halfHeight = aperture.Height / 2;

        if (aperture.Kind == 'R')
        {
            return
            [
                new Point(-halfWidth, -halfHeight),
                new Point(halfWidth, -halfHeight),
                new Point(halfWidth, halfHeight),
                new Point(-halfWidth, halfHeight)
            ];
        }

        if (aperture.Kind == 'P')
        {
            var polygon = new List<Point>(aperture.Vertices);
            double rotation = aperture.Rotation * Math.PI / 180;

            for (int n = 0; n < aperture.Vertices; n++)
            {
                double angle =
                    rotation + Math.Tau * n / aperture.Vertices;

                polygon.Add(new Point(
                    halfWidth * Math.Cos(angle),
                    halfWidth * Math.Sin(angle)));
            }

            return polygon;
        }

        if (aperture.Kind == 'O')
        {
            double radius = Math.Min(halfWidth, halfHeight);

            int count = SegmentCount(radius, Math.Tau);

            // Кратно четырём: присутствуют крайние точки по обеим осям.
            count = (count + 3) / 4 * 4;

            double shiftX = Math.Max(0, halfWidth - radius);
            double shiftY = Math.Max(0, halfHeight - radius);

            var outline = new List<Point>(count);

            for (int n = 0; n < count; n++)
            {
                double angle = Math.Tau * n / count;
                double cos = Math.Cos(angle);
                double sin = Math.Sin(angle);

                outline.Add(new Point(
                    radius * cos + (cos >= 0 ? shiftX : -shiftX),
                    radius * sin + (sin >= 0 ? shiftY : -shiftY)));
            }

            return outline;
        }

        throw new NotSupportedException(
            "Не удалось построить контур апертуры.");
    }

    private static Geometry PolygonGeometry(IReadOnlyList<Point> points)
    {
        if (points.Count < 3)
            throw new FormatException("Недостаточно точек полигона.");

        var geometry = new StreamGeometry
        {
            FillRule = FillRule.Nonzero
        };

        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(
                points[0],
                isFilled: true,
                isClosed: true);

            context.PolyLineTo(
                points.Skip(1).ToArray(),
                isStroked: true,
                isSmoothJoin: false);
        }

        return geometry;
    }

    private static List<Point> ConvexHull(IEnumerable<Point> source)
    {
        List<Point> points = source
            .Distinct()
            .OrderBy(p => p.X)
            .ThenBy(p => p.Y)
            .ToList();

        if (points.Count < 3)
            return points;

        static double Cross(Point a, Point b, Point c)
        {
            return
                (b.X - a.X) * (c.Y - a.Y) -
                (b.Y - a.Y) * (c.X - a.X);
        }

        var lower = new List<Point>();

        foreach (Point point in points)
        {
            while (lower.Count >= 2 &&
                   Cross(lower[^2], lower[^1], point) <= 0)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(point);
        }

        var upper = new List<Point>();

        for (int n = points.Count - 1; n >= 0; n--)
        {
            Point point = points[n];

            while (upper.Count >= 2 &&
                   Cross(upper[^2], upper[^1], point) <= 0)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);

        lower.AddRange(upper);

        return lower;
    }

    private static double ParseNumber(string text)
    {
        if (!double.TryParse(
                text,
                NumberStyles.Float,
                Invariant,
                out double value) ||
            !double.IsFinite(value))
        {
            throw new FormatException($"Неверное число: '{text}'.");
        }

        return value;
    }

    private static int ParseInteger(string text)
    {
        if (!int.TryParse(
                text,
                NumberStyles.AllowLeadingSign,
                Invariant,
                out int value))
        {
            throw new FormatException($"Ожидалось целое число: '{text}'.");
        }

        return value;
    }
}
