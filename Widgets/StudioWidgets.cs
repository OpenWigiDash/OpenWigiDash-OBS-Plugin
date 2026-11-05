using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Widgets;

[assembly: OpenWigiWidget(typeof(StudioModeWidget))]
[assembly: OpenWigiWidget(typeof(TransitionWidget))]

namespace OpenWigiDash.ObsPlugin.Widgets;

// ---------------------------------------------------------------------------------------------- Modo estúdio

/// <summary>Liga e desliga o modo estúdio (prévia e programa lado a lado no OBS).</summary>
public sealed class StudioModeWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("e3b8f5d0-6c27-4a19-8e4b-71d0a2c95b36"), "OBS: Modo estúdio",
        "Toque liga e desliga o modo estúdio do OBS.",
        WidgetSize.Mini, WidgetSize.Small);

    protected override TileVisual Preview => StudioModeInstance.Visual(true, "Câmera");

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new StudioModeInstance(context);
}

public sealed class StudioModeInstance(IWidgetContext context) : ObsTileInstance(context)
{
    protected override ObsIcon Icon => ObsIcon.Studio;

    protected override TileVisual GetVisual() => Visual(Obs.StudioMode, Obs.PreviewScene);

    internal static TileVisual Visual(bool on, string? preview) => on
        ? new TileVisual(ObsIcon.Studio)
        {
            Caption = "Modo estúdio", Accent = TilePalette.Studio, Big = "Ligado", MiniText = "Ligado",
            Detail = preview is null ? "Toque para desligar" : $"Prévia: {preview}",
        }
        : new TileVisual(ObsIcon.Studio) { Caption = "Modo estúdio", Big = "Desligado", BigDim = true, MiniText = "Desligado", Detail = "Toque para ligar" };

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        if (touch.Kind == TouchKind.Tap)
            Check(await Session.SetStudioModeAsync(!Obs.StudioMode).ConfigureAwait(false));
    }
}

// ---------------------------------------------------------------------------------------------- Transição

/// <summary>No modo estúdio, manda a prévia para o programa com a transição atual.</summary>
public sealed class TransitionWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("4f91a6c3-b2d8-47e0-9c15-83e6f0b27da4"), "OBS: Transição",
        "No modo estúdio, toque manda a prévia para o ar com a transição atual. Mostra qual cena vai entrar.",
        WidgetSize.Mini, WidgetSize.Small);

    protected override TileVisual Preview => TransitionInstance.Visual(true, "Câmera", "Fade", 300, inTransition: false);

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new TransitionInstance(context);
}

public sealed class TransitionInstance(IWidgetContext context) : ObsTileInstance(context)
{
    protected override ObsIcon Icon => ObsIcon.Transition;

    protected override TileVisual GetVisual() =>
        Visual(Obs.StudioMode, Obs.PreviewScene, Obs.Transition, Obs.TransitionDurationMs, Obs.InTransition);

    internal static TileVisual Visual(bool studio, string? preview, string? transition, double? durationMs, bool inTransition)
    {
        if (!studio)
            return new TileVisual(ObsIcon.Transition) { Caption = "Transição", Big = "Sem prévia", BigDim = true, MiniText = "Sem prévia", Detail = "Ligue o modo estúdio" };
        var name = transition is null ? "Transição"
            : durationMs is { } ms and > 0 && !IsCut(transition) ? $"{transition} · {Number(ms, 0)} ms" : transition;
        return new TileVisual(ObsIcon.Transition)
        {
            Caption = name,
            Accent = TilePalette.Studio,
            Tinted = inTransition,
            Big = preview ?? "—",
            MiniText = preview,
            Detail = inTransition ? "Em transição…" : "Toque para mandar ao ar",
        };
    }

    /// <summary>"Cut" e "Corte" não têm duração que valha mostrar.</summary>
    private static bool IsCut(string transition) => transition is "Cut" or "Corte" or "Cortar";

    protected override async Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        if (touch.Kind != TouchKind.Tap)
            return;
        if (!Obs.StudioMode)
        {
            Flash("Ligue o modo estúdio", ok: false);
            return;
        }
        Check(await Session.TriggerTransitionAsync().ConfigureAwait(false));
    }
}
