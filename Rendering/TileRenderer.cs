using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenWigi.Sdk;

namespace OpenWigiDash.ObsPlugin.Rendering;

/// <summary>
/// Desenha um <see cref="TileVisual"/> na área de conteúdo (o host pinta moldura e canto). As medidas partem do
/// Small (198x143) e escalam com o tamanho; Mini (altura abaixo de 100) mostra só ícone e um texto curto.
/// </summary>
public static class TileRenderer
{
    private const float RefWidth = 198, RefHeight = 143;
    private static readonly ConditionalWeakTable<byte[], Bitmap> Thumbnails = new();

    public static WidgetFrame Render(PixelSize size, WidgetTheme theme, TileVisual visual)
    {
        using var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var fonts = new Fonts(theme);
            var background = visual.Accent is { } accent && visual.Tinted && visual.Layout != TileLayout.Offline
                ? TilePalette.Tint(accent, visual.Layout == TileLayout.Standard && visual.Ring is null ? 0.24 : 0.18)
                : TilePalette.Background;
            var clear = visual.Layout == TileLayout.Offline ? Color.Black : background;
            g.Clear(clear);
            // O símbolo do OBS grande no canto, um tom acima do fundo, como o do Discord: marca o tile como do OBS
            // sem brigar com a informação. Acompanha a cor do estado porque sai do próprio fundo.
            DrawLogo(g, size, TilePalette.Blend(clear, Color.White, visual.Layout == TileLayout.Offline ? 0.05 : 0.075));

            var mini = size.Height < 100;
            var wide = size.Width >= size.Height * 2.2f;
            switch (visual.Layout)
            {
                case TileLayout.Offline:
                    DrawOffline(g, size, fonts, visual, mini);
                    break;
                case TileLayout.Grid:
                    DrawGrid(g, size, fonts, visual);
                    break;
                case TileLayout.Catalog:
                    DrawCatalog(g, size, fonts, visual, background);
                    break;
                case TileLayout.Stats when !mini:
                    DrawStats(g, size, fonts, visual, background, wide);
                    break;
                case TileLayout.Thumbnail when !mini:
                    DrawThumbnail(g, size, fonts, visual, background);
                    break;
                case TileLayout.Mixer when !mini:
                    if (wide)
                        DrawMixerWide(g, size, fonts, visual, background);
                    else
                        DrawStandardSmall(g, size, fonts, visual, background);
                    break;
                default:
                    if (mini)
                        DrawMini(g, size, fonts, visual, background);
                    else if (wide)
                        DrawStandardWide(g, size, fonts, visual, background);
                    else
                        DrawStandardSmall(g, size, fonts, visual, background);
                    break;
            }

            if (visual.Ring is { } ring)
            {
                using var pen = new Pen(ring, 3);
                g.DrawRectangle(pen, 1.5f, 1.5f, size.Width - 3, size.Height - 3);
            }
            if (visual.Press is { } press and > 0)
            {
                using var brush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
                g.FillRectangle(brush, 0, size.Height - 4, (float)(size.Width * Math.Clamp(press, 0, 1)), 4);
            }
        }
        return ToFrame(bitmap);
    }

    // ---- layouts ----

    /// <summary>Ícone do catálogo: ícone no meio e nome curto embaixo, nas mesmas proporções do Discord.</summary>
    private static void DrawCatalog(Graphics g, PixelSize size, Fonts fonts, TileVisual v, Color background)
    {
        var compact = size.Height < 100;
        var labelHeight = compact ? Math.Max(12, size.Height / 5f) : Math.Max(16, size.Height / 7f);
        var hasLabel = !string.IsNullOrEmpty(v.Big);
        var textBlock = hasLabel ? labelHeight + (compact ? 4 : 8) : 0;
        var top = compact ? 3f : 6f;
        var iconArea = new RectangleF(0, top, size.Width, size.Height - textBlock - top);
        // O traço visível ocupa até 95% da altura livre e 72% da largura, como o peso dos ícones do Discord. Os desenhos
        // têm folga de uns 3/24 de cada lado, então a caixa passa do visível em 24/18 (a folga pode sair da área).
        var side = Math.Min(iconArea.Height * 0.95f, size.Width * 0.72f) * 24f / 18f;
        ObsGlyphs.Draw(g, v.Icon, new RectangleF((size.Width - side) / 2, iconArea.Y + (iconArea.Height - side) / 2, side, side),
            TilePalette.Ink, background: background);
        if (hasLabel)
        {
            using var font = fonts.Semibold(labelHeight * 0.8f);
            DrawText(g, v.Big!, font, TilePalette.Ink, new RectangleF(4, size.Height - textBlock, size.Width - 8, labelHeight + 2), StringAlignment.Center);
        }
    }

    private static void DrawMini(Graphics g, PixelSize size, Fonts fonts, TileVisual v, Color background)
    {
        var hasText = !string.IsNullOrEmpty(v.MiniText);
        var iconSide = Math.Min(size.Height * (hasText ? 0.42f : 0.5f), size.Width * 0.45f);
        var textHeight = hasText ? size.Height * 0.2f : 0;
        var top = (size.Height - iconSide - textHeight - (hasText ? 3 : 0)) / 2;
        ObsGlyphs.Draw(g, v.Icon, new RectangleF((size.Width - iconSide) / 2, top, iconSide, iconSide), IconColor(v), v.Slashed, background);
        if (hasText)
        {
            using var font = fonts.Bold(Math.Max(11f, size.Height * 0.17f));
            DrawText(g, v.MiniText!, font, TilePalette.Ink, new RectangleF(2, top + iconSide + 2, size.Width - 4, textHeight + 4), StringAlignment.Center);
        }
    }

    private static void DrawStandardSmall(Graphics g, PixelSize size, Fonts fonts, TileVisual v, Color background)
    {
        var k = Math.Min(size.Width / RefWidth, size.Height / RefHeight);
        var padX = 14 * k;
        var padY = 12 * k;
        var iconSide = 24 * k;

        // Rótulo (ou pílula) e ícone na linha de cima.
        var headerHeight = iconSide;
        DrawCaption(g, fonts, v, new RectangleF(padX, padY, size.Width - padX * 2 - iconSide - 6, headerHeight), k);
        ObsGlyphs.Draw(g, v.Icon, new RectangleF(size.Width - padX - iconSide, padY, iconSide, iconSide), IconColor(v), v.Slashed, background);

        // Valor grande no meio.
        if (!string.IsNullOrEmpty(v.Big))
        {
            var em = BigSize(v.Big!, size.Width - padX * 2, k, fonts, g);
            using var font = fonts.Bold(em);
            var middle = new RectangleF(padX - 2 * k, padY + headerHeight, size.Width - padX * 2 + 4 * k,
                size.Height - padY * 2 - headerHeight - 18 * k);
            DrawText(g, v.Big!, font, v.BigDim ? TilePalette.Dim : TilePalette.Ink, middle, StringAlignment.Near);
        }

        // Linha de baixo: medidor ou detalhe.
        var bottom = new RectangleF(padX, size.Height - padY - 16 * k, size.Width - padX * 2, 16 * k);
        if (v.Meter is { } level)
            DrawMeter(g, new RectangleF(bottom.X, bottom.Bottom - 8 * k, bottom.Width, 8 * k), level, v.Slashed);
        else if (v.Progress is not null || v.ProgressText is not null)
            DrawProgress(g, fonts, v, bottom, k);
        else if (!string.IsNullOrEmpty(v.Detail))
        {
            using var font = fonts.Regular(Math.Max(11f, 12.5f * k));
            DrawText(g, v.Detail!, font, TilePalette.Dim, bottom, StringAlignment.Near);
        }
    }

    private static void DrawStandardWide(Graphics g, PixelSize size, Fonts fonts, TileVisual v, Color background)
    {
        var k = size.Height / RefHeight;
        var pad = 18 * k;
        var circle = Math.Min(76 * k, size.Height - 24 * k);
        var circleRect = new RectangleF(pad, (size.Height - circle) / 2, circle, circle);
        var active = v.Accent is not null && v.Tinted;
        using (var brush = new SolidBrush(active ? TilePalette.Tint(v.Accent!.Value, 0.45) : TilePalette.Panel))
            g.FillEllipse(brush, circleRect);
        var iconSide = circle * 0.53f;
        ObsGlyphs.Draw(g, v.Icon, RectangleF.Inflate(circleRect, -(circle - iconSide) / 2, -(circle - iconSide) / 2),
            v.PulseDim ? Color.FromArgb(110, Color.White) : active ? Color.White : TilePalette.IconRest, v.Slashed,
            active ? TilePalette.Tint(v.Accent!.Value, 0.45) : TilePalette.Panel);

        var x = circleRect.Right + 18 * k;
        var right = size.Width - pad;
        if (v.Spark is { Count: > 1 } spark)
        {
            var sparkRect = new RectangleF(right - 92 * k, 16 * k, 92 * k, 30 * k);
            DrawSpark(g, sparkRect, spark, v.SparkColor ?? TilePalette.Ok);
        }
        var width = right - x;
        var captionHeight = 18 * k;
        var bigEm = !string.IsNullOrEmpty(v.Big) ? BigSize(v.Big!, width, k * 1.12f, fonts, g) : 0;
        var bigHeight = bigEm * 1.15f;
        var hasProgress = v.Progress is not null || v.ProgressText is not null;
        var detailHeight = string.IsNullOrEmpty(v.Detail) && !hasProgress ? 0 : 18 * k;
        var gap = 7 * k;
        var total = captionHeight + gap + bigHeight + (detailHeight > 0 ? gap + detailHeight : 0);
        var y = (size.Height - total) / 2;

        DrawCaption(g, fonts, v, new RectangleF(x, y, width - (v.Spark is { Count: > 1 } ? 96 * k : 0), captionHeight), k);
        y += captionHeight + gap;
        if (bigEm > 0)
        {
            using var font = fonts.Bold(bigEm);
            DrawText(g, v.Big!, font, v.BigDim ? TilePalette.Dim : TilePalette.Ink, new RectangleF(x - 2 * k, y, width + 4 * k, bigHeight), StringAlignment.Near);
            y += bigHeight + gap;
        }
        if (hasProgress)
        {
            DrawProgress(g, fonts, v, new RectangleF(x, y, width, detailHeight), k);
        }
        else if (detailHeight > 0)
        {
            using var font = fonts.Regular(Math.Max(11f, 12.5f * k));
            var detailRect = new RectangleF(x, y, width, detailHeight);
            if (v.SparkColor is { } health)
            {
                var dot = 8 * k;
                using (var brush = new SolidBrush(health))
                    g.FillEllipse(brush, x, y + (detailHeight - dot) / 2, dot, dot);
                detailRect = detailRect with { X = x + dot + 6 * k, Width = width - dot - 6 * k };
            }
            DrawText(g, v.Detail!, font, TilePalette.Dim, detailRect, StringAlignment.Near);
        }
    }

    private static void DrawMixerWide(Graphics g, PixelSize size, Fonts fonts, TileVisual v, Color background)
    {
        var k = size.Height / RefHeight;
        var side = 40 * k;
        using (var hintFont = fonts.Regular(26 * k))
        {
            DrawText(g, "−", hintFont, TilePalette.Dim, new RectangleF(6 * k, 0, side, size.Height), StringAlignment.Center);
            DrawText(g, "+", hintFont, TilePalette.Dim, new RectangleF(size.Width - side - 6 * k, 0, side, size.Height), StringAlignment.Center);
        }
        var x = side + 14 * k;
        var width = size.Width - x * 2;
        var rowHeight = 28 * k;
        var meterHeight = 12 * k;
        var detailHeight = string.IsNullOrEmpty(v.Detail) ? 0 : 16 * k;
        var gap = 9 * k;
        var total = rowHeight + gap + meterHeight + (detailHeight > 0 ? gap + detailHeight : 0);
        var y = (size.Height - total) / 2;

        var iconSide = 26 * k;
        ObsGlyphs.Draw(g, v.Icon, new RectangleF(x, y + (rowHeight - iconSide) / 2, iconSide, iconSide), IconColor(v), v.Slashed, background);
        using (var dbFont = fonts.Bold(24 * k))
        {
            var dbWidth = string.IsNullOrEmpty(v.Big) ? 0 : g.MeasureString(v.Big, dbFont).Width;
            DrawText(g, v.Big ?? "", dbFont, v.Slashed ? TilePalette.Record : TilePalette.Ink,
                new RectangleF(x + width - dbWidth, y, dbWidth + 2, rowHeight), StringAlignment.Far);
            using var nameFont = fonts.Semibold(15 * k);
            DrawText(g, v.Caption ?? "", nameFont, TilePalette.Ink,
                new RectangleF(x + iconSide + 10 * k, y, width - iconSide - 10 * k - dbWidth - 8 * k, rowHeight), StringAlignment.Near);
        }
        y += rowHeight + gap;
        DrawMeter(g, new RectangleF(x, y, width, meterHeight), v.Meter ?? 0, v.Slashed);
        y += meterHeight + gap;
        if (detailHeight > 0)
        {
            using var font = fonts.Regular(Math.Max(11f, 12.5f * k));
            DrawText(g, v.Detail!, font, TilePalette.Dim, new RectangleF(x, y, width, detailHeight), StringAlignment.Near);
        }
    }

    private static void DrawThumbnail(Graphics g, PixelSize size, Fonts fonts, TileVisual v, Color background)
    {
        var k = Math.Min(size.Width / 400f, size.Height / RefHeight);
        k = Math.Max(k, 0.8f);
        if (Decode(v.Thumbnail) is { } image)
        {
            lock (image)
            {
                // Preenche o tile (corta as bordas que sobram), como "cover" no CSS.
                var scale = Math.Max(size.Width / (float)image.Width, size.Height / (float)image.Height);
                var w = image.Width * scale;
                var h = image.Height * scale;
                g.DrawImage(image, (size.Width - w) / 2, (size.Height - h) / 2, w, h);
            }
        }
        else
        {
            ObsGlyphs.Draw(g, ObsIcon.Scene, new RectangleF(size.Width / 2f - 24 * k, size.Height / 2f - 34 * k, 48 * k, 48 * k), TilePalette.Faint);
        }

        // Faixa escura embaixo para o nome ficar legível sobre qualquer imagem.
        var band = Math.Min(size.Height * 0.55f, 70 * k);
        using (var shade = new LinearGradientBrush(new RectangleF(0, size.Height - band - 1, size.Width, band + 2),
            Color.FromArgb(0, 0, 0, 0), Color.FromArgb(215, 0, 0, 0), LinearGradientMode.Vertical))
            g.FillRectangle(shade, 0, size.Height - band, size.Width, band);

        var pad = 14 * k;
        if (v.Pill is not null || v.Caption is not null)
            DrawCaption(g, fonts, v with { Pill = v.Pill ?? v.Caption, Caption = null }, new RectangleF(pad, pad, size.Width - pad * 2, 20 * k), k);
        if (!string.IsNullOrEmpty(v.Big))
        {
            using var font = fonts.Bold(Math.Min(26 * k, size.Height * 0.2f));
            DrawText(g, v.Big!, font, Color.White, new RectangleF(pad - 2, size.Height - pad - font.Size * 1.3f, size.Width - pad * 2, font.Size * 1.3f), StringAlignment.Near);
        }
    }

    private static void DrawOffline(Graphics g, PixelSize size, Fonts fonts, TileVisual v, bool mini)
    {
        var iconSide = mini ? Math.Min(30f, size.Height * 0.45f) : Math.Min(40f * size.Height / RefHeight, size.Height * 0.35f);
        var textHeight = mini || string.IsNullOrEmpty(v.Detail) ? 0 : 20f;
        var top = (size.Height - iconSide - (textHeight > 0 ? textHeight + 8 : 0)) / 2;
        var iconRect = new RectangleF((size.Width - iconSide) / 2, top, iconSide, iconSide);
        ObsGlyphs.Draw(g, v.Icon, iconRect, TilePalette.Faint);
        if (v.StatusDot is { } dot)
        {
            var d = Math.Max(7f, iconSide * 0.22f);
            using var brush = new SolidBrush(dot);
            g.FillEllipse(brush, iconRect.Right - d * 0.6f, iconRect.Top - d * 0.2f, d, d);
        }
        if (textHeight > 0)
        {
            using var font = fonts.Regular(13f * Math.Max(1f, size.Height / RefHeight));
            DrawText(g, v.Detail!, font, TilePalette.Dim, new RectangleF(6, iconRect.Bottom + 8, size.Width - 12, textHeight), StringAlignment.Center);
        }
    }

    /// <summary>Painel de cenas: uma célula por cena, verde a do programa, anel azul na prévia.</summary>
    private static void DrawGrid(Graphics g, PixelSize size, Fonts fonts, TileVisual v)
    {
        var cells = v.Cells ?? [];
        var full = size;
        if (v.GridFooter)
        {
            size = GridLayout.Body(full);
            if (!string.IsNullOrEmpty(v.Detail) && cells.Count > 0)
            {
                using var footerFont = fonts.Regular(13);
                var footer = new RectangleF(GridLayout.Pad, size.Height - 2, full.Width - GridLayout.Pad * 2, GridLayout.FooterHeight - 2);
                DrawText(g, v.Detail!, footerFont, v.Accent ?? TilePalette.Dim, footer, StringAlignment.Center);
            }
        }
        var (columns, rows) = GridLayout.Shape(size);
        for (var i = 0; i < cells.Count && i < columns * rows; i++)
        {
            var cell = cells[i];
            var rect = GridLayout.Cell(size, columns, rows, i);
            var radius = Math.Min(10f, rect.Height * 0.18f);
            using (var path = ObsGlyphs.Rounded(rect, radius))
            {
                using (var brush = new SolidBrush(cell.Fill is { } fill ? TilePalette.Tint(fill, 0.42) : TilePalette.Panel))
                    g.FillPath(brush, path);
                if ((cell.Ring ?? cell.Fill) is { } ringColor)
                {
                    using var pen = new Pen(ringColor, 2.5f);
                    using var inner = ObsGlyphs.Rounded(RectangleF.Inflate(rect, -1.25f, -1.25f), radius - 1);
                    g.DrawPath(pen, inner);
                }
            }
            var em = Math.Clamp(rect.Height * 0.24f, 11f, 17f);
            using var font = cell.More ? fonts.Regular(em) : fonts.Bold(em);
            using var brushText = new SolidBrush(cell.More ? TilePalette.Dim : TilePalette.Ink);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisWord,
            };
            g.DrawString(cell.Text, font, brushText, RectangleF.Inflate(rect, -6, -4), format);
        }
        if (cells.Count == 0 && !string.IsNullOrEmpty(v.Detail))
        {
            using var font = fonts.Regular(14);
            DrawText(g, v.Detail!, font, TilePalette.Dim, new RectangleF(0, 0, full.Width, full.Height), StringAlignment.Center);
        }
    }

    /// <summary>Status: rótulo (ou pílula REC/LIVE) e linhas de valor; no Wide, o minigráfico da CPU à direita.</summary>
    private static void DrawStats(Graphics g, PixelSize size, Fonts fonts, TileVisual v, Color background, bool wide)
    {
        var k = Math.Min(size.Width / (wide ? 400f : RefWidth), size.Height / RefHeight);
        var pad = 14 * k;
        var iconSide = 22 * k;
        DrawCaption(g, fonts, v, new RectangleF(pad, pad, size.Width - pad * 2 - iconSide - 6, iconSide), k);
        ObsGlyphs.Draw(g, v.Icon, new RectangleF(size.Width - pad - iconSide, pad, iconSide, iconSide), IconColor(v), v.Slashed, background);

        var top = pad + iconSide + 6 * k;
        var listWidth = wide ? size.Width * 0.46f : size.Width - pad * 2;
        var lines = v.Lines ?? [];
        var lineHeight = Math.Min(24 * k, (size.Height - top - pad) / Math.Max(1, lines.Count));
        using var labelFont = fonts.Regular(Math.Max(11f, 12.5f * k));
        using var valueFont = fonts.Bold(Math.Max(12f, 15f * k));
        for (var i = 0; i < lines.Count; i++)
        {
            var row = new RectangleF(pad, top + i * lineHeight, listWidth, lineHeight);
            DrawText(g, lines[i].Label, labelFont, TilePalette.Dim, row, StringAlignment.Near);
            DrawText(g, lines[i].Value, valueFont, lines[i].Color ?? TilePalette.Ink, row, StringAlignment.Far);
        }
        if (wide && v.Spark is { Count: > 1 } spark)
        {
            var left = pad + listWidth + 22 * k;
            var sparkRect = new RectangleF(left, top + 18 * k, size.Width - left - pad, size.Height - top - pad - 18 * k);
            using (var font = fonts.Regular(Math.Max(11f, 11.5f * k)))
                DrawText(g, v.Detail ?? "", font, TilePalette.Dim, new RectangleF(left, top, sparkRect.Width, 16 * k), StringAlignment.Near);
            DrawSpark(g, sparkRect, spark, v.SparkColor ?? TilePalette.Preview);
        }
    }

    /// <summary>Barra fina de progresso com o tempo à direita (mídia).</summary>
    private static void DrawProgress(Graphics g, Fonts fonts, TileVisual v, RectangleF row, float k)
    {
        using var font = fonts.Semibold(Math.Max(11f, 12.5f * k));
        var textWidth = string.IsNullOrEmpty(v.ProgressText) ? 0 : g.MeasureString(v.ProgressText, font).Width + 2;
        if (textWidth > 0)
            DrawText(g, v.ProgressText!, font, TilePalette.Dim, new RectangleF(row.Right - textWidth, row.Y, textWidth, row.Height), StringAlignment.Far);
        var barWidth = row.Width - textWidth - (textWidth > 0 ? 8 * k : 0);
        if (barWidth <= 4)
            return;
        var bar = new RectangleF(row.X, row.Y + (row.Height - 5 * k) / 2, barWidth, 5 * k);
        using (var track = ObsGlyphs.Rounded(bar, bar.Height / 2))
        using (var brush = new SolidBrush(Color.FromArgb(56, 255, 255, 255)))
            g.FillPath(brush, track);
        if (v.Progress is { } progress and > 0)
        {
            using var done = ObsGlyphs.Rounded(bar with { Width = Math.Max(bar.Height, (float)(bar.Width * Math.Clamp(progress, 0, 1))) }, bar.Height / 2);
            using var brush = new SolidBrush(v.Accent ?? TilePalette.Ink);
            g.FillPath(brush, done);
        }
    }

    // ---- peças ----

    private static readonly Lazy<Bitmap> LogoMask = new(LoadLogoMask);

    /// <summary>Símbolo do OBS (branco sobre preto, 512 px) como máscara: o brilho vira transparência.</summary>
    private static Bitmap LoadLogoMask()
    {
        using var stream = typeof(TileRenderer).Assembly.GetManifestResourceStream("OpenWigiDash.ObsPlugin.Rendering.obs-logo-mask.png")!;
        using var source = new Bitmap(stream);
        var mask = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                mask.SetPixel(x, y, Color.FromArgb(source.GetPixel(x, y).R, 255, 255, 255));
        return mask;
    }

    /// <summary>O símbolo grande no canto inferior direito, saindo do tile, na cor <paramref name="color"/>.</summary>
    private static void DrawLogo(Graphics g, PixelSize size, Color color)
    {
        var side = size.Height * 1.25f;
        var rect = new RectangleF(size.Width - side * 0.64f, size.Height - side * 0.7f, side, side);
        var mask = LogoMask.Value;
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix(
        [
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 1, 0],
            [color.R / 255f, color.G / 255f, color.B / 255f, 0, 1],
        ]));
        lock (mask)
            g.DrawImage(mask, Rectangle.Round(rect), 0, 0, mask.Width, mask.Height, GraphicsUnit.Pixel, attributes);
    }

    private static void DrawCaption(Graphics g, Fonts fonts, TileVisual v, RectangleF rect, float k)
    {
        if (v.Pill is { } pill)
        {
            using var font = fonts.Bold(10.5f * Math.Max(k, 1f));
            var text = pill.ToUpperInvariant();
            var w = g.MeasureString(text, font).Width + 10 * k;
            var h = font.Size * 1.7f;
            var pillRect = new RectangleF(rect.X, rect.Y + (rect.Height - h) / 2, w, h);
            using (var path = ObsGlyphs.Rounded(pillRect, h / 2))
            using (var brush = new SolidBrush(v.Accent ?? TilePalette.Live))
                g.FillPath(brush, path);
            DrawText(g, text, font, Color.White, pillRect, StringAlignment.Center);
            return;
        }
        if (string.IsNullOrEmpty(v.Caption))
            return;
        using var captionFont = fonts.Bold(Math.Max(10.5f, 11f * k));
        DrawText(g, v.Caption!.ToUpperInvariant(), captionFont, v.Accent ?? TilePalette.Dim, rect, StringAlignment.Near);
    }

    /// <summary>Medidor segmentado: verde até -18 dB, amarelo até -9 dB, vermelho acima (posições de 0 a 1).</summary>
    private static void DrawMeter(Graphics g, RectangleF rect, double level, bool muted)
    {
        var segments = Math.Max(8, (int)(rect.Width / 9));
        var gap = 2f;
        var w = (rect.Width - gap * (segments - 1)) / segments;
        var lit = (int)Math.Round(Math.Clamp(level, 0, 1) * segments);
        for (var i = 0; i < segments; i++)
        {
            var f = i / (double)segments;
            var color = muted ? TilePalette.Faint : f > 0.86 ? TilePalette.Record : f > 0.68 ? TilePalette.Warn : TilePalette.Ok;
            using var brush = new SolidBrush(i < lit ? color : Color.FromArgb(41, color));
            g.FillRectangle(brush, rect.X + i * (w + gap), rect.Y, w, rect.Height);
        }
    }

    private static void DrawSpark(Graphics g, RectangleF rect, IReadOnlyList<double> values, Color color)
    {
        var lo = values.Min();
        var hi = values.Max();
        var range = Math.Max(hi - lo, Math.Max(hi * 0.05, 1));
        lo -= range * 0.1;
        range *= 1.2;
        var points = values.Select((v, i) => new PointF(
            rect.X + i * rect.Width / (values.Count - 1),
            rect.Bottom - (float)((v - lo) / range) * rect.Height)).ToArray();
        using (var path = new GraphicsPath())
        {
            path.AddLines(points);
            path.AddLine(points[^1], new PointF(rect.Right, rect.Bottom));
            path.AddLine(new PointF(rect.Right, rect.Bottom), new PointF(rect.X, rect.Bottom));
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(46, color));
            g.FillPath(fill, path);
        }
        using var pen = new Pen(color, 1.6f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, points);
    }

    private static Color IconColor(TileVisual v)
    {
        var color = v.Accent ?? TilePalette.IconRest;
        return v.PulseDim ? Color.FromArgb(90, color) : color;
    }

    /// <summary>O maior tamanho (até 34 no Small) em que o texto cabe na largura.</summary>
    private static float BigSize(string text, float width, float k, Fonts fonts, Graphics g)
    {
        var em = (text.Length <= 8 ? 34f : text.Length <= 12 ? 26f : 20f) * k;
        while (em > 12)
        {
            using var font = fonts.Bold(em);
            if (g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width <= width)
                break;
            em -= 1;
        }
        return em;
    }

    private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect, StringAlignment alignment)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = alignment,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(text, font, brush, rect, format);
    }

    private static Bitmap? Decode(byte[]? jpeg)
    {
        if (jpeg is null || jpeg.Length == 0)
            return null;
        try
        {
            return Thumbnails.GetValue(jpeg, bytes =>
            {
                using var stream = new MemoryStream(bytes);
                using var loaded = new Bitmap(stream);
                return new Bitmap(loaded);
            });
        }
        catch (ArgumentException)
        {
            return null; // JPEG quebrado: fica o ícone
        }
    }

    internal static WidgetFrame ToFrame(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return new WidgetFrame(new PixelSize(bitmap.Width, bitmap.Height), FramePixelFormat.Bgra32, pixels, data.Stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>Fontes do tema (Segoe UI no Windows), em pixels.</summary>
    private sealed class Fonts(WidgetTheme theme)
    {
        public Font Regular(float px) => new(theme.PrimaryFontFamily, px, FontStyle.Regular, GraphicsUnit.Pixel);
        public Font Semibold(float px) => new(theme.SecondaryFontFamily, px, FontStyle.Regular, GraphicsUnit.Pixel);
        /// <summary>Peso dos números e nomes (600 nos protótipos): a Semibold do tema, sem negrito por cima.</summary>
        public Font Bold(float px) => Semibold(px);
    }
}
