using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Settings;
using OpenWigiDash.ObsPlugin.Widgets;

[assembly: OpenWigiWidget(typeof(RecordWidget))]
[assembly: OpenWigiWidget(typeof(StreamWidget))]
[assembly: OpenWigiWidget(typeof(ReplayBufferWidget))]
[assembly: OpenWigiWidget(typeof(VirtualCamWidget))]

namespace OpenWigiDash.ObsPlugin.Widgets;

// ---------------------------------------------------------------------------------------------- Gravar

/// <summary>Grava ou para. O tile mostra o tempo; long-press pausa (ou marca capítulo, nas opções).</summary>
public sealed class RecordWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("9e8cc1a6-10af-47f1-b19f-9a3d40ed5a2d"), "OBS: Gravar",
        "Toque grava ou para; segure para pausar. Mostra o tempo de gravação.",
        WidgetSize.Mini, WidgetSize.Small, WidgetSize.Wide);

    protected override TileVisual Preview => RecordInstance.Visual(new OutputStatus { Active = true, Timecode = TimeSpan.FromSeconds(727) },
        DateTimeOffset.UnixEpoch, mini: false, wide: false, RecordLongPress.Pause) with { PulseDim = false };

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new RecordInstance(context);
}

/// <summary>O que o long-press do Gravar faz.</summary>
public enum RecordLongPress
{
    Pause,
    Chapter,
}

public sealed class RecordInstance(IWidgetContext context) : ObsTileInstance(context)
{
    internal const string LongPressKey = "longPress";

    public RecordLongPress LongPressAction { get; private set; } = RecordLongPress.Pause;

    protected override ObsIcon Icon => ObsIcon.Record;
    protected override bool HasLongPress => true;
    protected override bool Animating => Obs.Record.Active;
    public override Type? OptionsComponent => typeof(RecordOptions);

    protected internal override async ValueTask LoadSettingsAsync(CancellationToken cancellationToken)
    {
        if (Enum.TryParse<RecordLongPress>(await Context.Settings.GetAsync(LongPressKey, cancellationToken).ConfigureAwait(false), out var action))
            LongPressAction = action;
    }

    public Task SetLongPressAsync(RecordLongPress action)
    {
        LongPressAction = action;
        return SaveAsync(LongPressKey, action.ToString());
    }

    protected override TileVisual GetVisual() => Visual(Obs.Record, Now, IsMini, IsWide, LongPressAction);

    internal static TileVisual Visual(OutputStatus record, DateTimeOffset now, bool mini, bool wide, RecordLongPress longPress)
    {
        if (!record.Active)
            return new TileVisual(ObsIcon.Record) { Caption = "Gravar", Big = "Parado", BigDim = true, Detail = "Toque para gravar" };
        var elapsed = record.Elapsed(now);
        if (record.Paused)
        {
            return new TileVisual(ObsIcon.Pause)
            {
                Caption = "Pausado", Accent = TilePalette.Warn, Big = Clock(elapsed), MiniText = ShortClock(elapsed),
                Detail = longPress == RecordLongPress.Pause ? "Segure para continuar" : "Pausado no OBS",
            };
        }
        var size = record.Bytes > 0 ? $" · {Number(record.Bytes / 1e9, 1)} GB" : "";
        return new TileVisual(ObsIcon.Record)
        {
            Caption = "Gravando",
            Accent = TilePalette.Record,
            Big = Clock(elapsed),
            MiniText = ShortClock(elapsed),
            // O ponto pisca: meio segundo aceso, meio apagado.
            PulseDim = now.ToUnixTimeMilliseconds() / 700 % 2 == 1,
            Detail = (longPress == RecordLongPress.Pause ? "Segure para pausar" : "Segure para marcar capítulo") + (wide ? size : ""),
        };
    }

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        switch (touch.Kind)
        {
            case TouchKind.Tap:
                Check(await Session.ToggleRecordAsync().ConfigureAwait(false));
                break;
            case TouchKind.LongPress when Obs.Record.Active && LongPressAction == RecordLongPress.Pause:
                Check(await Session.ToggleRecordPauseAsync().ConfigureAwait(false));
                break;
            case TouchKind.LongPress when Obs.Record.Active:
                // Capítulo só existe gravando em Hybrid MP4 (OBS 30.2+); o OBS responde erro nos outros formatos.
                if (Check(await Session.CreateRecordChapterAsync().ConfigureAwait(false), "Só em Hybrid MP4"))
                    Flash("Capítulo marcado");
                break;
        }
    }
}

// ---------------------------------------------------------------------------------------------- Live

/// <summary>Inicia a live com um toque; encerrar pede long-press. Mostra tempo, kbps e quadros perdidos.</summary>
public sealed class StreamWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("122705ce-1425-4b9d-b5ed-23fd0b12ea3d"), "OBS: Live",
        "Toque inicia a live; segure para encerrar. Mostra o tempo no ar, o bitrate e os quadros perdidos.",
        WidgetSize.Mini, WidgetSize.Small, WidgetSize.Wide);

    protected override TileVisual Preview => StreamInstance.Visual(
        new OutputStatus { Active = true, Timecode = TimeSpan.FromSeconds(3753), Kbps = 6000, TotalFrames = 1000 },
        [5800, 5950, 6100, 6000, 5900, 6050, 6000], DateTimeOffset.UnixEpoch, wide: true);

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new StreamInstance(context);
}

public sealed class StreamInstance(IWidgetContext context) : ObsTileInstance(context)
{
    protected override ObsIcon Icon => ObsIcon.Live;
    protected override bool HasLongPress => true;
    protected override bool Animating => Obs.Stream.Active;

    protected override TileVisual GetVisual() => Visual(Obs.Stream, Obs.KbpsHistory, Now, IsWide);

    /// <summary>Saúde da live pelos quadros perdidos: até 1% verde, até 5% amarelo, acima vermelho.</summary>
    internal static System.Drawing.Color Health(OutputStatus stream) =>
        stream.Reconnecting || stream.DroppedPercent > 5 ? TilePalette.Record
        : stream.DroppedPercent > 1 ? TilePalette.Warn
        : TilePalette.Ok;

    internal static TileVisual Visual(OutputStatus stream, IReadOnlyList<double> history, DateTimeOffset now, bool wide)
    {
        if (!stream.Active)
            return new TileVisual(ObsIcon.Live) { Caption = "Live", Big = "Fora do ar", BigDim = true, Detail = "Toque para iniciar" };
        var elapsed = stream.Elapsed(now);
        var kbps = stream.Kbps is { } rate ? $"{Number(rate, 0)} kbps" : "… kbps";
        var dropped = $"{Number(stream.DroppedPercent, 1)}% perdidos";
        if (stream.Reconnecting)
        {
            return new TileVisual(ObsIcon.Live)
            {
                Pill = "Reconectando", Accent = TilePalette.Warn, Big = Clock(elapsed), MiniText = "Caiu",
                Detail = "O OBS está reconectando",
            };
        }
        return new TileVisual(ObsIcon.Live)
        {
            Pill = "Ao vivo",
            Accent = TilePalette.Live,
            Big = Clock(elapsed),
            MiniText = ShortClock(elapsed),
            Detail = wide ? $"{kbps} · {dropped}" : "Segure para encerrar",
            Spark = wide && history.Count > 1 ? history : null,
            SparkColor = wide ? Health(stream) : null,
        };
    }

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        switch (touch.Kind)
        {
            case TouchKind.Tap when !Obs.Stream.Active:
                Check(await Session.StartStreamAsync().ConfigureAwait(false), "Não iniciou");
                break;
            case TouchKind.Tap:
                Flash("Segure para encerrar");
                break;
            case TouchKind.LongPress when Obs.Stream.Active:
                Check(await Session.StopStreamAsync().ConfigureAwait(false));
                break;
        }
    }
}

// ---------------------------------------------------------------------------------------------- Replay buffer

/// <summary>Salva o replay (liga o buffer se estiver desligado); long-press desliga o buffer.</summary>
public sealed class ReplayBufferWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("b1a48408-c7e4-466d-a631-9d22afbbccf9"), "OBS: Replay buffer",
        "Toque salva o replay (e liga o buffer se estiver desligado); segure para desligar o buffer.",
        WidgetSize.Mini, WidgetSize.Small);

    protected override TileVisual Preview => ReplayBufferInstance.Visual(true, null, null, DateTimeOffset.UnixEpoch);

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new ReplayBufferInstance(context);
}

public sealed class ReplayBufferInstance(IWidgetContext context) : ObsTileInstance(context)
{
    private static readonly TimeSpan SavedLength = TimeSpan.FromSeconds(2.5);

    protected override ObsIcon Icon => ObsIcon.Replay;
    protected override bool HasLongPress => true;
    protected override bool Animating => Obs.LastReplayAt is { } at && Now - at < SavedLength + TimeSpan.FromSeconds(1);

    protected override TileVisual GetVisual() => Visual(Obs.ReplayBuffer, Obs.LastReplayAt, Obs.LastReplayPath, Now);

    internal static TileVisual Visual(bool on, DateTimeOffset? savedAt, string? savedPath, DateTimeOffset now)
    {
        if (savedAt is { } at && now - at < SavedLength)
        {
            return new TileVisual(ObsIcon.Check)
            {
                Caption = "Replay ligado", Accent = TilePalette.Ok, Big = "Salvo", MiniText = "Salvo",
                Detail = FileName(savedPath),
            };
        }
        return on
            ? new TileVisual(ObsIcon.Replay) { Caption = "Replay ligado", Accent = TilePalette.Ok, Tinted = false, Big = "Salvar clipe", Detail = "Segure para desligar" }
            : new TileVisual(ObsIcon.Replay) { Caption = "Replay", Big = "Desligado", BigDim = true, Detail = "Toque para ligar" };
    }

    /// <summary>Só o nome do arquivo. O OBS manda o caminho com "/" ou "\\" conforme a configuração.</summary>
    internal static string? FileName(string? path) =>
        path is null ? null : path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        switch (touch.Kind)
        {
            case TouchKind.Tap when !Obs.ReplayBuffer:
                Check(await Session.StartReplayBufferAsync().ConfigureAwait(false), "Ative no OBS: Saída > Replay");
                break;
            case TouchKind.Tap:
                Check(await Session.SaveReplayBufferAsync().ConfigureAwait(false), "Não salvou");
                break;
            case TouchKind.LongPress when Obs.ReplayBuffer:
                Check(await Session.StopReplayBufferAsync().ConfigureAwait(false));
                break;
        }
    }
}

// ---------------------------------------------------------------------------------------------- Câmera virtual

/// <summary>Liga e desliga a câmera virtual do OBS.</summary>
public sealed class VirtualCamWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("b2ec4796-e4f9-44a2-b89d-094ea325f551"), "OBS: Câmera virtual",
        "Toque liga e desliga a câmera virtual do OBS.",
        WidgetSize.Mini, WidgetSize.Small);

    protected override TileVisual Preview => VirtualCamInstance.Visual(true);

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new VirtualCamInstance(context);
}

public sealed class VirtualCamInstance(IWidgetContext context) : ObsTileInstance(context)
{
    protected override ObsIcon Icon => ObsIcon.VirtualCam;

    protected override TileVisual GetVisual() => Visual(Obs.VirtualCam);

    internal static TileVisual Visual(bool on) => on
        ? new TileVisual(ObsIcon.VirtualCam) { Caption = "Câmera virtual", Accent = TilePalette.Preview, Big = "Ligada", MiniText = "Ligada", Detail = "Toque para desligar" }
        : new TileVisual(ObsIcon.VirtualCam) { Caption = "Câmera virtual", Big = "Desligada", BigDim = true, Detail = "Toque para ligar" };

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        if (touch.Kind == TouchKind.Tap)
            Check(await Session.ToggleVirtualCamAsync().ConfigureAwait(false));
    }
}
