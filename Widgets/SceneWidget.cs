using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Settings;
using OpenWigiDash.ObsPlugin.Widgets;

[assembly: OpenWigiWidget(typeof(SceneWidget))]

namespace OpenWigiDash.ObsPlugin.Widgets;

/// <summary>
/// Troca para uma cena. Verde quando ela está no ar, azul quando está na prévia (modo estúdio). No Wide e no Large
/// mostra a miniatura ao vivo da cena (1 quadro por segundo).
/// </summary>
public sealed class SceneWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("f1b75632-0c9d-46b8-aaf5-821bad519237"), "OBS: Cena",
        "Toque troca para a cena (no modo estúdio, para a prévia); segure para mandar direto para o ar. No Wide e no Large, com miniatura ao vivo.",
        WidgetSize.Small, WidgetSize.Wide, WidgetSize.Large);

    protected override TileVisual Preview => SceneInstance.Visual("Jogo", program: true, preview: false, exists: true, thumbnail: null, showThumbnail: false);

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new SceneInstance(context);
}

public sealed class SceneInstance(IWidgetContext context) : ObsTileInstance(context)
{
    internal const string SceneKey = "scene";
    internal const string ThumbnailKey = "thumbnail";

    private IDisposable? _thumbnailWatch;
    private string? _watched;

    /// <summary>Nome da cena escolhida (o OBS identifica cena pelo nome).</summary>
    public string? Scene { get; private set; }

    /// <summary>Miniatura ao vivo no Wide e no Large.</summary>
    public bool ShowThumbnail { get; private set; } = true;

    public IReadOnlyList<string> AvailableScenes => Obs.Scenes;

    protected override ObsIcon Icon => ObsIcon.Scene;
    protected override bool HasLongPress => true;
    public override Type? OptionsComponent => typeof(SceneOptions);

    /// <summary>Tamanho em que a miniatura aparece.</summary>
    private bool ThumbnailSize => !IsMini && (IsWide || Context.PixelSize.Height >= 250);

    protected internal override async ValueTask LoadSettingsAsync(CancellationToken cancellationToken)
    {
        Scene = await Context.Settings.GetAsync(SceneKey, cancellationToken).ConfigureAwait(false);
        if (bool.TryParse(await Context.Settings.GetAsync(ThumbnailKey, cancellationToken).ConfigureAwait(false), out var show))
            ShowThumbnail = show;
    }

    public async Task SetSceneAsync(string? scene)
    {
        Scene = string.IsNullOrWhiteSpace(scene) ? null : scene;
        await SaveAsync(SceneKey, Scene).ConfigureAwait(false);
        OnAwakeChanged(!IsAsleep);
    }

    public async Task SetShowThumbnailAsync(bool show)
    {
        ShowThumbnail = show;
        await SaveAsync(ThumbnailKey, show.ToString()).ConfigureAwait(false);
        OnAwakeChanged(!IsAsleep);
    }

    protected override TileVisual GetVisual()
    {
        if (Scene is null)
            return new TileVisual(ObsIcon.Scene) { Caption = "Cena", Big = "Escolha a cena", BigDim = true, Detail = "Nas opções do widget" };
        var thumb = ShowThumbnail && ThumbnailSize;
        return Visual(Scene, Obs.ProgramScene == Scene, Obs.StudioMode && Obs.PreviewScene == Scene, Obs.Scenes.Contains(Scene),
            thumb ? Obs.Thumbnail(Scene)?.Jpeg : null, thumb);
    }

    internal static TileVisual Visual(string scene, bool program, bool preview, bool exists, byte[]? thumbnail, bool showThumbnail)
    {
        if (!exists)
            return new TileVisual(ObsIcon.Scene) { Caption = "Cena", Big = scene, BigDim = true, Detail = "Não existe nesta coleção" };
        var color = program ? TilePalette.Ok : preview ? TilePalette.Preview : (System.Drawing.Color?)null;
        var caption = program ? "No ar" : preview ? "Prévia" : "Cena";
        if (showThumbnail)
        {
            return new TileVisual(ObsIcon.Scene)
            {
                Layout = TileLayout.Thumbnail, Big = scene, Thumbnail = thumbnail, Ring = color, Accent = color,
                Pill = color is null ? null : caption,
            };
        }
        return new TileVisual(ObsIcon.Scene) { Caption = caption, Big = scene, Accent = color, Ring = color };
    }

    protected override void OnAwakeChanged(bool awake)
    {
        var want = awake && ShowThumbnail && ThumbnailSize ? Scene : null;
        if (want == _watched)
            return;
        _thumbnailWatch?.Dispose();
        _thumbnailWatch = want is null ? null : Session.WatchThumbnail(want);
        _watched = want;
    }

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        if (Scene is null || !Obs.Scenes.Contains(Scene))
            return;
        switch (touch.Kind)
        {
            case TouchKind.Tap when Obs.StudioMode:
                Check(await Session.SetPreviewSceneAsync(Scene).ConfigureAwait(false));
                break;
            case TouchKind.Tap:
            case TouchKind.LongPress:
                Check(await Session.SetProgramSceneAsync(Scene).ConfigureAwait(false));
                break;
        }
    }
}
