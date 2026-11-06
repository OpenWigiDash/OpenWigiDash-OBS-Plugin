using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Settings;
using OpenWigiDash.ObsPlugin.Widgets;

[assembly: OpenWigiWidget(typeof(SceneCollectionWidget))]
[assembly: OpenWigiWidget(typeof(ProfileWidget))]

namespace OpenWigiDash.ObsPlugin.Widgets;

/// <summary>Todas as coleções de cenas numa grade; segurar troca para a coleção.</summary>
public sealed class SceneCollectionWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("da2377b7-483c-424d-9f44-52092dc58251"), "OBS: Coleção de cenas",
        "Todas as coleções de cenas numa grade, com a atual em verde. Segure para trocar (a troca recarrega todas as cenas e fontes).",
        WidgetSize.Wide, WidgetSize.Large) with { SupportsFullscreen = true };

    protected override string CatalogLabel => "Coleções";

    protected override TileVisual Preview => ConfigListInstance.Visual(
        ["Principal", "Podcast", "Gameplay", "Evento"], "Gameplay", switchingTo: null, switching: false, ConfigKind.SceneCollection,
        outputsActive: false, blockedText: null, requireHold: true, new PixelSize(400, 290), page: 0);

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new SceneCollectionInstance(context);
}

/// <summary>Todos os perfis numa grade; segurar troca de perfil, e com live ou gravação rodando fica bloqueado.</summary>
public sealed class ProfileWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("a2c96055-b24b-4d53-9610-77337600d348"), "OBS: Perfil",
        "Todos os perfis numa grade, com o atual em verde. Segure para trocar. Com live, gravação, replay ou câmera virtual ligados, a troca fica bloqueada.",
        WidgetSize.Wide, WidgetSize.Large) with { SupportsFullscreen = true };

    protected override TileVisual Preview => ConfigListInstance.Visual(
        ["Twitch", "YouTube", "Gravação 4K"], "Twitch", switchingTo: null, switching: false, ConfigKind.Profile,
        outputsActive: false, blockedText: null, requireHold: true, new PixelSize(400, 290), page: 0);

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new ProfileInstance(context);
}

public enum ConfigKind
{
    SceneCollection,
    Profile,
}

public sealed class SceneCollectionInstance(IWidgetContext context) : ConfigListInstance(context, ConfigKind.SceneCollection)
{
    protected override ObsIcon Icon => ObsIcon.Collection;
    protected override IReadOnlyList<string> Items => Obs.SceneCollections;
    protected override string? Current => Obs.CurrentSceneCollection;
    protected override bool ObsSwitching => Obs.SwitchingSceneCollection;
    protected override string? BlockedText => null;
    protected override Task<ObsResponse> SwitchAsync(string item) => Session.SetSceneCollectionAsync(item);
}

public sealed class ProfileInstance(IWidgetContext context) : ConfigListInstance(context, ConfigKind.Profile)
{
    protected override ObsIcon Icon => ObsIcon.Profile;
    protected override IReadOnlyList<string> Items => Obs.Profiles;
    protected override string? Current => Obs.CurrentProfile;
    protected override bool ObsSwitching => Obs.SwitchingProfile;
    protected override string? BlockedText => ProfileBlockedText(Obs.Stream.Active, Obs.Record.Active, Obs.ReplayBuffer, Obs.VirtualCam);
    protected override Task<ObsResponse> SwitchAsync(string item) => Session.SetProfileAsync(item);

    /// <summary>
    /// O perfil traz servidor, chave, encoder e pasta de gravação: trocar com uma saída ligada muda a saída no meio.
    /// O próprio OBS bloqueia no menu; aqui o aviso diz qual saída parar.
    /// </summary>
    internal static string? ProfileBlockedText(bool stream, bool record, bool replay, bool virtualCam)
    {
        var active = new List<string>(4);
        if (stream)
            active.Add("a live");
        if (record)
            active.Add("a gravação");
        if (replay)
            active.Add("o replay");
        if (virtualCam)
            active.Add("a câmera virtual");
        return active.Count switch
        {
            0 => null,
            1 => $"Pare {active[0]} para trocar",
            2 => $"Pare {active[0]} e {active[1]} para trocar",
            _ => "Pare as saídas do OBS para trocar",
        };
    }
}

/// <summary>
/// Grade de coleções ou perfis, no jeito do painel de cenas: a atual em verde, a que está entrando com anel azul.
/// A troca é pesada (recarrega cenas ou muda as saídas), então por padrão só vale segurando, e enquanto o OBS não
/// confirma o tile mostra "Trocando…" e ignora novos toques.
/// </summary>
public abstract class ConfigListInstance(IWidgetContext context, ConfigKind kind) : ObsTileInstance(context)
{
    internal const string RequireHoldKey = "requireHold";

    private int _page;
    private string? _switchingTo;

    public ConfigKind Kind { get; } = kind;

    /// <summary>Só troca segurando (padrão). Desligado, um toque já troca.</summary>
    public bool RequireHold { get; private set; } = true;

    protected abstract IReadOnlyList<string> Items { get; }
    protected abstract string? Current { get; }
    /// <summary>O OBS avisou que está trocando (pode ter sido pela tela dele).</summary>
    protected abstract bool ObsSwitching { get; }
    /// <summary>Motivo de não poder trocar agora, ou <c>null</c>.</summary>
    protected abstract string? BlockedText { get; }
    protected abstract Task<ObsResponse> SwitchAsync(string item);

    protected override bool HasLongPress => true;
    public override Type? OptionsComponent => typeof(ConfigOptions);

    internal int Page => _page;
    internal bool Busy => _switchingTo is not null || ObsSwitching;

    protected internal override async ValueTask LoadSettingsAsync(CancellationToken cancellationToken)
    {
        if (bool.TryParse(await Context.Settings.GetAsync(RequireHoldKey, cancellationToken).ConfigureAwait(false), out var hold))
            RequireHold = hold;
    }

    public async Task SetRequireHoldAsync(bool hold)
    {
        RequireHold = hold;
        await SaveAsync(RequireHoldKey, hold ? null : "false").ConfigureAwait(false);
    }

    protected override TileVisual GetVisual() =>
        Visual(Items, Current, _switchingTo, ObsSwitching, Kind, Obs.OutputsActive, BlockedText, RequireHold, Context.PixelSize, _page);

    internal static TileVisual Visual(IReadOnlyList<string> items, string? current, string? switchingTo, bool switching, ConfigKind kind,
        bool outputsActive, string? blockedText, bool requireHold, PixelSize size, int page)
    {
        var icon = kind == ConfigKind.Profile ? ObsIcon.Profile : ObsIcon.Collection;
        if (items.Count == 0)
        {
            return new TileVisual(icon)
            {
                Layout = TileLayout.Grid, Cells = [], Detail = kind == ConfigKind.Profile ? "Nenhum perfil" : "Nenhuma coleção",
            };
        }
        var body = GridLayout.Body(size);
        var (columns, rows) = GridLayout.Shape(body);
        var slice = GridLayout.Page(items.Count, columns * rows, page);
        var cells = new List<GridCell>(slice.Count + 1);
        for (var i = 0; i < slice.Count; i++)
        {
            var item = items[slice.Start + i];
            cells.Add(new GridCell(item,
                Fill: item == current && switchingTo is null ? TilePalette.Ok : null,
                Ring: item == switchingTo ? TilePalette.Preview : null));
        }
        if (slice.More)
        {
            var currentPage = slice.Start / Math.Max(1, columns * rows - 1) + 1;
            cells.Add(new GridCell($"Mais › {currentPage}/{slice.Pages}", More: true));
        }

        var (detail, accent) = (switchingTo is not null || switching, blockedText, outputsActive && kind == ConfigKind.SceneCollection) switch
        {
            (true, _, _) => ("Trocando…", TilePalette.Preview),
            (_, { } blocked, _) => (blocked, TilePalette.Record),
            (_, _, true) => (requireHold ? "Ao vivo: segure para trocar" : "Ao vivo: a troca recarrega as cenas", TilePalette.Warn),
            _ => (requireHold ? "Segure para trocar" : "Toque para trocar", (System.Drawing.Color?)null),
        };
        return new TileVisual(icon)
        {
            Layout = TileLayout.Grid, Cells = cells, GridFooter = true, Detail = detail, Accent = accent, Tinted = false,
        };
    }

    /// <summary>O item sob o dedo, <c>""</c> na célula "mais", <c>null</c> fora das células.</summary>
    internal static string? ItemAt(IReadOnlyList<string> items, PixelSize size, int page, int x, int y)
    {
        var body = GridLayout.Body(size);
        var (columns, rows) = GridLayout.Shape(body);
        if (GridLayout.HitTest(body, columns, rows, x, y) is not { } index)
            return null;
        var slice = GridLayout.Page(items.Count, columns * rows, page);
        if (index < slice.Count)
            return items[slice.Start + index];
        return slice.More && index == slice.Count ? "" : null;
    }

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        var items = Items;
        switch (touch.Kind)
        {
            case TouchKind.SwipeUp when !Context.IsFullscreen:
            case TouchKind.SwipeDown when Context.IsFullscreen:
                await Context.RequestFullscreenAsync(!Context.IsFullscreen, cancellationToken).ConfigureAwait(false);
                return;
            case TouchKind.SwipeLeft when Context.IsFullscreen:
                Turn(items, +1);
                return;
            case TouchKind.SwipeRight when Context.IsFullscreen:
                Turn(items, -1);
                return;
            case TouchKind.Tap or TouchKind.LongPress:
                break;
            default:
                return;
        }

        var item = ItemAt(items, Context.PixelSize, _page, touch.X, touch.Y);
        if (item == "")
        {
            if (touch.Kind == TouchKind.Tap)
                Turn(items, +1);
            return;
        }
        if (item is null || item == Current || Busy)
            return;
        if (touch.Kind == TouchKind.Tap && RequireHold)
        {
            Flash("Segure para trocar");
            return;
        }
        if (BlockedText is { } blocked)
        {
            Flash(blocked, ok: false);
            return;
        }

        _switchingTo = item;
        Context.RequestRender();
        ObsResponse response;
        try
        {
            response = await SwitchAsync(item).ConfigureAwait(false);
        }
        finally
        {
            _switchingTo = null;
            Context.RequestRender();
        }
        // Passou do prazo mas o OBS ainda está carregando: o "Trocando…" segue pelo evento, não é falha.
        if (!response.Ok && !ObsSwitching)
            Check(response, "OBS recusou a troca");
    }

    private void Turn(IReadOnlyList<string> items, int delta)
    {
        var (columns, rows) = GridLayout.Shape(GridLayout.Body(Context.PixelSize));
        var pages = GridLayout.Page(items.Count, columns * rows, 0).Pages;
        if (pages <= 1)
            return;
        _page = ((_page + delta) % pages + pages) % pages;
        Context.RequestRender();
    }

    public override ValueTask OnFullscreenChangedAsync(bool fullscreen, CancellationToken cancellationToken)
    {
        _page = 0;
        return base.OnFullscreenChangedAsync(fullscreen, cancellationToken);
    }
}
