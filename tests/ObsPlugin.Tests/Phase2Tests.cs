using System.Text.Json.Nodes;
using OpenWigi.Sdk;
using OpenWigiDash.ObsPlugin.Obs;
using OpenWigiDash.ObsPlugin.Rendering;
using OpenWigiDash.ObsPlugin.Widgets;

namespace ObsPlugin.Tests;

/// <summary>Fase 2: estado de itens de cena, filtros, mídia, estatísticas e transição.</summary>
public class SourcesStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SceneItems_AreTopFirst_AndEnableEventUpdatesOne()
    {
        var state = new ObsState(new ManualTime(T0));
        Assert.Null(state.SceneItems("Jogo"));
        state.ApplySceneItems("Jogo", Wait.Json("""
            {"sceneItems":[
              {"sceneItemId":1,"sourceName":"Captura","inputKind":"game_capture","sceneItemEnabled":true,"isGroup":false,"sceneItemIndex":0},
              {"sceneItemId":2,"sourceName":"Webcam","inputKind":"dshow_input","sceneItemEnabled":false,"isGroup":false,"sceneItemIndex":1}]}
            """));
        Assert.Equal(["Webcam", "Captura"], state.SceneItems("Jogo")!.Select(i => i.SourceName));

        Assert.True(state.ApplyEvent("SceneItemEnableStateChanged", Wait.Json("""{"sceneName":"Jogo","sceneItemId":2,"sceneItemEnabled":true}""")).Changed);
        Assert.True(state.SceneItems("Jogo")![0].Enabled);
        // Cena que ninguém acompanha: ignora.
        Assert.False(state.ApplyEvent("SceneItemEnableStateChanged", Wait.Json("""{"sceneName":"Outra","sceneItemId":2,"sceneItemEnabled":true}""")).Changed);
        // Item novo: pede a lista de novo.
        Assert.Equal("Jogo", state.ApplyEvent("SceneItemCreated", Wait.Json("""{"sceneName":"Jogo","sourceName":"Texto","sceneItemId":3}""")).SceneItemsOf);
    }

    [Fact]
    public void Filters_EnableEvent_AndListChange()
    {
        var state = new ObsState(new ManualTime(T0));
        state.ApplyFilters("Webcam", Wait.Json("""{"filters":[{"filterName":"Cor","filterKind":"color_filter_v2","filterEnabled":false,"filterIndex":0}]}"""));
        Assert.True(state.ApplyEvent("SourceFilterEnableStateChanged", Wait.Json("""{"sourceName":"Webcam","filterName":"Cor","filterEnabled":true}""")).Changed);
        Assert.True(state.Filters("Webcam")![0].Enabled);
        Assert.Equal("Webcam", state.ApplyEvent("SourceFilterRemoved", Wait.Json("""{"sourceName":"Webcam","filterName":"Cor"}""")).FiltersOf);
    }

    [Fact]
    public void Media_CursorRunsOnlyWhilePlaying_AndActionGuessesTheState()
    {
        var time = new ManualTime(T0);
        var state = new ObsState(time);
        Assert.True(state.ApplyMedia("Vinheta", Wait.Json("""{"mediaState":"OBS_MEDIA_STATE_PLAYING","mediaDuration":30000,"mediaCursor":10000}""")).Changed);
        time.Advance(TimeSpan.FromSeconds(5));
        var media = state.Media("Vinheta")!;
        Assert.Equal(TimeSpan.FromSeconds(15), media.Position(time.Now));
        Assert.Equal(TimeSpan.FromSeconds(15), media.Remaining(time.Now));
        Assert.Equal(0.5, media.Progress(time.Now));

        // Leitura nova com o cursor onde se esperava: não redesenha à toa.
        Assert.False(state.ApplyMedia("Vinheta", Wait.Json("""{"mediaState":"OBS_MEDIA_STATE_PLAYING","mediaDuration":30000,"mediaCursor":15200}""")).Changed);
        // Pulou: redesenha.
        Assert.True(state.ApplyMedia("Vinheta", Wait.Json("""{"mediaState":"OBS_MEDIA_STATE_PLAYING","mediaDuration":30000,"mediaCursor":2000}""")).Changed);

        var pause = state.ApplyEvent("MediaInputActionTriggered", Wait.Json("""{"inputName":"Vinheta","mediaAction":"OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PAUSE"}"""));
        Assert.True(pause.Changed);
        Assert.Equal("Vinheta", pause.MediaOf);
        Assert.True(state.Media("Vinheta")!.Paused);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(2), state.Media("Vinheta")!.Position(time.Now)); // pausado não anda
    }

    [Fact]
    public void Stats_KeepCpuHistory()
    {
        var state = new ObsState(new ManualTime(T0));
        for (var i = 0; i < ObsState.StatsHistoryLength + 5; i++)
            state.ApplyStats(Wait.Json($$"""{"cpuUsage":{{i}},"activeFps":60,"renderSkippedFrames":10,"renderTotalFrames":1000}"""));
        Assert.Equal(ObsState.StatsHistoryLength, state.CpuHistory.Count);
        Assert.Equal(ObsState.StatsHistoryLength + 4, state.CpuHistory[^1]);
        Assert.Equal(1, state.Stats!.RenderDroppedPercent);
    }

    [Fact]
    public void Transition_NameDurationAndRunning()
    {
        var state = new ObsState(new ManualTime(T0));
        state.ApplyTransition(Wait.Json("""{"transitionName":"Fade","transitionDuration":300}"""));
        Assert.Equal("Fade", state.Transition);
        Assert.True(state.ApplyEvent("CurrentSceneTransitionChanged", Wait.Json("""{"transitionName":"Cortar"}""")).NeedsTransition);
        state.ApplyEvent("SceneTransitionStarted", Wait.Json("""{"transitionName":"Cortar"}"""));
        Assert.True(state.InTransition);
        state.ApplyEvent("SceneTransitionEnded", Wait.Json("""{"transitionName":"Cortar"}"""));
        Assert.False(state.InTransition);
    }
}

/// <summary>O desenho (sem desenhar) dos widgets da fase 2 e a geometria da grade.</summary>
public class SourcesVisualTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Grid_ShapeAndPaging()
    {
        Assert.Equal((3, 4), GridLayout.Shape(new PixelSize(400, 290)));
        Assert.Equal((0, 5, false, 1), GridLayout.Page(5, 12, 0));
        // 25 cenas em 12 células: 11 por página, 3 páginas; a página dá a volta.
        Assert.Equal((11, 11, true, 3), GridLayout.Page(25, 12, 1));
        Assert.Equal((22, 3, true, 3), GridLayout.Page(25, 12, 2));
        Assert.Equal((0, 11, true, 3), GridLayout.Page(25, 12, 3));
        Assert.Equal((22, 3, true, 3), GridLayout.Page(25, 12, -1));

        var size = new PixelSize(400, 290);
        var cell = GridLayout.Cell(size, 3, 4, 4);
        Assert.Equal(4, GridLayout.HitTest(size, 3, 4, (int)(cell.X + cell.Width / 2), (int)(cell.Y + cell.Height / 2)));
        Assert.Null(GridLayout.HitTest(size, 3, 4, 500, 10));
    }

    [Fact]
    public void ScenePanel_ProgramPreviewAndMore()
    {
        var size = new PixelSize(400, 290);
        string[] few = ["A", "B", "C"];
        var visual = ScenePanelInstance.Visual(few, program: "A", preview: "B", studio: true, size, page: 0);
        Assert.Equal(TileLayout.Grid, visual.Layout);
        Assert.Equal(TilePalette.Ok, visual.Cells![0].Fill);
        Assert.Equal(TilePalette.Preview, visual.Cells[1].Ring);
        Assert.Null(visual.Cells[2].Fill ?? visual.Cells[2].Ring);
        // Sem modo estúdio a prévia não aparece.
        Assert.Null(ScenePanelInstance.Visual(few, "A", "B", studio: false, size, 0).Cells![1].Ring);

        var many = Enumerable.Range(1, 25).Select(i => $"Cena {i}").ToArray();
        var page2 = ScenePanelInstance.Visual(many, "Cena 13", null, false, size, page: 1);
        Assert.Equal(12, page2.Cells!.Count);
        Assert.Equal("Cena 12", page2.Cells[0].Text);
        Assert.Equal(TilePalette.Ok, page2.Cells[1].Fill);
        Assert.True(page2.Cells[^1].More);
        Assert.Equal("Mais › 2/3", page2.Cells[^1].Text);

        var last = GridLayout.Cell(size, 3, 4, 11);
        Assert.Equal("", ScenePanelInstance.SceneAt(many, size, 0, (int)last.X + 5, (int)last.Y + 5));
        var first = GridLayout.Cell(size, 3, 4, 0);
        Assert.Equal("Cena 12", ScenePanelInstance.SceneAt(many, size, 1, (int)first.X + 5, (int)first.Y + 5));
        // Página com 3 cenas: a 4ª célula é "mais", a 5ª é vazia.
        var fifth = GridLayout.Cell(size, 3, 4, 4);
        Assert.Null(ScenePanelInstance.SceneAt(many, size, 2, (int)fifth.X + 5, (int)fifth.Y + 5));

        Assert.Equal("Nenhuma cena", ScenePanelInstance.Visual([], null, null, false, size, 0).Detail);
    }

    [Fact]
    public void SourceVisibility_AndFilter()
    {
        var shown = SourceVisibilityInstance.Visual(new SceneItem(1, "Webcam", "dshow_input", true, false), "Jogo", mini: false);
        Assert.Equal("Visível", shown.Caption);
        Assert.Equal(ObsIcon.VirtualCam, shown.Icon);
        var hidden = SourceVisibilityInstance.Visual(new SceneItem(1, "Webcam", "dshow_input", false, false), "Jogo", mini: false);
        Assert.True(hidden.Slashed);
        Assert.Null(hidden.Accent);
        Assert.Equal(ObsIcon.Monitor, SourceVisibilityInstance.IconFor("game_capture"));
        Assert.Equal(ObsIcon.Globe, SourceVisibilityInstance.IconFor("browser_source"));
        Assert.Equal(ObsIcon.Scene, SourceVisibilityInstance.IconFor(null));

        Assert.Equal(TilePalette.Studio, FilterInstance.Visual(new SourceFilter("Cor", "color_filter_v2", true), "Webcam").Accent);
        Assert.True(FilterInstance.Visual(new SourceFilter("Cor", "color_filter_v2", false), "Webcam").Slashed);
    }

    [Fact]
    public void Media()
    {
        var playing = MediaInstance.Visual("Vinheta", new MediaStatus("OBS_MEDIA_STATE_PLAYING", 90_000, 30_000, T0), T0 + TimeSpan.FromSeconds(15));
        Assert.Equal("Tocando", playing.Caption);
        Assert.Equal(0.5, playing.Progress);
        Assert.Equal("−00:45", playing.ProgressText);
        var paused = MediaInstance.Visual("Vinheta", new MediaStatus("OBS_MEDIA_STATE_PAUSED", 90_000, 30_000, T0), T0 + TimeSpan.FromMinutes(1));
        Assert.Equal("−01:00", paused.ProgressText);
        Assert.Equal(ObsIcon.Pause, paused.Icon);
        Assert.Equal("Parado", MediaInstance.Visual("Vinheta", new MediaStatus("OBS_MEDIA_STATE_ENDED", 90_000, 90_000, T0), T0).Caption);
        Assert.Equal("Erro", MediaInstance.Visual("Vinheta", new MediaStatus("OBS_MEDIA_STATE_ERROR", null, null, T0), T0).Caption);
    }

    [Fact]
    public void StudioAndTransition()
    {
        Assert.Equal("Prévia: Câmera", StudioModeInstance.Visual(true, "Câmera").Detail);
        Assert.Equal("Desligado", StudioModeInstance.Visual(false, null).Big);

        Assert.Equal("Sem prévia", TransitionInstance.Visual(false, null, "Fade", 300, false).Big);
        var fade = TransitionInstance.Visual(true, "Câmera", "Fade", 300, inTransition: false);
        Assert.Equal("Fade · 300 ms", fade.Caption);
        Assert.Equal("Câmera", fade.Big);
        Assert.False(fade.Tinted);
        Assert.Equal("Cortar", TransitionInstance.Visual(true, "Câmera", "Cortar", 0, false).Caption);
        Assert.Equal("Em transição…", TransitionInstance.Visual(true, "Câmera", "Fade", 300, inTransition: true).Detail);
    }

    [Fact]
    public void Status()
    {
        var stats = new ObsStats(4.26, 512, 60, 3, 30, 1000, 80, 1000);
        var small = StatusInstance.Visual(stats, [1, 2], new OutputStatus(), new OutputStatus(), T0, wide: false);
        Assert.Equal("OBS", small.Caption);
        Assert.Equal(["CPU", "FPS", "Perdidos"], small.Lines!.Select(l => l.Label));
        Assert.Equal("4,3 %", small.Lines![0].Value);
        Assert.Equal("8,0 %", small.Lines[2].Value); // o pior dos dois
        Assert.Equal(TilePalette.Record, small.Lines[2].Color);

        var live = new OutputStatus { Active = true, Timecode = TimeSpan.FromMinutes(10), ObservedAt = T0 };
        var wide = StatusInstance.Visual(stats, [1, 2], live, new OutputStatus(), T0, wide: true);
        Assert.Equal("Ao vivo 00:10:00", wide.Pill);
        Assert.Equal(4, wide.Lines!.Count);
        Assert.Equal(TilePalette.Warn, wide.Lines[2].Color); // render 3 %
        Assert.Equal("…", StatusInstance.Visual(null, [], new OutputStatus(), new OutputStatus(), T0, false).Lines![0].Value);

        Assert.Null(StatusInstance.Dropped("x", 1).Color);
        Assert.Equal(TilePalette.Warn, StatusInstance.Dropped("x", 1.1).Color);
    }
}

/// <summary>Os tiles da fase 2 contra o OBS de mentira (mesma coleção dos outros: trocam a sessão estática).</summary>
[Collection(nameof(TileTests))]
public sealed class SourcesTileTests : IAsyncLifetime
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
        ObsLauncher.FindOverride = null;
        ObsLauncher.StartOverride = null;
        await _session.DisposeAsync();
        await _obs.DisposeAsync();
    }

    private async Task<T> Create<T>(ObsWidgetBase widget, WidgetSize size, FakeContext? context = null) where T : ObsTileInstance
    {
        var instance = (T)await widget.CreateInstanceAsync(context ?? new FakeContext(size), CancellationToken.None);
        await Wait.Until(() => _session.ConnectionState == ObsConnectionState.Ready && _session.State.AudioInputs.Count == 2, "OBS pronto");
        return instance;
    }

    private static TouchEvent Touch(TouchKind kind, int x = 50, int y = 50) => new(kind, x, y, TimeSpan.Zero, kind == TouchKind.LongPress ? 1 : 0);

    [Fact]
    public async Task SourceVisibility_TapToggles_HoldFlipsOnlyWhileHeld()
    {
        var tile = await Create<SourceVisibilityInstance>(new SourceVisibilityWidget(), WidgetSize.Small);
        await tile.SetSceneAsync("Jogo");
        Assert.Contains("Jogo", _session.WatchedSceneItems);
        await Wait.Until(() => tile.AvailableSources.Count == 2, "itens da cena");
        Assert.Equal("Webcam", tile.AvailableSources[0].SourceName);
        await tile.SetSourceAsync("Webcam");
        Assert.Equal("Visível", tile.CurrentVisual().Caption);

        await tile.OnTouchAsync(Touch(TouchKind.Tap), CancellationToken.None);
        await Wait.Until(() => tile.CurrentVisual().Caption == "Escondida", "esconder");
        Assert.False(_obs.Items["Jogo"][0].Enabled);

        await tile.OnTouchAsync(Touch(TouchKind.LongPress), CancellationToken.None);
        await Wait.Until(() => _obs.Items["Jogo"][0].Enabled, "mostrar enquanto segura");
        await tile.OnTouchAsync(Touch(TouchKind.PressEnded), CancellationToken.None);
        await Wait.Until(() => !_obs.Items["Jogo"][0].Enabled, "volta a esconder");

        // Trocar de cena esquece a fonte.
        await tile.SetSceneAsync("Câmera");
        Assert.Null(tile.Source);
        await tile.DisposeAsync();
        Assert.Empty(_session.WatchedSceneItems);
    }

    [Fact]
    public async Task Filter_TapToggles()
    {
        var tile = await Create<FilterInstance>(new FilterWidget(), WidgetSize.Mini);
        Assert.Contains("Webcam", tile.AvailableSources);
        Assert.Contains("Jogo", tile.AvailableSources);
        await tile.SetSourceAsync("Webcam");
        await Wait.Until(() => tile.AvailableFilters.Count == 2, "filtros");
        await tile.SetFilterAsync("Correção de cor");
        Assert.Equal("Desligado", tile.CurrentVisual().MiniText);

        await tile.OnTouchAsync(Touch(TouchKind.Tap), CancellationToken.None);
        await Wait.Until(() => tile.CurrentVisual().MiniText == "Ligado", "ligar");
        Assert.True(_obs.SourceFilters["Webcam"][0].Enabled);
        await tile.DisposeAsync();
    }

    [Fact]
    public async Task Media_TapPlaysAndPauses_HoldRestarts()
    {
        var tile = await Create<MediaInstance>(new MediaWidget(), WidgetSize.Wide);
        Assert.Equal(["Vinheta"], tile.AvailableInputs.Select(i => i.Name));
        await tile.SetInputAsync("Vinheta");
        await Wait.Until(() => tile.CurrentVisual().Caption == "Parado", "parado");

        // Parada, o toque reinicia (play não volta ao começo no OBS).
        await tile.OnTouchAsync(Touch(TouchKind.Tap), CancellationToken.None);
        await Wait.Until(() => tile.CurrentVisual().Caption == "Tocando", "tocando");
        Assert.Contains("TriggerMediaInputAction", _obs.Requests);

        await tile.OnTouchAsync(Touch(TouchKind.Tap), CancellationToken.None);
        await Wait.Until(() => tile.CurrentVisual().Caption == "Pausado", "pausado");
        Assert.Equal("OBS_MEDIA_STATE_PAUSED", _obs.MediaInputs["Vinheta"].State);

        await tile.OnTouchAsync(Touch(TouchKind.LongPress), CancellationToken.None);
        await Wait.Until(() => _obs.MediaInputs["Vinheta"].State == "OBS_MEDIA_STATE_PLAYING", "reiniciar");
        await tile.DisposeAsync();
    }

    [Fact]
    public async Task StudioModeAndTransition()
    {
        var studio = await Create<StudioModeInstance>(new StudioModeWidget(), WidgetSize.Small);
        var transition = await Create<TransitionInstance>(new TransitionWidget(), WidgetSize.Small);
        await Wait.Until(() => _session.State.Transition == "Fade", "transição");

        // Sem modo estúdio a transição só avisa.
        await transition.OnTouchAsync(Touch(TouchKind.Tap), CancellationToken.None);
        Assert.Equal("Ligue o modo estúdio", transition.CurrentVisual().Detail);
        Assert.DoesNotContain("TriggerStudioModeTransition", _obs.Requests);

        await studio.OnTouchAsync(Touch(TouchKind.Tap), CancellationToken.None);
        await Wait.Until(() => _session.State.StudioMode && studio.CurrentVisual().Big == "Ligado", "modo estúdio");
        await Wait.Until(() => _session.State.PreviewScene == "Câmera", "prévia");

        await transition.OnTouchAsync(Touch(TouchKind.Tap), CancellationToken.None);
        await Wait.Until(() => _session.State.ProgramScene == "Câmera", "transição feita");
        Assert.Equal("Câmera", _obs.Program);
        await studio.DisposeAsync();
        await transition.DisposeAsync();
    }

    [Fact]
    public async Task Status_WatchesStatsWhileAwake()
    {
        var tile = await Create<StatusInstance>(new StatusWidget(), WidgetSize.Wide);
        Assert.True(_session.StatsWatched);
        await Wait.Until(() => tile.CurrentVisual().Lines?[0].Value == "4,5 %", "estatísticas");
        _obs.Cpu = 12;
        await Wait.Until(() => tile.CurrentVisual().Lines?[0].Value == "12,0 %", "segundo a segundo");

        await tile.OnSleepAsync(CancellationToken.None);
        Assert.False(_session.StatsWatched);
        await tile.DisposeAsync();
    }

    [Fact]
    public async Task ScenePanel_TapCellChangesScene()
    {
        var context = new FakeContext(WidgetSize.Large);
        var panel = await Create<ScenePanelInstance>(new ScenePanelWidget(), WidgetSize.Large, context);
        Assert.True(new ScenePanelWidget().Manifest.SupportsFullscreen);
        await Wait.Until(() => panel.CurrentVisual().Cells?.Count == 3, "cenas");
        Assert.Equal(TilePalette.Ok, panel.CurrentVisual().Cells![0].Fill);

        var (columns, rows) = GridLayout.Shape(context.PixelSize);
        var third = GridLayout.Cell(context.PixelSize, columns, rows, 2);
        await panel.OnTouchAsync(Touch(TouchKind.Tap, (int)(third.X + third.Width / 2), (int)(third.Y + third.Height / 2)), CancellationToken.None);
        await Wait.Until(() => _session.State.ProgramScene == "Intervalo", "trocar para Intervalo");
        await Wait.Until(() => panel.CurrentVisual().Cells![2].Fill == TilePalette.Ok);
        await panel.DisposeAsync();
    }

    [Fact]
    public async Task Status_ObsClosed_HoldOpensObs()
    {
        var started = new List<string>();
        ObsLauncher.FindOverride = () => @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
        ObsLauncher.StartOverride = started.Add;
        var closed = new ObsSession(TimeProvider.System, () => new ObsEndpoint("127.0.0.1", FakeObs.FreePort(), null), [TimeSpan.FromMilliseconds(100)]);
        ObsTileInstance.SessionOverride = closed;
        var tile = (StatusInstance)await new StatusWidget().CreateInstanceAsync(new FakeContext(WidgetSize.Small), CancellationToken.None);
        try
        {
            await Wait.Until(() => tile.CurrentVisual().Detail == "Segure para abrir o OBS", "convite para abrir");
            await tile.OnTouchAsync(new TouchEvent(TouchKind.PressProgress, 50, 50, TimeSpan.FromMilliseconds(400), 0.5), CancellationToken.None);
            Assert.Equal(0.5, tile.CurrentVisual().Press);
            await tile.OnTouchAsync(Touch(TouchKind.LongPress), CancellationToken.None);
            Assert.Single(started);
            Assert.Equal("Abrindo o OBS…", tile.CurrentVisual().Detail);

            // Sem o OBS instalado, só diz que está fechado.
            ObsLauncher.FindOverride = () => null;
            await Wait.Until(() => tile.CurrentVisual().Detail == "OBS fechado", "sem convite");
        }
        finally
        {
            await tile.DisposeAsync();
            await closed.DisposeAsync();
        }
    }
}
