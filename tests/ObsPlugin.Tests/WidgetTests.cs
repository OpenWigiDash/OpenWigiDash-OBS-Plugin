using System.Text.Json.Nodes;
using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Widgets;

namespace ObsPlugin.Tests;

/// <summary>O que cada tile mostra (sem desenhar: o desenho é GDI+, só Windows).</summary>
public class VisualTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Record()
    {
        var stopped = RecordInstance.Visual(new OutputStatus(), T0, mini: false, wide: false, RecordLongPress.Pause);
        Assert.Equal("Parado", stopped.Big);
        Assert.Null(stopped.Accent);

        var running = new OutputStatus { Active = true, Timecode = TimeSpan.FromSeconds(3723), ObservedAt = T0, Bytes = 2_400_000_000 };
        var rec = RecordInstance.Visual(running, T0 + TimeSpan.FromSeconds(2), mini: false, wide: true, RecordLongPress.Pause);
        Assert.Equal("01:02:05", rec.Big);
        Assert.Equal("1:02:05", rec.MiniText);
        Assert.Equal(TilePalette.Record, rec.Accent);
        Assert.Equal("Segure para pausar · 2,4 GB", rec.Detail);

        var paused = RecordInstance.Visual(running with { Paused = true }, T0 + TimeSpan.FromMinutes(5), false, false, RecordLongPress.Pause);
        Assert.Equal("01:02:03", paused.Big);
        Assert.Equal(TilePalette.Warn, paused.Accent);
        Assert.Equal(ObsIcon.Pause, paused.Icon);

        Assert.Equal("Segure para marcar capítulo", RecordInstance.Visual(running, T0, false, wide: false, RecordLongPress.Chapter).Detail);
    }

    [Fact]
    public void Stream()
    {
        Assert.Equal("Fora do ar", StreamInstance.Visual(new OutputStatus(), [], T0, wide: true).Big);

        var live = new OutputStatus { Active = true, Timecode = TimeSpan.FromMinutes(10), ObservedAt = T0, Kbps = 6000, TotalFrames = 1000, SkippedFrames = 3 };
        var wide = StreamInstance.Visual(live, [5900, 6000], T0, wide: true);
        Assert.Equal("Ao vivo", wide.Pill);
        Assert.Equal("00:10:00", wide.Big);
        Assert.Equal("6.000 kbps · 0,3% perdidos", wide.Detail);
        Assert.Equal(TilePalette.Ok, wide.SparkColor);
        Assert.NotNull(wide.Spark);

        Assert.Equal("Segure para encerrar", StreamInstance.Visual(live, [], T0, wide: false).Detail);
        Assert.Equal("Reconectando", StreamInstance.Visual(live with { Reconnecting = true }, [], T0, true).Pill);
    }

    [Theory]
    [InlineData(0, 1000, "Ok")]
    [InlineData(20, 1000, "Warn")]
    [InlineData(80, 1000, "Record")]
    public void StreamHealth(long skipped, long total, string color)
    {
        var expected = color switch { "Ok" => TilePalette.Ok, "Warn" => TilePalette.Warn, _ => TilePalette.Record };
        Assert.Equal(expected, StreamInstance.Health(new OutputStatus { Active = true, SkippedFrames = skipped, TotalFrames = total }));
    }

    [Fact]
    public void Replay()
    {
        Assert.Equal("Desligado", ReplayBufferInstance.Visual(false, null, null, T0).Big);
        var on = ReplayBufferInstance.Visual(true, null, null, T0);
        Assert.Equal("Salvar clipe", on.Big);
        Assert.False(on.Tinted);

        var saved = ReplayBufferInstance.Visual(true, T0, @"C:\Videos\Replay 2026-11-03 20-00-00.mkv", T0 + TimeSpan.FromSeconds(1));
        Assert.Equal("Salvo", saved.Big);
        Assert.Equal("Replay 2026-11-03 20-00-00.mkv", saved.Detail);
        Assert.Equal("Salvar clipe", ReplayBufferInstance.Visual(true, T0, null, T0 + TimeSpan.FromSeconds(5)).Big);
    }

    [Fact]
    public void VirtualCam()
    {
        Assert.Equal("Ligada", VirtualCamInstance.Visual(true).Big);
        Assert.Equal("Desligada", VirtualCamInstance.Visual(false).Big);
    }

    [Fact]
    public void Scene()
    {
        var program = SceneInstance.Visual("Jogo", program: true, preview: false, exists: true, thumbnail: null, showThumbnail: false);
        Assert.Equal("No ar", program.Caption);
        Assert.Equal(TilePalette.Ok, program.Ring);

        var preview = SceneInstance.Visual("Câmera", program: false, preview: true, exists: true, thumbnail: [1], showThumbnail: true);
        Assert.Equal(TileLayout.Thumbnail, preview.Layout);
        Assert.Equal("Prévia", preview.Pill);
        Assert.Equal(TilePalette.Preview, preview.Ring);

        var idle = SceneInstance.Visual("Intervalo", false, false, true, null, true);
        Assert.Null(idle.Pill);
        Assert.Null(idle.Ring);

        Assert.Equal("Não existe nesta coleção", SceneInstance.Visual("Velha", false, false, exists: false, null, false).Detail);
    }

    [Fact]
    public void Mixer()
    {
        var mic = new AudioInput("Microfone", "wasapi_input_capture") { VolumeDb = -6.5, Level = 0.5 };
        var on = MixerInstance.Visual(mic, MixerMode.Toggle, wide: true, mini: false);
        Assert.Equal(ObsIcon.Microphone, on.Icon);
        Assert.Equal("−6,5 dB", on.Big);
        Assert.Equal(0.5, on.Meter);
        Assert.True(on.Hints);

        var muted = MixerInstance.Visual(mic with { Muted = true }, MixerMode.Toggle, wide: false, mini: false);
        Assert.Equal("Mutado", muted.Big);
        Assert.True(muted.Slashed);
        Assert.Equal(0, muted.Meter);

        Assert.Null(MixerInstance.Visual(mic, MixerMode.Toggle, false, mini: true).Meter);
        Assert.Equal("Segure para falar", MixerInstance.Visual(mic, MixerMode.PushToTalk, false, false).Detail);
        Assert.Equal(ObsIcon.Speaker, MixerInstance.IconFor("wasapi_output_capture"));
        Assert.Equal(ObsIcon.Speaker, MixerInstance.IconFor("ffmpeg_source"));
        Assert.Equal("−∞ dB", MixerInstance.Db(-100));
        Assert.Equal("0,0 dB", MixerInstance.Db(0));
    }
}

/// <summary>Os tiles de verdade, ligados ao OBS de mentira. Não rodam em paralelo: trocam a sessão estática.</summary>
[Collection(nameof(TileTests))]
[CollectionDefinition(nameof(TileTests), DisableParallelization = true)]
public sealed class TileTests : IAsyncLifetime
{
    private FakeObs _obs = null!;
    private ObsSession _session = null!;

    public async Task InitializeAsync()
    {
        _obs = FakeObs.Start();
        _session = new ObsSession(TimeProvider.System, () => _obs.Endpoint, [TimeSpan.FromMilliseconds(100)]);
        ObsTileInstance.SessionOverride = _session;
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        ObsTileInstance.SessionOverride = null;
        await _session.DisposeAsync();
        await _obs.DisposeAsync();
    }

    private async Task<T> Create<T>(ObsWidgetBase widget, WidgetSize size, Action<FakeContext>? setup = null) where T : ObsTileInstance
    {
        var context = new FakeContext(size);
        setup?.Invoke(context);
        var instance = (T)await widget.CreateInstanceAsync(context, CancellationToken.None);
        await Wait.Until(() => _session.ConnectionState == ObsConnectionState.Ready && _session.State.AudioInputs.Count == 2, "OBS pronto");
        return instance;
    }

    private static TouchEvent Touch(TouchKind kind, int x = 50, double progress = 0) => new(kind, x, 50, TimeSpan.Zero, progress);

    [Fact]
    public async Task Offline_ShowsWhy()
    {
        ObsTileInstance.SessionOverride = new ObsSession(TimeProvider.System, () => new ObsEndpoint("127.0.0.1", FakeObs.FreePort(), null), [TimeSpan.FromMilliseconds(100)]);
        var instance = (RecordInstance)await new RecordWidget().CreateInstanceAsync(new FakeContext(WidgetSize.Small), CancellationToken.None);
        try
        {
            await Wait.Until(() => instance.CurrentVisual().Detail == "OBS fechado");
            Assert.Equal(TileLayout.Offline, instance.CurrentVisual().Layout);
        }
        finally
        {
            await instance.DisposeAsync();
            await instance.Session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Record_TapStarts_LongPressBarShows()
    {
        var record = await Create<RecordInstance>(new RecordWidget(), WidgetSize.Small);
        await record.OnTouchAsync(Touch(TouchKind.Tap), CancellationToken.None);
        await Wait.Until(() => record.CurrentVisual().Caption == "Gravando", "gravando");
        Assert.True(_obs.Recording);

        await record.OnTouchAsync(Touch(TouchKind.PressProgress, progress: 0.6), CancellationToken.None);
        Assert.Equal(0.6, record.CurrentVisual().Press);
        await record.OnTouchAsync(Touch(TouchKind.PressEnded), CancellationToken.None);
        Assert.Null(record.CurrentVisual().Press);
        await record.DisposeAsync();
    }

    [Fact]
    public async Task Record_ChapterRefused_FlashesTheReason()
    {
        var record = await Create<RecordInstance>(new RecordWidget(), WidgetSize.Small);
        await record.SetLongPressAsync(RecordLongPress.Chapter);
        _obs.Refuse.Add("CreateRecordChapter");
        await record.OnTouchAsync(Touch(TouchKind.Tap), CancellationToken.None);
        await Wait.Until(() => _session.State.Record.Active);

        await record.OnTouchAsync(Touch(TouchKind.LongPress), CancellationToken.None);
        var visual = record.CurrentVisual();
        Assert.Equal("Só em Hybrid MP4", visual.Detail);
        Assert.Equal(TilePalette.Record, visual.Accent);
        await record.DisposeAsync();
    }

    [Fact]
    public async Task Stream_TapWhileLive_DoesNotStop()
    {
        var stream = await Create<StreamInstance>(new StreamWidget(), WidgetSize.Wide);
        await stream.OnTouchAsync(Touch(TouchKind.Tap), CancellationToken.None);
        await Wait.Until(() => _session.State.Stream.Active, "ao vivo");

        await stream.OnTouchAsync(Touch(TouchKind.Tap), CancellationToken.None);
        Assert.Equal("Segure para encerrar", stream.CurrentVisual().Detail);
        Assert.True(_obs.Streaming);

        await stream.OnTouchAsync(Touch(TouchKind.LongPress), CancellationToken.None);
        await Wait.Until(() => !_session.State.Stream.Active, "fora do ar");
        Assert.False(_obs.Streaming);
        await stream.DisposeAsync();
    }

    [Fact]
    public async Task Scene_StudioMode_TapGoesToPreview_LongPressToProgram()
    {
        _obs.StudioMode = true;
        var scene = await Create<SceneInstance>(new SceneWidget(), WidgetSize.Small);
        await scene.SetSceneAsync("Intervalo");
        await Wait.Until(() => _session.State.StudioMode);

        await scene.OnTouchAsync(Touch(TouchKind.Tap), CancellationToken.None);
        await Wait.Until(() => scene.CurrentVisual().Caption == "Prévia", "prévia");
        Assert.Equal("Jogo", _obs.Program);

        await scene.OnTouchAsync(Touch(TouchKind.LongPress), CancellationToken.None);
        await Wait.Until(() => scene.CurrentVisual().Caption == "No ar", "no ar");
        Assert.Equal("Intervalo", _obs.Program);
        await scene.DisposeAsync();
    }

    [Fact]
    public async Task Scene_WideWatchesThumbnail_SmallDoesNot()
    {
        var wide = await Create<SceneInstance>(new SceneWidget(), WidgetSize.Wide);
        await wide.SetSceneAsync("Câmera");
        Assert.Contains("Câmera", _session.WatchedThumbnails);
        await Wait.Until(() => wide.CurrentVisual().Thumbnail is not null, "miniatura");

        await wide.SetShowThumbnailAsync(false);
        Assert.Empty(_session.WatchedThumbnails);
        await wide.DisposeAsync();

        var small = await Create<SceneInstance>(new SceneWidget(), WidgetSize.Small);
        await small.SetSceneAsync("Câmera");
        Assert.Empty(_session.WatchedThumbnails);
        await small.DisposeAsync();
    }

    [Fact]
    public async Task Mixer_ToggleHoldAndEdges()
    {
        var mixer = await Create<MixerInstance>(new MixerWidget(), WidgetSize.Wide);
        await mixer.SetInputAsync("Microfone");
        Assert.True(_session.MetersSubscribed || _obs.Received.Any(m => (int?)m["op"] == 3));

        // Toque no meio muta.
        await mixer.OnTouchAsync(Touch(TouchKind.Tap, x: 200), CancellationToken.None);
        await Wait.Until(() => _session.State.FindInput("Microfone")!.Muted, "mutar");

        // Segurar inverte só enquanto segura.
        await mixer.OnTouchAsync(Touch(TouchKind.LongPress, x: 200), CancellationToken.None);
        await Wait.Until(() => !_obs.Audio["Microfone"].Muted, "abrir enquanto segura");
        await mixer.OnTouchAsync(Touch(TouchKind.PressEnded, x: 200), CancellationToken.None);
        await Wait.Until(() => _obs.Audio["Microfone"].Muted, "volta a mutar");

        // Borda direita sobe 1 dB; esquerda desce.
        await mixer.OnTouchAsync(Touch(TouchKind.Tap, x: 395), CancellationToken.None);
        await Wait.Until(() => _obs.Audio["Microfone"].Db == -5, "+1 dB");
        await Wait.Until(() => _session.State.FindInput("Microfone")!.VolumeDb == -5);
        await mixer.OnTouchAsync(Touch(TouchKind.Tap, x: 5), CancellationToken.None);
        await Wait.Until(() => _obs.Audio["Microfone"].Db == -6, "-1 dB");
        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task Mixer_PushToTalk()
    {
        _obs.Audio["Microfone"] = (true, -6);
        var mixer = await Create<MixerInstance>(new MixerWidget(), WidgetSize.Small);
        await mixer.SetInputAsync("Microfone");
        await mixer.SetModeAsync(MixerMode.PushToTalk);

        await mixer.OnTouchAsync(Touch(TouchKind.PressStarted), CancellationToken.None);
        await Wait.Until(() => !_obs.Audio["Microfone"].Muted, "falar");
        await mixer.OnTouchAsync(Touch(TouchKind.PressEnded), CancellationToken.None);
        await Wait.Until(() => _obs.Audio["Microfone"].Muted, "mutar de novo");
        await mixer.DisposeAsync();
    }

    [Fact]
    public async Task Settings_SurviveANewInstance()
    {
        var context = new FakeContext(WidgetSize.Small);
        var first = (MixerInstance)await new MixerWidget().CreateInstanceAsync(context, CancellationToken.None);
        await first.SetInputAsync("Áudio do PC");
        await first.SetModeAsync(MixerMode.PushToMute);
        await first.SetStepAsync(2.5);
        await first.DisposeAsync();

        var second = (MixerInstance)await new MixerWidget().CreateInstanceAsync(context, CancellationToken.None);
        Assert.Equal("Áudio do PC", second.Input);
        Assert.Equal(MixerMode.PushToMute, second.Mode);
        Assert.Equal(2.5, second.StepDb);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Manifests_AreUnique()
    {
        ObsWidgetBase[] widgets = [new RecordWidget(), new StreamWidget(), new SceneWidget(), new MixerWidget(), new ReplayBufferWidget(), new VirtualCamWidget(),
            new ScenePanelWidget(), new StatusWidget(), new SourceVisibilityWidget(), new FilterWidget(), new StudioModeWidget(), new TransitionWidget(), new MediaWidget()];
        Assert.Equal(widgets.Length, widgets.Select(w => w.Manifest.Id).Distinct().Count());
        Assert.All(widgets, w => Assert.StartsWith("OBS: ", w.Manifest.Name));
        await Task.CompletedTask;
    }

    [Fact]
    public void CatalogIcons_FollowOnePattern()
    {
        // Todos no mesmo layout (ícone + nome curto), sem miniatura do tile, e cada um com ícone próprio.
        ObsWidgetBase[] widgets = [new RecordWidget(), new StreamWidget(), new SceneWidget(), new MixerWidget(), new ReplayBufferWidget(), new VirtualCamWidget(),
            new ScenePanelWidget(), new StatusWidget(), new SourceVisibilityWidget(), new FilterWidget(), new StudioModeWidget(), new TransitionWidget(), new MediaWidget()];
        Assert.All(widgets, w => Assert.Equal(TileLayout.Catalog, w.CatalogVisual.Layout));
        Assert.All(widgets, w => Assert.InRange(w.CatalogVisual.Big!.Length, 1, 14));
        Assert.Equal(widgets.Length, widgets.Select(w => w.CatalogVisual.Icon).Distinct().Count());
        Assert.Equal("Cenas", new ScenePanelWidget().CatalogVisual.Big);
        Assert.Equal("Câmera virtual", new VirtualCamWidget().CatalogVisual.Big);
    }
}
