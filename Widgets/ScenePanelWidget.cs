using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Widgets;

[assembly: OpenWigiWidget(typeof(ScenePanelWidget))]

namespace OpenWigiDash.ObsPlugin.Widgets;

/// <summary>
/// Todas as cenas numa grade. Verde = no ar, anel azul = prévia. Arrastar para cima ou para baixo abre e fecha a
/// tela cheia; com mais cenas que células, a última vira "mais" e, na tela cheia, arrastar para os lados troca a página.
/// </summary>
public sealed class ScenePanelWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("2d6a9f14-e870-4c3b-8f25-b91c0e4d7a63"), "OBS: Painel de cenas",
        "Todas as cenas numa grade: toque troca (no modo estúdio, para a prévia), segure manda direto para o ar. Arraste para cima para a tela cheia.",
        WidgetSize.Large) with { SupportsFullscreen = true };

    protected override string CatalogLabel => "Cenas";

    protected override TileVisual Preview => ScenePanelInstance.Visual(
        ["Começando", "Câmera", "Jogo", "Conversa", "Intervalo", "Encerramento"], "Jogo", "Câmera", studio: true,
        new PixelSize(400, 290), page: 0);

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new ScenePanelInstance(context);
}

public sealed class ScenePanelInstance(IWidgetContext context) : ObsTileInstance(context)
{
    private int _page;

    protected override ObsIcon Icon => ObsIcon.Grid;
    protected override bool HasLongPress => true;

    internal int Page => _page;

    protected override TileVisual GetVisual() =>
        Visual(Obs.Scenes, Obs.ProgramScene, Obs.PreviewScene, Obs.StudioMode, Context.PixelSize, _page);

    internal static TileVisual Visual(IReadOnlyList<string> scenes, string? program, string? preview, bool studio, PixelSize size, int page)
    {
        if (scenes.Count == 0)
            return new TileVisual(ObsIcon.Grid) { Layout = TileLayout.Grid, Cells = [], Detail = "Nenhuma cena" };
        var (columns, rows) = GridLayout.Shape(size);
        var slice = GridLayout.Page(scenes.Count, columns * rows, page);
        var cells = new List<GridCell>(slice.Count + 1);
        for (var i = 0; i < slice.Count; i++)
        {
            var scene = scenes[slice.Start + i];
            cells.Add(new GridCell(scene,
                Fill: scene == program ? TilePalette.Ok : null,
                Ring: studio && scene == preview && scene != program ? TilePalette.Preview : null));
        }
        if (slice.More)
        {
            var current = slice.Start / Math.Max(1, columns * rows - 1) + 1;
            cells.Add(new GridCell($"Mais › {current}/{slice.Pages}", More: true));
        }
        return new TileVisual(ObsIcon.Grid) { Layout = TileLayout.Grid, Cells = cells };
    }

    /// <summary>A cena sob o dedo, ou <c>""</c> para a célula "mais", ou <c>null</c> se não pegou célula.</summary>
    internal static string? SceneAt(IReadOnlyList<string> scenes, PixelSize size, int page, int x, int y)
    {
        var (columns, rows) = GridLayout.Shape(size);
        if (GridLayout.HitTest(size, columns, rows, x, y) is not { } index)
            return null;
        var slice = GridLayout.Page(scenes.Count, columns * rows, page);
        if (index < slice.Count)
            return scenes[slice.Start + index];
        return slice.More && index == slice.Count ? "" : null;
    }

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        var scenes = Obs.Scenes;
        switch (touch.Kind)
        {
            case TouchKind.SwipeUp when !Context.IsFullscreen:
            case TouchKind.SwipeDown when Context.IsFullscreen:
                await Context.RequestFullscreenAsync(!Context.IsFullscreen, cancellationToken).ConfigureAwait(false);
                return;
            case TouchKind.SwipeLeft when Context.IsFullscreen:
                Turn(scenes, +1);
                return;
            case TouchKind.SwipeRight when Context.IsFullscreen:
                Turn(scenes, -1);
                return;
            case TouchKind.Tap or TouchKind.LongPress:
                break;
            default:
                return;
        }

        switch (SceneAt(scenes, Context.PixelSize, _page, touch.X, touch.Y))
        {
            case "" when touch.Kind == TouchKind.Tap:
                Turn(scenes, +1);
                break;
            case { Length: > 0 } scene when touch.Kind == TouchKind.Tap && Obs.StudioMode:
                Check(await Session.SetPreviewSceneAsync(scene).ConfigureAwait(false));
                break;
            case { Length: > 0 } scene:
                Check(await Session.SetProgramSceneAsync(scene).ConfigureAwait(false));
                break;
        }
    }

    private void Turn(IReadOnlyList<string> scenes, int delta)
    {
        var (columns, rows) = GridLayout.Shape(Context.PixelSize);
        var pages = GridLayout.Page(scenes.Count, columns * rows, 0).Pages;
        if (pages <= 1)
            return;
        _page = ((_page + delta) % pages + pages) % pages;
        Context.RequestRender();
    }

    public override ValueTask OnFullscreenChangedAsync(bool fullscreen, CancellationToken cancellationToken)
    {
        _page = 0; // a grade mudou de tamanho: a página antiga não vale mais
        return base.OnFullscreenChangedAsync(fullscreen, cancellationToken);
    }
}
