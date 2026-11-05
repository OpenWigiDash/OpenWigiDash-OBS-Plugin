using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Settings;
using OpenWigiDash.ObsPlugin.Widgets;

[assembly: OpenWigiWidget(typeof(MediaWidget))]

namespace OpenWigiDash.ObsPlugin.Widgets;

/// <summary>Controle de uma fonte de mídia: play e pause, progresso e tempo restante. Segurar reinicia.</summary>
public sealed class MediaWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("b7e2d9a4-1c63-4f85-90a7-5d3c8e1f62b0"), "OBS: Mídia",
        "Toque dá play e pause numa fonte de mídia do OBS; segure para reiniciar. Mostra o progresso e o tempo restante.",
        WidgetSize.Small, WidgetSize.Wide);

    protected override TileVisual Preview => MediaInstance.Visual("Vinheta",
        new MediaStatus("OBS_MEDIA_STATE_PLAYING", 30_000, 12_000, DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch);

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new MediaInstance(context);
}

public sealed class MediaInstance(IWidgetContext context) : ObsTileInstance(context)
{
    internal const string InputKey = "input";

    /// <summary>Tipos de fonte que o OBS controla com play/pause.</summary>
    internal static readonly HashSet<string> MediaKinds = new(StringComparer.Ordinal) { "ffmpeg_source", "vlc_source" };

    private IDisposable? _watch;
    private string? _watched;

    public string? Input { get; private set; }

    public IReadOnlyList<InputInfo> AvailableInputs => [.. Obs.Inputs.Where(i => MediaKinds.Contains(i.Kind))];

    protected override ObsIcon Icon => ObsIcon.Play;
    protected override bool HasLongPress => true;
    protected override bool Animating => Obs.Media(Input) is { Playing: true };
    public override Type? OptionsComponent => typeof(MediaOptions);

    protected internal override async ValueTask LoadSettingsAsync(CancellationToken cancellationToken) =>
        Input = await Context.Settings.GetAsync(InputKey, cancellationToken).ConfigureAwait(false);

    public async Task SetInputAsync(string? input)
    {
        Input = string.IsNullOrWhiteSpace(input) ? null : input;
        await SaveAsync(InputKey, Input).ConfigureAwait(false);
        OnAwakeChanged(!IsAsleep);
    }

    protected override TileVisual GetVisual()
    {
        if (Input is null)
            return new TileVisual(ObsIcon.Play) { Caption = "Mídia", Big = "Escolha a mídia", BigDim = true, Detail = "Nas opções do widget" };
        if (Obs.Inputs.Count > 0 && Obs.Inputs.All(i => i.Name != Input))
            return new TileVisual(ObsIcon.Play) { Caption = "Mídia", Big = Input, BigDim = true, Detail = "Fonte não existe no OBS" };
        if (Obs.Media(Input) is not { } status)
            return new TileVisual(ObsIcon.Play) { Caption = "Mídia", Big = Input, BigDim = true, Detail = "Lendo…" };
        return Visual(Input, status, Now);
    }

    internal static TileVisual Visual(string name, MediaStatus status, DateTimeOffset now)
    {
        var position = status.Position(now);
        var remaining = status.Remaining(now);
        var time = remaining is { } r ? "−" + ShortClock(r.Max(TimeSpan.Zero))
            : position is { } p ? ShortClock(p) : null;
        if (status.Failed)
            return new TileVisual(ObsIcon.Play) { Caption = "Erro", Accent = TilePalette.Record, Big = name, Detail = "O OBS não abriu a mídia" };
        if (status.Playing)
        {
            return new TileVisual(ObsIcon.Play)
            {
                Caption = "Tocando", Accent = TilePalette.Ok, Big = name,
                Progress = status.Progress(now), ProgressText = time,
            };
        }
        if (status.Paused)
        {
            return new TileVisual(ObsIcon.Pause)
            {
                Caption = "Pausado", Accent = TilePalette.Warn, Tinted = false, Big = name,
                Progress = status.Progress(now), ProgressText = time,
            };
        }
        return new TileVisual(ObsIcon.Play) { Caption = "Parado", Big = name, Detail = "Toque para tocar" };
    }

    protected override void OnAwakeChanged(bool awake)
    {
        var want = awake ? Input : null;
        if (want == _watched)
            return;
        _watch?.Dispose();
        _watch = want is null ? null : Session.WatchMedia(want);
        _watched = want;
    }

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        if (Input is null)
            return;
        var status = Obs.Media(Input);
        var action = touch.Kind switch
        {
            TouchKind.Tap when status is { Playing: true } => MediaAction.Pause,
            TouchKind.Tap when status is { Paused: true } => MediaAction.Play,
            // Terminada ou parada, "play" não volta ao começo no OBS: reinicia.
            TouchKind.Tap => MediaAction.Restart,
            TouchKind.LongPress => MediaAction.Restart,
            _ => (MediaAction?)null,
        };
        if (action is { } a)
            Check(await Session.MediaActionAsync(Input, a).ConfigureAwait(false));
    }

    public override ValueTask DisposeAsync()
    {
        _watch?.Dispose();
        _watch = null;
        return base.DisposeAsync();
    }
}
