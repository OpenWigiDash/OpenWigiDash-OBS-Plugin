using System.Drawing;

namespace OpenWigiDash.ObsPlugin.Rendering;

/// <summary>Como o tile se organiza.</summary>
public enum TileLayout
{
    /// <summary>Rótulo em cima, valor grande no meio, detalhe embaixo (Small) ou ícone redondo à esquerda (Wide).</summary>
    Standard,
    /// <summary>Mixer: nome e dB, medidor segmentado e, no Wide, "−" e "+" nas bordas.</summary>
    Mixer,
    /// <summary>Cena com miniatura (Wide e Large): a imagem ocupa o tile, o nome fica embaixo.</summary>
    Thumbnail,
    /// <summary>OBS ainda não pronto: ícone apagado, um ponto de estado e o motivo.</summary>
    Offline,
    /// <summary>Grade de botões (painel de cenas): <see cref="TileVisual.Cells"/> em <see cref="GridLayout"/>.</summary>
    Grid,
    /// <summary>Linhas de rótulo e valor (status do OBS), com minigráfico no Wide.</summary>
    Stats,
}

/// <summary>Uma célula do painel: o texto e a cor (programa), o anel (prévia) ou "mais" (próxima página).</summary>
public sealed record GridCell(string Text, Color? Fill = null, Color? Ring = null, bool More = false);

/// <summary>Uma linha do status: rótulo apagado à esquerda, valor à direita (com cor se estiver ruim).</summary>
public sealed record StatLine(string Label, string Value, Color? Color = null);

/// <summary>
/// O que um tile do OBS mostra, sem desenho. As instâncias montam isto a partir do <c>ObsState</c>; o
/// <see cref="TileRenderer"/> desenha. Os testes conferem isto em qualquer sistema (o desenho só roda no Windows).
/// </summary>
public sealed record TileVisual(ObsIcon Icon)
{
    public TileLayout Layout { get; init; } = TileLayout.Standard;

    /// <summary>Rótulo pequeno em maiúsculas no topo (ex.: GRAVANDO, NO AR).</summary>
    public string? Caption { get; init; }

    /// <summary>A informação principal: tempo, nome da cena, dB.</summary>
    public string? Big { get; init; }

    /// <summary>Linha de baixo, apagada (ex.: "Segure para pausar", kbps).</summary>
    public string? Detail { get; init; }

    /// <summary>Texto curto embaixo do ícone no Mini (ex.: o tempo).</summary>
    public string? MiniText { get; init; }

    /// <summary>Cor do estado: tinge o fundo, o rótulo e o ícone. <c>null</c> = repouso (grafite).</summary>
    public Color? Accent { get; init; }

    /// <summary>
    /// <c>false</c>: a cor do estado vai só no rótulo e no ícone, sem tingir o fundo (ex.: replay ligado esperando).
    /// </summary>
    public bool Tinted { get; init; } = true;

    /// <summary>Anel de 3 px por dentro (cena no programa ou na prévia).</summary>
    public Color? Ring { get; init; }

    /// <summary>Pílula no lugar do rótulo (ex.: AO VIVO).</summary>
    public string? Pill { get; init; }

    /// <summary>Ícone cortado (mutado, câmera desligada).</summary>
    public bool Slashed { get; init; }

    /// <summary>Ícone apagado no quadro de pulso (o ponto da gravação piscando).</summary>
    public bool PulseDim { get; init; }

    /// <summary>Valor grande apagado (ex.: "Parado").</summary>
    public bool BigDim { get; init; }

    /// <summary>Medidor de 0 a 1; <c>null</c> = sem medidor.</summary>
    public double? Meter { get; init; }

    /// <summary>"−" e "+" nas bordas (mixer no Wide).</summary>
    public bool Hints { get; init; }

    /// <summary>Minigráfico no canto (kbps da live).</summary>
    public IReadOnlyList<double>? Spark { get; init; }

    /// <summary>Cor do minigráfico e do ponto de saúde.</summary>
    public Color? SparkColor { get; init; }

    /// <summary>Miniatura em JPEG (layout <see cref="TileLayout.Thumbnail"/>).</summary>
    public byte[]? Thumbnail { get; init; }

    /// <summary>Ponto de estado no Offline (vermelho = parado, azul = conectando).</summary>
    public Color? StatusDot { get; init; }

    /// <summary>Barra branca embaixo enchendo durante o long-press (0..1).</summary>
    public double? Press { get; init; }

    /// <summary>Barra de progresso embaixo (mídia), de 0 a 1, com <see cref="ProgressText"/> à direita.</summary>
    public double? Progress { get; init; }

    public string? ProgressText { get; init; }

    /// <summary>Células do layout <see cref="TileLayout.Grid"/>, na ordem de leitura.</summary>
    public IReadOnlyList<GridCell>? Cells { get; init; }

    /// <summary>Linhas do layout <see cref="TileLayout.Stats"/>.</summary>
    public IReadOnlyList<StatLine>? Lines { get; init; }
}

/// <summary>Cores dos tiles, iguais aos protótipos aprovados. O fundo é sempre grafite: a tela é escura.</summary>
public static class TilePalette
{
    public static readonly Color Background = Color.FromArgb(0x14, 0x16, 0x1B);
    public static readonly Color Ink = Color.FromArgb(0xEE, 0xF0, 0xF4);
    public static readonly Color Dim = Color.FromArgb(0x8B, 0x93, 0xA3);
    public static readonly Color Faint = Color.FromArgb(0x4A, 0x50, 0x5C);
    public static readonly Color IconRest = Color.FromArgb(0xC6, 0xCB, 0xD5);
    public static readonly Color Panel = Color.FromArgb(0x1D, 0x20, 0x27);
    public static readonly Color Record = Color.FromArgb(0xE5, 0x48, 0x4D);
    public static readonly Color Live = Color.FromArgb(0xEC, 0x3A, 0x6A);
    public static readonly Color Ok = Color.FromArgb(0x30, 0xC4, 0x8D);
    public static readonly Color Preview = Color.FromArgb(0x4C, 0x8D, 0xFF);
    public static readonly Color Warn = Color.FromArgb(0xF5, 0xA5, 0x24);
    public static readonly Color Studio = Color.FromArgb(0x9B, 0x7B, 0xFF);

    /// <summary>A cor misturada ao grafite (o "tom" do fundo de um estado).</summary>
    public static Color Tint(Color color, double amount) => Blend(Background, color, amount);

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
}
