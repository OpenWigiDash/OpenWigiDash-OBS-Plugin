using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Settings;
using OpenWigiDash.ObsPlugin.Widgets;

[assembly: OpenWigiWidget(typeof(MixerWidget))]

namespace OpenWigiDash.ObsPlugin.Widgets;

/// <summary>
/// Uma fonte de áudio do mixer do OBS: mutar, push to talk ou push to mute, com o medidor ao vivo e o volume em dB.
/// No Wide, as bordas abaixam e aumentam o volume.
/// </summary>
public sealed class MixerWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("72707dbc-5668-43e3-8d3b-96feb0962a69"), "OBS: Mixer de áudio",
        "Muta uma fonte de áudio do OBS (ou push to talk / push to mute), com medidor ao vivo. No Wide, as bordas ajustam o volume.",
        WidgetSize.Mini, WidgetSize.Small, WidgetSize.Wide);

    protected override TileVisual Preview => MixerInstance.Visual(new AudioInput("Microfone", "wasapi_input_capture") { VolumeDb = -6, Level = 0.62 },
        MixerMode.Toggle, wide: false, mini: false);

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new MixerInstance(context);
}

/// <summary>O que o toque no mixer faz.</summary>
public enum MixerMode
{
    /// <summary>Toque muta e desmuta. Segurar inverte só enquanto o dedo está na tela.</summary>
    Toggle,
    /// <summary>Segurar abre; soltar muta.</summary>
    PushToTalk,
    /// <summary>Segurar muta; soltar volta.</summary>
    PushToMute,
}

public sealed class MixerInstance(IWidgetContext context) : ObsTileInstance(context)
{
    internal const string InputKey = "input";
    internal const string ModeKey = "mode";
    internal const string StepKey = "step";
    internal const double EdgeFraction = 0.18;

    private IDisposable? _meterWatch;
    private bool? _restoreMute;

    public string? Input { get; private set; }
    public MixerMode Mode { get; private set; } = MixerMode.Toggle;

    /// <summary>Quanto as bordas do Wide mudam o volume, em dB.</summary>
    public double StepDb { get; private set; } = 1;

    public IReadOnlyList<AudioInput> AvailableInputs => Obs.AudioInputs;

    protected override ObsIcon Icon => ObsIcon.Microphone;
    protected override bool HasLongPress => Mode == MixerMode.Toggle;
    public override Type? OptionsComponent => typeof(MixerOptions);

    protected internal override async ValueTask LoadSettingsAsync(CancellationToken cancellationToken)
    {
        Input = await Context.Settings.GetAsync(InputKey, cancellationToken).ConfigureAwait(false);
        if (Enum.TryParse<MixerMode>(await Context.Settings.GetAsync(ModeKey, cancellationToken).ConfigureAwait(false), out var mode))
            Mode = mode;
        if (double.TryParse(await Context.Settings.GetAsync(StepKey, cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture, out var step) && step is > 0 and <= 10)
            StepDb = step;
    }

    public Task SetInputAsync(string? input)
    {
        Input = string.IsNullOrWhiteSpace(input) ? null : input;
        return SaveAsync(InputKey, Input);
    }

    public Task SetModeAsync(MixerMode mode)
    {
        Mode = mode;
        return SaveAsync(ModeKey, mode.ToString());
    }

    public Task SetStepAsync(double step)
    {
        StepDb = Math.Clamp(step, 0.5, 10);
        return SaveAsync(StepKey, StepDb.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    protected override TileVisual GetVisual()
    {
        if (Input is null)
            return new TileVisual(ObsIcon.Microphone) { Caption = "Mixer", Big = "Escolha a fonte", BigDim = true, Detail = "Nas opções do widget" };
        if (Obs.FindInput(Input) is not { } input)
            return new TileVisual(ObsIcon.Microphone) { Caption = Input, Big = "Sumiu", BigDim = true, Detail = "Fonte não existe no OBS" };
        return Visual(input, Mode, IsWide, IsMini);
    }

    /// <summary>Microfone para captura de entrada; alto-falante para o resto (áudio do PC, mídia, navegador).</summary>
    internal static ObsIcon IconFor(string kind) =>
        kind.Contains("input_capture", StringComparison.Ordinal) || kind.Contains("_input", StringComparison.Ordinal)
            ? ObsIcon.Microphone : ObsIcon.Speaker;

    internal static string Db(double db) => db <= -99.5 ? "−∞ dB" : $"{Number(db, 1).Replace('-', '−')} dB";

    internal static TileVisual Visual(AudioInput input, MixerMode mode, bool wide, bool mini)
    {
        var detail = mode switch
        {
            MixerMode.PushToTalk => "Segure para falar",
            MixerMode.PushToMute => "Segure para mutar",
            _ => wide ? "Toque no meio muta · bordas ajustam" : null,
        };
        return new TileVisual(IconFor(input.Kind))
        {
            Layout = TileLayout.Mixer,
            Caption = input.Name,
            Big = input.Muted ? "Mutado" : Db(input.VolumeDb),
            Accent = input.Muted ? TilePalette.Record : null,
            Slashed = input.Muted,
            Meter = mini ? null : input.Muted ? 0 : input.Level,
            Detail = detail,
            Hints = wide,
        };
    }

    protected override void OnAwakeChanged(bool awake)
    {
        // Medidor só para quem desenha ele (o Mini não tem) e com a tela acordada.
        var want = awake && !IsMini;
        if (want == (_meterWatch is not null))
            return;
        _meterWatch?.Dispose();
        _meterWatch = want ? Session.WatchMeters() : null;
    }

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        if (Input is null || Obs.FindInput(Input) is not { } input)
            return;

        // No Wide, as bordas ajustam o volume em qualquer modo.
        var edge = IsWide ? EdgeOf(touch.X) : 0;
        if (edge != 0)
        {
            if (touch.Kind == TouchKind.Tap)
            {
                var from = input.VolumeDb <= -99.5 ? -60 : input.VolumeDb;
                Check(await Session.SetInputVolumeDbAsync(Input, Math.Min(0, from + edge * StepDb)).ConfigureAwait(false));
            }
            return;
        }

        switch (Mode, touch.Kind)
        {
            case (MixerMode.Toggle, TouchKind.Tap):
                Check(await Session.SetInputMuteAsync(Input, !input.Muted).ConfigureAwait(false));
                break;
            // Segurar no modo normal: inverte só enquanto o dedo está na tela (falar rapidinho com o mic mutado).
            case (MixerMode.Toggle, TouchKind.LongPress):
                _restoreMute = input.Muted;
                await Session.SetInputMuteAsync(Input, !input.Muted).ConfigureAwait(false);
                break;
            case (MixerMode.Toggle, TouchKind.PressEnded) when _restoreMute is { } restore:
                _restoreMute = null;
                await Session.SetInputMuteAsync(Input, restore).ConfigureAwait(false);
                break;
            case (MixerMode.PushToTalk, TouchKind.PressStarted):
                await Session.SetInputMuteAsync(Input, false).ConfigureAwait(false);
                break;
            case (MixerMode.PushToTalk, TouchKind.PressEnded):
                await Session.SetInputMuteAsync(Input, true).ConfigureAwait(false);
                break;
            case (MixerMode.PushToMute, TouchKind.PressStarted):
                _restoreMute = input.Muted;
                await Session.SetInputMuteAsync(Input, true).ConfigureAwait(false);
                break;
            case (MixerMode.PushToMute, TouchKind.PressEnded):
                await Session.SetInputMuteAsync(Input, _restoreMute ?? false).ConfigureAwait(false);
                _restoreMute = null;
                break;
        }
    }

    /// <summary>-1 na borda esquerda, +1 na direita, 0 no meio.</summary>
    internal int EdgeOf(int x)
    {
        var width = Context.PixelSize.Width;
        return x < width * EdgeFraction ? -1 : x > width * (1 - EdgeFraction) ? 1 : 0;
    }

    public override ValueTask DisposeAsync()
    {
        _meterWatch?.Dispose();
        _meterWatch = null;
        return base.DisposeAsync();
    }
}
