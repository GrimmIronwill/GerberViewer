using System.Windows.Media;

namespace GerberViewer;

public sealed partial class GerberParser
{
    // Ограничиваем размер пакета, чтобы не держать
    // все исходные примитивы большого файла одновременно.
    private const int ImageBatchSize = 256;

    private readonly List<Geometry> _imageBatch = [];

    private bool _imageBatchDark;

    private void AddGeometry(Geometry geometry)
    {
        if (_imageBatch.Count > 0 &&
            _imageBatchDark != _dark)
        {
            // Сначала применяем все предыдущие операции.
            // Только после этого начинаем другую полярность.
            FlushImageBatch();
        }

        if (_imageBatch.Count == 0)
            _imageBatchDark = _dark;

        _imageBatch.Add(geometry);
        _primitiveCount++;

        if (_imageBatch.Count >= ImageBatchSize)
            FlushImageBatch();
    }

    private void FlushImageBatch()
    {
        if (_imageBatch.Count == 0)
            return;

        // И dark-, и clear-пакет сначала представляют
        // объединением всех геометрий своего пакета.
        Geometry batch = UnionBalanced(
            _imageBatch,
            0,
            _imageBatch.Count);

        if (ReferenceEquals(_image, Geometry.Empty))
        {
            // Добавление к пустому изображению.
            // Вычитание из пустого изображения ничего не меняет.
            if (_imageBatchDark)
                _image = batch;
        }
        else
        {
            _image = Geometry.Combine(
                _image,
                batch,
                _imageBatchDark
                    ? GeometryCombineMode.Union
                    : GeometryCombineMode.Exclude,
                null,
                Tolerance,
                ToleranceType.Absolute);
        }

        _imageBatch.Clear();
    }

    private static Geometry UnionBalanced(
        IReadOnlyList<Geometry> geometries,
        int start,
        int count)
    {
        if (count == 1)
            return geometries[start];

        int leftCount = count / 2;

        Geometry left = UnionBalanced(
            geometries,
            start,
            leftCount);

        Geometry right = UnionBalanced(
            geometries,
            start + leftCount,
            count - leftCount);

        return Geometry.Combine(
            left,
            right,
            GeometryCombineMode.Union,
            null,
            Tolerance,
            ToleranceType.Absolute);
    }
}
