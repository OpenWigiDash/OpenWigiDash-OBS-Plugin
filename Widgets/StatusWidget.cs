using System.Drawing;
using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Widgets;

[assembly: OpenWigiWidget(typeof(StatusWidget))]

namespace OpenWigiDash.ObsPlugin.Widgets;

/// <summary>Saúde do OBS: CPU, FPS e quadros perdidos. Com o OBS fechado neste PC, segurar abre o OBS.</summary>
public sealed class StatusWidget : ObsWidgetBase
{
    public override WidgetManifest Manifest { get; } = CreateManifest(
        new Guid("c84e1b37-5a92-4d06-b3f8-0e6a7d21c59e"), "OBS: Status",
        "CPU, FPS e quadros perdidos do OBS, com gráfico da CPU no Wide. Com o OBS fechado, segure para abrir.",
        WidgetSize.Small, WidgetSize.Wide);

    protected override TileVisual Preview => StatusInstance.Visual(
        new ObsStats(4.2, 512, 60, 3.1, 2, 36_000, 0, 36_000), [3, 4, 5, 4, 6, 5, 4, 4, 5, 4],
        new OutputStatus { Active = true, Timecode = TimeSpan.FromMinutes(42), ObservedAt = DateTimeOffset.UnixEpoch },
        new OutputStatus(), DateTimeOffset.UnixEpoch, wide: false);

    protected override ObsTileInstance CreateInstance(IWidgetContext context) => new StatusInstance(context);
}

public sealed class StatusInstance(IWidgetContext context) : ObsTileInstance(context)
{
    /// <summary>Depois de abrir o OBS, tenta conectar nestes momentos (ele demora a subir o WebSocket).</summary>
    internal static readonly TimeSpan[] ReconnectAfterLaunch = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8)];

    private IDisposable? _watch;

    protected override ObsIcon Icon => ObsIcon.Stats;
    protected override bool Animating => Obs.Stream.Active || Obs.Record.Active;

    /// <summary>Só dá para abrir o OBS se ele está fechado, neste PC e instalado.</summary>
    protected override bool OfflineLongPress =>
        Session.ConnectionState == ObsConnectionState.Unavailable && Session.IsLocal && ObsLauncher.Executable is not null;

    protected override string OfflineText(ObsConnectionState state, string text) =>
        OfflineLongPress ? "Segure para abrir o OBS" : text;

    protected override TileVisual GetVisual() => Visual(Obs.Stats, Obs.CpuHistory, Obs.Stream, Obs.Record, Now, IsWide);

    internal static TileVisual Visual(ObsStats? stats, IReadOnlyList<double> cpu, OutputStatus stream, OutputStatus record,
        DateTimeOffset now, bool wide)
    {
        var visual = new TileVisual(ObsIcon.Stats) { Layout = TileLayout.Stats, Caption = "OBS", Detail = "CPU do OBS" };
        if (stream.Active)
            visual = visual with { Caption = null, Pill = $"Ao vivo {Clock(stream.Elapsed(now))}", Accent = TilePalette.Live, Tinted = false };
        else if (record.Active)
            visual = visual with { Caption = null, Pill = $"Gravando {Clock(record.Elapsed(now))}", Accent = TilePalette.Record, Tinted = false };

        if (stats is null)
            return visual with { Lines = [new StatLine("CPU", "…"), new StatLine("FPS", "…"), new StatLine("Perdidos", "…")] };

        var cpuLine = new StatLine("CPU", $"{Number(stats.CpuPercent, 1)} %", stats.CpuPercent >= 80 ? TilePalette.Record : stats.CpuPercent >= 50 ? TilePalette.Warn : null);
        var fpsLine = new StatLine("FPS", Number(stats.Fps, stats.Fps >= 100 ? 0 : 1));
        IReadOnlyList<StatLine> lines = wide
            ? [cpuLine, fpsLine, Dropped("Render perdidos", stats.RenderDroppedPercent), Dropped("Codificação perdidos", stats.OutputDroppedPercent)]
            : [cpuLine, fpsLine, Dropped("Perdidos", Math.Max(stats.RenderDroppedPercent, stats.OutputDroppedPercent))];
        return visual with { Lines = lines, Spark = cpu, SparkColor = TilePalette.Preview };
    }

    /// <summary>Até 1 % passa; acima de 1 % fica laranja, acima de 5 % vermelho.</summary>
    internal static StatLine Dropped(string label, double percent) =>
        new(label, $"{Number(percent, 1)} %", percent > 5 ? TilePalette.Record : percent > 1 ? TilePalette.Warn : (Color?)null);

    protected override void OnAwakeChanged(bool awake)
    {
        if (awake == (_watch is not null))
            return;
        _watch?.Dispose();
        _watch = awake ? Session.WatchStats() : null;
    }

    protected override Task OnReadyTouchAsync(TouchEvent touch, CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task OnOfflineTouchAsync(TouchEvent touch, CancellationToken cancellationToken)
    {
        if (touch.Kind != TouchKind.LongPress || !OfflineLongPress)
            return Task.CompletedTask;
        if (!ObsLauncher.Start())
        {
            Flash("Não achei o OBS", ok: false);
            return Task.CompletedTask;
        }
        Flash("Abrindo o OBS…");
        _ = ReconnectSoonAsync(Context.Stopping);
        return Task.CompletedTask;
    }

    /// <summary>O OBS leva uns segundos para abrir o WebSocket: tenta logo, sem esperar a espera longa da sessão.</summary>
    private async Task ReconnectSoonAsync(CancellationToken token)
    {
        try
        {
            var waited = TimeSpan.Zero;
            foreach (var at in ReconnectAfterLaunch)
            {
                await Task.Delay(at - waited, Context.Host.Time, token).ConfigureAwait(false);
                waited = at;
                if (Session.ConnectionState == ObsConnectionState.Ready)
                    return;
                Session.Reconnect();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override ValueTask DisposeAsync()
    {
        _watch?.Dispose();
        _watch = null;
        return base.DisposeAsync();
    }
}
