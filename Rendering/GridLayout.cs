using System.Drawing;
using OpenWigi.Sdk;

namespace OpenWigiDash.ObsPlugin.Rendering;

/// <summary>
/// Geometria do painel de cenas, a mesma para desenhar e para achar a célula do toque. Só conta, não desenha
/// (roda em qualquer sistema).
/// </summary>
public static class GridLayout
{
    public const float Pad = 8, Gap = 6;

    /// <summary>Colunas e linhas que cabem: Large (400x290) dá 3x4, a tela cheia do WigiDash dá 6x7.</summary>
    public static (int Columns, int Rows) Shape(PixelSize size) =>
        (Math.Clamp((int)Math.Round(size.Width / 135.0), 2, 6), Math.Clamp((int)Math.Round(size.Height / 72.0), 2, 7));

    public static RectangleF Cell(PixelSize size, int columns, int rows, int index)
    {
        var w = (size.Width - Pad * 2 - Gap * (columns - 1)) / columns;
        var h = (size.Height - Pad * 2 - Gap * (rows - 1)) / rows;
        var column = index % columns;
        var row = index / columns;
        return new RectangleF(Pad + column * (w + Gap), Pad + row * (h + Gap), w, h);
    }

    /// <summary>A célula sob o ponto, contando metade do espaço entre elas; <c>null</c> fora da grade.</summary>
    public static int? HitTest(PixelSize size, int columns, int rows, int x, int y)
    {
        for (var i = 0; i < columns * rows; i++)
        {
            var cell = RectangleF.Inflate(Cell(size, columns, rows, i), Gap / 2, Gap / 2);
            if (cell.Contains(x, y))
                return i;
        }
        return null;
    }

    /// <summary>
    /// Quais itens vão em cada página: cabendo, todos; senão, a última célula vira "mais" e cada página leva
    /// <c>células - 1</c> itens.
    /// </summary>
    public static (int Start, int Count, bool More, int Pages) Page(int items, int cells, int page)
    {
        if (items <= cells)
            return (0, items, false, 1);
        var perPage = Math.Max(1, cells - 1);
        var pages = (items + perPage - 1) / perPage;
        page = ((page % pages) + pages) % pages;
        var start = page * perPage;
        return (start, Math.Min(perPage, items - start), true, pages);
    }
}
