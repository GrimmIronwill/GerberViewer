using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace GerberViewer;

public sealed partial class GerberParser
{
    private readonly Dictionary<string, MacroDefinition> _macros =
        new(StringComparer.Ordinal);

    private sealed record MacroDefinition(
        string Name,
        string[] Statements);

    private static readonly Regex MacroNameRegex = new(
        @"^[A-Za-z_$\.][A-Za-z0-9_$\.]*$",
        RegexOptions.Compiled);

    private static readonly Regex MacroAssignmentRegex = new(
        @"^\$(\d+)=(.+)$",
        RegexOptions.Compiled);

    private void ParseMacroDefinition(string text)
    {
        // Пример:
        // AMRING*1,1,$1,0,0*1,0,$2,0,0*
        string[] parts = text.Split('*');

        string header = parts[0].Trim();

        if (!header.StartsWith("AM", StringComparison.Ordinal))
            throw new FormatException("Неверный заголовок макроса.");

        string name = header[2..];

        if (name.Length is < 1 or > 127 ||
            !MacroNameRegex.IsMatch(name))
        {
            throw new FormatException(
                $"Неверное имя макроса: '{name}'.");
        }

        if (name is "C" or "R" or "O" or "P")
        {
            throw new FormatException(
                $"Имя '{name}' зарезервировано стандартной апертурой.");
        }

        var statements = new List<string>();

        for (int n = 1; n < parts.Length; n++)
        {
            string statement = parts[n].Trim();

            if (statement.Length == 0)
            {
                // После последнего '*' остаётся пустая часть.
                if (n == parts.Length - 1)
                    continue;

                throw new FormatException(
                    $"Пустой оператор в макросе '{name}'.");
            }

            statements.Add(statement);
        }

        if (statements.Count == 0)
        {
            throw new FormatException(
                $"Макрос '{name}' не содержит операторов.");
        }

        if (!_macros.TryAdd(
                name,
                new MacroDefinition(name, statements.ToArray())))
        {
            throw new FormatException(
                $"Макрос '{name}' уже определён.");
        }
    }

    private void ParseMacroAperture(
        int id,
        string name,
        string modifiers)
    {
        if (_apertures.ContainsKey(id))
        {
            throw new FormatException(
                $"Апертура D{id} уже определена.");
        }

        if (!_macros.TryGetValue(name, out MacroDefinition? macro))
        {
            throw new FormatException(
                $"Макрос апертуры '{name}' не определён.");
        }

        double[] parameters = modifiers.Length == 0
            ? []
            : modifiers
                .Split('X', StringSplitOptions.None)
                .Select(ParseNumber)
                .ToArray();

        // Вычисляем макрос один раз при ADD,
        // а не заново для каждой вспышки.
        Geometry geometry = BuildMacroGeometry(macro, parameters);

        geometry.Freeze();

        _apertures.Add(
            id,
            new Aperture(
                Kind: 'M',
                Width: 0,
                Height: 0,
                Vertices: 0,
                Rotation: 0,
                HoleDiameter: 0,
                MacroGeometry: geometry));
    }

    private Geometry BuildMacroGeometry(
        MacroDefinition macro,
        IReadOnlyList<double> parameters)
    {
        var variables = new Dictionary<int, double>();

        // Параметры ADD становятся $1, $2, ...
        // Они пока остаются в исходных единицах файла.
        for (int n = 0; n < parameters.Count; n++)
            variables.Add(n + 1, parameters[n]);

        Geometry result = Geometry.Empty;

        for (int n = 0; n < macro.Statements.Length; n++)
        {
            string raw = macro.Statements[n];

            try
            {
                // Примитив 0 — комментарий.
                // Проверяем до удаления пробелов.
                if (raw[0] == '0' &&
                    (raw.Length == 1 || char.IsWhiteSpace(raw[1])))
                {
                    continue;
                }

                string statement = RemoveWhitespace(raw);

                if (statement.StartsWith('$'))
                {
                    Match assignment =
                        MacroAssignmentRegex.Match(statement);

                    if (!assignment.Success)
                    {
                        throw new FormatException(
                            "Неверное присваивание переменной макроса.");
                    }

                    int index = ParseInteger(
                        assignment.Groups[1].Value);

                    if (index <= 0)
                    {
                        throw new FormatException(
                            "Номер переменной должен быть положительным.");
                    }

                    double value = EvaluateMacroExpression(
                        assignment.Groups[2].Value,
                        variables);

                    variables[index] = value;
                    continue;
                }

                string[] fields = statement.Split(',');

                int primitiveCode = ParseInteger(fields[0]);

                var values = new double[fields.Length - 1];

                for (int k = 1; k < fields.Length; k++)
                {
                    values[k - 1] = EvaluateMacroExpression(
                        fields[k],
                        variables);
                }

                var (geometry, dark) =
                    BuildMacroPrimitive(primitiveCode, values);

                // Экспозиция макроса работает только внутри макроса.
                // Порядок операторов менять нельзя.
                result = CombineMacroGeometry(
                    result,
                    geometry,
                    dark
                        ? GeometryCombineMode.Union
                        : GeometryCombineMode.Exclude);
            }
            catch (Exception ex) when (
                ex is FormatException or
                NotSupportedException or
                ArgumentException or
                OverflowException)
            {
                throw new FormatException(
                    $"Макрос '{macro.Name}', оператор №{n + 1}: " +
                    $"{raw}\n{ex.Message}",
                    ex);
            }
        }

        return result;
    }

    private static double EvaluateMacroExpression(
        string expression,
        IReadOnlyDictionary<int, double> variables)
    {
        return new MacroExpressionParser(
            expression,
            variables).Parse();
    }

    private (Geometry Geometry, bool Dark) BuildMacroPrimitive(
        int code,
        double[] p)
    {
        switch (code)
        {
            // 1: круг
            // exposure, diameter, centerX, centerY [, rotation]
            case 1:
                {
                    RequireMacroParameterCount(p, 4, 5);

                    bool dark = ReadMacroExposure(p[0]);
                    double diameter = MacroDimension(p[1]);

                    var center = new Point(
                        MacroMm(p[2]),
                        MacroMm(p[3]));

                    Geometry geometry = diameter == 0
                        ? Geometry.Empty
                        : new EllipseGeometry(
                            center,
                            diameter / 2,
                            diameter / 2);

                    double rotation = p.Length == 5 ? p[4] : 0;

                    return (RotateMacroGeometry(geometry, rotation), dark);
                }

            // 2 — устаревший вариант 20.
            //
            // 20: векторная линия с плоскими торцами
            // exposure, width, startX, startY, endX, endY, rotation
            case 2:
            case 20:
                {
                    RequireMacroParameterCount(p, 7, 7);

                    bool dark = ReadMacroExposure(p[0]);
                    double width = MacroDimension(p[1]);

                    var start = new Point(
                        MacroMm(p[2]),
                        MacroMm(p[3]));

                    var end = new Point(
                        MacroMm(p[4]),
                        MacroMm(p[5]));

                    Vector delta = end - start;
                    double length = delta.Length;

                    if (!double.IsFinite(length))
                        throw new FormatException("Слишком длинная линия.");

                    Geometry geometry;

                    if (width == 0 || length == 0)
                    {
                        geometry = Geometry.Empty;
                    }
                    else
                    {
                        var normal = new Vector(
                            -delta.Y / length * width / 2,
                            delta.X / length * width / 2);

                        geometry = PolygonGeometry(
                            new Point[]
                            {
                            start + normal,
                            end + normal,
                            end - normal,
                            start - normal
                            });
                    }

                    return (RotateMacroGeometry(geometry, p[6]), dark);
                }

            // 21: прямоугольник, заданный центром
            // exposure, width, height, centerX, centerY, rotation
            //
            // 22: устаревший прямоугольник,
            // заданный нижним левым углом.
            case 21:
            case 22:
                {
                    RequireMacroParameterCount(p, 6, 6);

                    bool dark = ReadMacroExposure(p[0]);
                    double width = MacroDimension(p[1]);
                    double height = MacroDimension(p[2]);

                    double x = MacroMm(p[3]);
                    double y = MacroMm(p[4]);

                    if (code == 21)
                    {
                        x -= width / 2;
                        y -= height / 2;
                    }

                    Geometry geometry =
                        width == 0 || height == 0
                            ? Geometry.Empty
                            : new RectangleGeometry(
                                new Rect(x, y, width, height));

                    return (RotateMacroGeometry(geometry, p[5]), dark);
                }

            // 4: замкнутый контур
            //
            // exposure, numberOfVertices,
            // startX, startY,
            // x1, y1, ... xN, yN,
            // rotation
            //
            // Последняя точка должна повторять первую.
            case 4:
                {
                    if (p.Length < 2)
                        throw new FormatException("Неполный примитив 4.");

                    bool dark = ReadMacroExposure(p[0]);

                    int vertices = MacroInteger(
                        p[1],
                        3,
                        5000,
                        "Число вершин контура");

                    int expected = 2 * vertices + 5;

                    RequireMacroParameterCount(p, expected, expected);

                    var points = new List<Point>(vertices + 1);

                    for (int n = 0; n <= vertices; n++)
                    {
                        points.Add(new Point(
                            MacroMm(p[2 + n * 2]),
                            MacroMm(p[3 + n * 2])));
                    }

                    // Малый запас на арифметику выражений.
                    if ((points[0] - points[^1]).Length > 1e-9)
                    {
                        throw new FormatException(
                            "Последняя точка примитива 4 " +
                            "должна совпадать с первой.");
                    }

                    points.RemoveAt(points.Count - 1);

                    Geometry geometry = PolygonGeometry(points);

                    return (RotateMacroGeometry(geometry, p[^1]), dark);
                }

            // 5: правильный многоугольник
            // exposure, vertices, centerX, centerY,
            // circumscribedDiameter, rotation
            case 5:
                {
                    RequireMacroParameterCount(p, 6, 6);

                    bool dark = ReadMacroExposure(p[0]);

                    int vertices = MacroInteger(
                        p[1],
                        3,
                        12,
                        "Число вершин многоугольника");

                    double centerX = MacroMm(p[2]);
                    double centerY = MacroMm(p[3]);
                    double diameter = MacroDimension(p[4]);

                    if (diameter == 0)
                        return (Geometry.Empty, dark);

                    double radius = diameter / 2;
                    var points = new List<Point>(vertices);

                    for (int n = 0; n < vertices; n++)
                    {
                        double angle = Math.Tau * n / vertices;

                        points.Add(new Point(
                            centerX + radius * Math.Cos(angle),
                            centerY + radius * Math.Sin(angle)));
                    }

                    Geometry geometry = PolygonGeometry(points);

                    return (RotateMacroGeometry(geometry, p[5]), dark);
                }

            // 6: муар — устаревший примитив.
            // Отдельного параметра exposure нет: всегда dark.
            //
            // centerX, centerY, outerDiameter,
            // ringThickness, ringGap, maximumRings,
            // crosshairThickness, crosshairLength, rotation
            case 6:
                {
                    RequireMacroParameterCount(p, 9, 9);

                    double centerX = MacroMm(p[0]);
                    double centerY = MacroMm(p[1]);
                    double outerDiameter = MacroDimension(p[2]);
                    double ringThickness = MacroDimension(p[3]);
                    double ringGap = MacroDimension(p[4]);

                    // Защитный лимит сложности реализации.
                    int maximumRings = MacroInteger(
                        p[5],
                        0,
                        10_000,
                        "Максимальное число колец");

                    double crossThickness = MacroDimension(p[6]);
                    double crossLength = MacroDimension(p[7]);

                    if (maximumRings > 0 &&
                        outerDiameter > 0 &&
                        ringThickness == 0)
                    {
                        throw new FormatException(
                            "Толщина колец муара должна быть больше нуля.");
                    }

                    var center = new Point(centerX, centerY);
                    Geometry geometry = Geometry.Empty;

                    double radius = outerDiameter / 2;

                    for (int n = 0; n < maximumRings && radius > 0; n++)
                    {
                        Geometry ring = new EllipseGeometry(
                            center,
                            radius,
                            radius);

                        double innerRadius = radius - ringThickness;

                        if (innerRadius > 0)
                        {
                            ring = CombineMacroGeometry(
                                ring,
                                new EllipseGeometry(
                                    center,
                                    innerRadius,
                                    innerRadius),
                                GeometryCombineMode.Exclude);
                        }

                        geometry = CombineMacroGeometry(
                            geometry,
                            ring,
                            GeometryCombineMode.Union);

                        radius -= ringThickness + ringGap;
                    }

                    if (crossThickness > 0 && crossLength > 0)
                    {
                        var horizontal = new RectangleGeometry(
                            new Rect(
                                centerX - crossLength / 2,
                                centerY - crossThickness / 2,
                                crossLength,
                                crossThickness));

                        var vertical = new RectangleGeometry(
                            new Rect(
                                centerX - crossThickness / 2,
                                centerY - crossLength / 2,
                                crossThickness,
                                crossLength));

                        geometry = CombineMacroGeometry(
                            geometry,
                            horizontal,
                            GeometryCombineMode.Union);

                        geometry = CombineMacroGeometry(
                            geometry,
                            vertical,
                            GeometryCombineMode.Union);
                    }

                    return (RotateMacroGeometry(geometry, p[8]), true);
                }

            // 7: тепловой барьер.
            // Отдельного параметра exposure нет: всегда dark.
            //
            // centerX, centerY, outerDiameter,
            // innerDiameter, gapThickness, rotation
            case 7:
                {
                    RequireMacroParameterCount(p, 6, 6);

                    double centerX = MacroMm(p[0]);
                    double centerY = MacroMm(p[1]);
                    double outerDiameter = MacroDimension(p[2]);
                    double innerDiameter = MacroDimension(p[3]);
                    double gap = MacroDimension(p[4]);

                    if (outerDiameter <= 0 ||
                        innerDiameter >= outerDiameter)
                    {
                        throw new FormatException(
                            "В тепловом барьере внешний диаметр должен " +
                            "быть положительным и больше внутреннего.");
                    }

                    if (gap >= outerDiameter / Math.Sqrt(2))
                    {
                        throw new FormatException(
                            "Зазор теплового барьера слишком большой.");
                    }

                    var center = new Point(centerX, centerY);

                    Geometry geometry = new EllipseGeometry(
                        center,
                        outerDiameter / 2,
                        outerDiameter / 2);

                    if (innerDiameter > 0)
                    {
                        geometry = CombineMacroGeometry(
                            geometry,
                            new EllipseGeometry(
                                center,
                                innerDiameter / 2,
                                innerDiameter / 2),
                            GeometryCombineMode.Exclude);
                    }

                    if (gap > 0)
                    {
                        var horizontalGap = new RectangleGeometry(
                            new Rect(
                                centerX - outerDiameter / 2,
                                centerY - gap / 2,
                                outerDiameter,
                                gap));

                        var verticalGap = new RectangleGeometry(
                            new Rect(
                                centerX - gap / 2,
                                centerY - outerDiameter / 2,
                                gap,
                                outerDiameter));

                        geometry = CombineMacroGeometry(
                            geometry,
                            horizontalGap,
                            GeometryCombineMode.Exclude);

                        geometry = CombineMacroGeometry(
                            geometry,
                            verticalGap,
                            GeometryCombineMode.Exclude);
                    }

                    return (RotateMacroGeometry(geometry, p[5]), true);
                }

            default:
                throw new NotSupportedException(
                    $"Примитив макроса {code} не поддерживается.");
        }
    }

    private double MacroMm(double value)
    {
        double result = value * _unitFactor;

        if (!double.IsFinite(result))
        {
            throw new FormatException(
                "Числовое переполнение в координате макроса.");
        }

        return result;
    }

    private double MacroDimension(double value)
    {
        double result = MacroMm(value);

        if (result < 0)
        {
            throw new FormatException(
                "Размер примитива макроса не может быть отрицательным.");
        }

        return result;
    }

    private static bool ReadMacroExposure(double value)
    {
        return value switch
        {
            0 => false,
            1 => true,

            _ => throw new FormatException(
                "Экспозиция примитива макроса должна быть 0 или 1.")
        };
    }

    private static int MacroInteger(
        double value,
        int min,
        int max,
        string name)
    {
        if (!double.IsFinite(value) ||
            value != Math.Truncate(value) ||
            value < min ||
            value > max)
        {
            throw new FormatException(
                $"{name}: ожидается целое число от {min} до {max}.");
        }

        return (int)value;
    }

    private static void RequireMacroParameterCount(
        double[] parameters,
        int min,
        int max)
    {
        if (parameters.Length < min || parameters.Length > max)
        {
            string expected = min == max
                ? min.ToString(Invariant)
                : $"{min}–{max}";

            throw new FormatException(
                $"Неверное число параметров примитива макроса. " +
                $"Ожидалось: {expected}; получено: {parameters.Length}.");
        }
    }

    private static Geometry RotateMacroGeometry(
        Geometry geometry,
        double angle)
    {
        angle %= 360;

        if (angle == 0)
            return geometry;

        // Поворот вокруг начала координат макроса (0, 0),
        // а не вокруг собственного центра примитива.
        var rotated = new GeometryGroup
        {
            FillRule = FillRule.Nonzero,
            Transform = new RotateTransform(angle)
        };

        rotated.Children.Add(geometry);

        return rotated;
    }

    private static Geometry CombineMacroGeometry(
        Geometry left,
        Geometry right,
        GeometryCombineMode mode)
    {
        return Geometry.Combine(
            left,
            right,
            mode,
            null,
            Tolerance,
            ToleranceType.Absolute);
    }

    // Парсер арифметики макросов.
    //
    // Приоритеты:
    // 1. Скобки, переменные, числа
    // 2. Унарные + и -
    // 3. x, X, /
    // 4. + и -
    //
    // Символ '*' не является умножением:
    // в Gerber он завершает оператор макроса.
    private sealed class MacroExpressionParser
    {
        private const int MaximumDepth = 64;

        private readonly string _text;
        private readonly IReadOnlyDictionary<int, double> _variables;

        private int _position;
        private int _depth;

        public MacroExpressionParser(
            string text,
            IReadOnlyDictionary<int, double> variables)
        {
            _text = text;
            _variables = variables;
        }

        public double Parse()
        {
            double result = ParseSum();

            SkipWhitespace();

            if (_position != _text.Length)
            {
                throw Error(
                    $"Неожиданный символ '{_text[_position]}'.");
            }

            return Finite(result);
        }

        private double ParseSum()
        {
            double value = ParseProduct();

            while (true)
            {
                if (Take('+'))
                {
                    value = Finite(value + ParseProduct());
                }
                else if (Take('-'))
                {
                    value = Finite(value - ParseProduct());
                }
                else
                {
                    return value;
                }
            }
        }

        private double ParseProduct()
        {
            double value = ParseUnary();

            while (true)
            {
                if (Take('x') || Take('X'))
                {
                    value = Finite(value * ParseUnary());
                }
                else if (Take('/'))
                {
                    double divisor = ParseUnary();

                    if (divisor == 0)
                        throw Error("Деление на ноль.");

                    value = Finite(value / divisor);
                }
                else
                {
                    return value;
                }
            }
        }

        private double ParseUnary()
        {
            _depth++;

            if (_depth > MaximumDepth)
            {
                throw Error(
                    "Слишком большая вложенность выражения.");
            }

            try
            {
                if (Take('+'))
                    return ParseUnary();

                if (Take('-'))
                    return Finite(-ParseUnary());

                return ParsePrimary();
            }
            finally
            {
                _depth--;
            }
        }

        private double ParsePrimary()
        {
            if (Take('('))
            {
                double value = ParseSum();

                if (!Take(')'))
                    throw Error("Не найдена закрывающая скобка.");

                return value;
            }

            if (Take('$'))
            {
                int start = _position;

                while (_position < _text.Length &&
                       IsDigit(_text[_position]))
                {
                    _position++;
                }

                if (start == _position)
                    throw Error("После '$' ожидается номер переменной.");

                int index = ParseInteger(_text[start.._position]);

                if (index <= 0)
                {
                    throw Error(
                        "Номер переменной должен быть положительным.");
                }

                // Неинициализированные переменные имеют значение 0.
                return _variables.TryGetValue(index, out double value)
                    ? value
                    : 0;
            }

            SkipWhitespace();

            int numberStart = _position;
            bool hasDigits = false;

            while (_position < _text.Length &&
                   IsDigit(_text[_position]))
            {
                hasDigits = true;
                _position++;
            }

            if (_position < _text.Length &&
                _text[_position] == '.')
            {
                _position++;

                while (_position < _text.Length &&
                       IsDigit(_text[_position]))
                {
                    hasDigits = true;
                    _position++;
                }
            }

            if (!hasDigits)
                throw Error("Ожидалось число, переменная или скобка.");

            return ParseNumber(_text[numberStart.._position]);
        }

        private bool Take(char expected)
        {
            SkipWhitespace();

            if (_position >= _text.Length ||
                _text[_position] != expected)
            {
                return false;
            }

            _position++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length &&
                   char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }

        private static bool IsDigit(char value)
        {
            return value is >= '0' and <= '9';
        }

        private double Finite(double value)
        {
            if (!double.IsFinite(value))
                throw Error("Числовое переполнение.");

            return value;
        }

        private FormatException Error(string message)
        {
            return new FormatException(
                $"Выражение '{_text}', позиция {_position + 1}: " +
                message);
        }
    }
}
