using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Settings;
using OpenWigiDash.ObsPlugin.Widgets;

[assembly: OpenWigiWidget(typeof(SourceVisibilityWidget))]
[assembly: OpenWigiWidget(typeof(FilterWidget))]

namespace OpenWigiDash.ObsPlugin.Widgets;

// ---------------------------------------------------------------------------------------------- Fonte visível

/// <summary>Mostra e esconde uma fonte numa cena. Segurar mostra (ou esconde) só enquanto o dedo está na tela.</summary>
public sealed class SourceVisibilityWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("5c0f3e2a-8d41-4f7b-9b52-6a1e7d93c4f0"), "OBS: Fonte visível",
        "Toque mostra e esconde uma fonte da cena; segure para inverter só enquanto segura.",
        WidgetSize.Mini, WidgetSize.Small);

    // A prévia usa o ícone da webcam de exemplo; no catálogo, o olho diz o que o widget faz.
    protected override ObsIcon CatalogIcon => ObsIcon.Eye;

    protected override TileVisual Preview => SourceVisibilityInstance.Visual(
        new SceneItem(1, "Webcam", "dshow_input", true, false), "Jogo", mini: false);

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new SourceVisibilityInstance(context);
}

public sealed class SourceVisibilityInstance(IWidgetContext context) : ObsTileInstance(context)
{
    internal const string SceneKey = "scene";
    internal const string SourceKey = "source";

    private IDisposable? _watch;
    private string? _watched;
    private bool? _restore;

    public string? Scene { get; private set; }
    public string? Source { get; private set; }

    public IReadOnlyList<string> AvailableScenes => Obs.Scenes;

    /// <summary>Itens da cena escolhida (para a tela de opções); vazio enquanto não chegam.</summary>
    public IReadOnlyList<SceneItem> AvailableSources => Obs.SceneItems(Scene) ?? [];

    protected override ObsIcon Icon => ObsIcon.Eye;
    protected override bool HasLongPress => true;
    public override Type? OptionsComponent => typeof(SourceOptions);

    protected internal override async ValueTask LoadSettingsAsync(CancellationToken cancellationToken)
    {
        Scene = await Context.Settings.GetAsync(SceneKey, cancellationToken).ConfigureAwait(false);
        Source = await Context.Settings.GetAsync(SourceKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetSceneAsync(string? scene)
    {
        scene = string.IsNullOrWhiteSpace(scene) ? null : scene;
        if (scene != Scene)
        {
            Source = null;
            await SaveAsync(SourceKey, null).ConfigureAwait(false);
        }
        Scene = scene;
        await SaveAsync(SceneKey, Scene).ConfigureAwait(false);
        OnAwakeChanged(true); // a tela de opções precisa da lista mesmo com o tile dormindo
    }

    public Task SetSourceAsync(string? source)
    {
        Source = string.IsNullOrWhiteSpace(source) ? null : source;
        return SaveAsync(SourceKey, Source);
    }

    private SceneItem? Item => Obs.SceneItems(Scene)?.FirstOrDefault(i => i.SourceName == Source);

    protected override TileVisual GetVisual()
    {
        if (Scene is null || Source is null)
            return new TileVisual(ObsIcon.Eye) { Caption = "Fonte", Big = "Escolha a fonte", BigDim = true, Detail = "Nas opções do widget", MiniText = "Escolha" };
        if (!Obs.Scenes.Contains(Scene))
            return new TileVisual(ObsIcon.Eye) { Caption = "Fonte", Big = Source, BigDim = true, Detail = $"A cena {Scene} sumiu", MiniText = "Sumiu" };
        if (Obs.SceneItems(Scene) is not { } items)
            return new TileVisual(ObsIcon.Eye) { Caption = "Fonte", Big = Source, BigDim = true, Detail = "Lendo a cena…" };
        if (items.FirstOrDefault(i => i.SourceName == Source) is not { } item)
            return new TileVisual(ObsIcon.Eye) { Caption = "Fonte", Big = Source, BigDim = true, Detail = $"Não está mais em {Scene}", MiniText = "Sumiu" };
        return Visual(item, Scene, IsMini);
    }

    /// <summary>Ícone pelo tipo da fonte: câmera, captura de tela, navegador, texto, imagem, mídia, áudio, cena.</summary>
    internal static ObsIcon IconFor(string? kind) => kind switch
    {
        null or "" => ObsIcon.Scene, // cena dentro de cena ou grupo
        "dshow_input" or "av_capture_input" or "av_capture_input_v2" or "v4l2_input" or "macos-avcapture" => ObsIcon.VirtualCam,
        "monitor_capture" or "window_capture" or "game_capture" or "display_capture" or "screen_capture" or "xcomposite_input" => ObsIcon.Monitor,
        "browser_source" => ObsIcon.Globe,
        "text_gdiplus" or "text_ft2_source" => ObsIcon.Text,
        "image_source" or "slideshow" or "color_source" => ObsIcon.Image,
        "ffmpeg_source" or "vlc_source" => ObsIcon.Play,
        _ when kind.Contains("audio", StringComparison.Ordinal) || kind.StartsWith("wasapi", StringComparison.Ordinal) => MixerInstance.IconFor(kind),
        _ => ObsIcon.Eye,
    };

    internal static TileVisual Visual(SceneItem item, string scene, bool mini) => item.Enabled
        ? new TileVisual(IconFor(item.InputKind)) { Caption = "Visível", Accent = TilePalette.Ok, Big = item.SourceName, Detail = scene, MiniText = "Visível" }
        : new TileVisual(IconFor(item.InputKind)) { Caption = "Escondida", Big = item.SourceName, Slashed = true, Detail = scene, MiniText = "Escondida" };

    protected override void OnAwakeChanged(bool awake)
    {
        var want = awake ? Scene : null;
        if (want == _watched)
            return;
        _watch?.Dispose();
        _watch = want is null ? null : Session.WatchSceneItems(want);
        _watched = want;
    }

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        if (Scene is null || Item is not { } item)
            return;
        switch (touch.Kind)
        {
            case TouchKind.Tap:
                Check(await Session.SetSceneItemEnabledAsync(Scene, item.Id, !item.Enabled).ConfigureAwait(false));
                break;
            // Segurar: inverte só enquanto o dedo está na tela (ex.: mostrar a câmera por um instante).
            case TouchKind.LongPress:
                _restore = item.Enabled;
                Check(await Session.SetSceneItemEnabledAsync(Scene, item.Id, !item.Enabled).ConfigureAwait(false));
                break;
            case TouchKind.PressEnded when _restore is { } restore:
                _restore = null;
                await Session.SetSceneItemEnabledAsync(Scene, item.Id, restore).ConfigureAwait(false);
                break;
        }
    }

    public override ValueTask DisposeAsync()
    {
        _watch?.Dispose();
        _watch = null;
        return base.DisposeAsync();
    }
}

// ---------------------------------------------------------------------------------------------- Filtro

/// <summary>Liga e desliga um filtro de uma fonte ou cena. Segurar inverte só enquanto segura.</summary>
public sealed class FilterWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("9a6d2c71-3f0e-4b8a-a5d4-2e7c1b80f963"), "OBS: Filtro",
        "Toque liga e desliga um filtro de uma fonte ou cena; segure para inverter só enquanto segura.",
        WidgetSize.Mini, WidgetSize.Small);

    protected override TileVisual Preview => FilterInstance.Visual(new SourceFilter("Correção de cor", "color_filter_v2", true), "Webcam");

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new FilterInstance(context);
}

public sealed class FilterInstance(IWidgetContext context) : ObsTileInstance(context)
{
    internal const string SourceKey = "source";
    internal const string FilterKey = "filter";

    private IDisposable? _watch;
    private string? _watched;
    private bool? _restore;

    public string? Source { get; private set; }
    public string? Filter { get; private set; }

    /// <summary>Fontes e cenas (filtro pode estar em qualquer uma).</summary>
    public IReadOnlyList<string> AvailableSources => [.. Obs.Scenes, .. Obs.Inputs.Select(i => i.Name).Where(n => !Obs.Scenes.Contains(n))];

    public IReadOnlyList<SourceFilter> AvailableFilters => Obs.Filters(Source) ?? [];

    protected override ObsIcon Icon => ObsIcon.Filter;
    protected override bool HasLongPress => true;
    public override Type? OptionsComponent => typeof(FilterOptions);

    protected internal override async ValueTask LoadSettingsAsync(CancellationToken cancellationToken)
    {
        Source = await Context.Settings.GetAsync(SourceKey, cancellationToken).ConfigureAwait(false);
        Filter = await Context.Settings.GetAsync(FilterKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetSourceAsync(string? source)
    {
        source = string.IsNullOrWhiteSpace(source) ? null : source;
        if (source != Source)
        {
            Filter = null;
            await SaveAsync(FilterKey, null).ConfigureAwait(false);
        }
        Source = source;
        await SaveAsync(SourceKey, Source).ConfigureAwait(false);
        OnAwakeChanged(true);
    }

    public Task SetFilterAsync(string? filter)
    {
        Filter = string.IsNullOrWhiteSpace(filter) ? null : filter;
        return SaveAsync(FilterKey, Filter);
    }

    private SourceFilter? Current => Obs.Filters(Source)?.FirstOrDefault(f => f.Name == Filter);

    protected override TileVisual GetVisual()
    {
        if (Source is null || Filter is null)
            return new TileVisual(ObsIcon.Filter) { Caption = "Filtro", Big = "Escolha o filtro", BigDim = true, Detail = "Nas opções do widget", MiniText = "Escolha" };
        if (Obs.Filters(Source) is not { } filters)
            return new TileVisual(ObsIcon.Filter) { Caption = "Filtro", Big = Filter, BigDim = true, Detail = "Lendo os filtros…" };
        return filters.FirstOrDefault(f => f.Name == Filter) is { } filter
            ? Visual(filter, Source)
            : new TileVisual(ObsIcon.Filter) { Caption = "Filtro", Big = Filter, BigDim = true, Detail = $"Não está mais em {Source}", MiniText = "Sumiu" };
    }

    internal static TileVisual Visual(SourceFilter filter, string source) => filter.Enabled
        ? new TileVisual(ObsIcon.Filter) { Caption = "Ligado", Accent = TilePalette.Studio, Big = filter.Name, Detail = source, MiniText = "Ligado" }
        : new TileVisual(ObsIcon.Filter) { Caption = "Desligado", Big = filter.Name, Slashed = true, Detail = source, MiniText = "Desligado" };

    protected override void OnAwakeChanged(bool awake)
    {
        var want = awake ? Source : null;
        if (want == _watched)
            return;
        _watch?.Dispose();
        _watch = want is null ? null : Session.WatchFilters(want);
        _watched = want;
    }

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        if (Source is null || Current is not { } filter)
            return;
        switch (touch.Kind)
        {
            case TouchKind.Tap:
                Check(await Session.SetFilterEnabledAsync(Source, filter.Name, !filter.Enabled).ConfigureAwait(false));
                break;
            case TouchKind.LongPress:
                _restore = filter.Enabled;
                Check(await Session.SetFilterEnabledAsync(Source, filter.Name, !filter.Enabled).ConfigureAwait(false));
                break;
            case TouchKind.PressEnded when _restore is { } restore:
                _restore = null;
                await Session.SetFilterEnabledAsync(Source, filter.Name, restore).ConfigureAwait(false);
                break;
        }
    }

    public override ValueTask DisposeAsync()
    {
        _watch?.Dispose();
        _watch = null;
        return base.DisposeAsync();
    }
}
