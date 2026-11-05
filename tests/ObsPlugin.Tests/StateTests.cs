using OpenWigiDash.ObsPlugin.Obs;

namespace ObsPlugin.Tests;

/// <summary>Relógio parado que o teste anda à mão.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public class StateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SceneList_IsReversedToMatchObs()
    {
        var state = new ObsState(new ManualTime(T0));
        state.ApplySceneList(Wait.Json("""
            {"currentProgramSceneName":"Jogo","currentPreviewSceneName":null,
             "scenes":[{"sceneName":"Intervalo","sceneIndex":0},{"sceneName":"Câmera","sceneIndex":1},{"sceneName":"Jogo","sceneIndex":2}]}
            """));
        Assert.Equal(["Jogo", "Câmera", "Intervalo"], state.Scenes);
        Assert.Equal("Jogo", state.ProgramScene);
        Assert.Null(state.PreviewScene);
    }

    [Fact]
    public void ProgramAndPreviewEvents()
    {
        var state = new ObsState(new ManualTime(T0));
        Assert.True(state.ApplyEvent("CurrentProgramSceneChanged", Wait.Json("""{"sceneName":"Câmera"}""")).Changed);
        Assert.False(state.ApplyEvent("CurrentProgramSceneChanged", Wait.Json("""{"sceneName":"Câmera"}""")).Changed);
        state.ApplyEvent("StudioModeStateChanged", Wait.Json("""{"studioModeEnabled":true}"""));
        state.ApplyEvent("CurrentPreviewSceneChanged", Wait.Json("""{"sceneName":"Jogo"}"""));
        Assert.True(state.StudioMode);
        Assert.Equal("Jogo", state.PreviewScene);

        state.ApplyEvent("StudioModeStateChanged", Wait.Json("""{"studioModeEnabled":false}"""));
        Assert.Null(state.PreviewScene);
    }

    [Fact]
    public void SceneRenamed_AsksForTheListAgain()
    {
        var state = new ObsState(new ManualTime(T0));
        Assert.True(state.ApplyEvent("SceneNameChanged", Wait.Json("""{"oldSceneName":"A","sceneName":"B"}""")).NeedsScenes);
        Assert.True(state.ApplyEvent("CurrentSceneCollectionChanged", Wait.Json("{}")).NeedsInputs);
        Assert.True(state.ApplyEvent("InputCreated", Wait.Json("""{"inputName":"X"}""")).NeedsInputs);
    }

    [Fact]
    public void Record_TimeKeepsRunningBetweenReads_AndFreezesWhenPaused()
    {
        var time = new ManualTime(T0);
        var state = new ObsState(time);
        state.ApplyRecordStatus(Wait.Json("""{"outputActive":true,"outputPaused":false,"outputTimecode":"00:01:00.000","outputBytes":1000}"""));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(65), state.Record.Elapsed(time.Now));

        state.ApplyEvent("RecordStateChanged", Wait.Json("""{"outputActive":true,"outputState":"OBS_WEBSOCKET_OUTPUT_PAUSED"}"""));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(state.Record.Paused);
        Assert.Equal(TimeSpan.FromSeconds(65), state.Record.Elapsed(time.Now));

        state.ApplyEvent("RecordStateChanged", Wait.Json("""{"outputActive":false,"outputState":"OBS_WEBSOCKET_OUTPUT_STOPPED"}"""));
        Assert.False(state.Record.Active);
        Assert.Equal(TimeSpan.Zero, state.Record.Elapsed(time.Now));
    }

    [Fact]
    public void Stream_KbpsFromBytes_AndHistoryClearsWhenItEnds()
    {
        var time = new ManualTime(T0);
        var state = new ObsState(time);
        state.ApplyStreamStatus(Wait.Json("""{"outputActive":true,"outputBytes":0,"outputTotalFrames":100,"outputSkippedFrames":0}"""));
        Assert.Null(state.Stream.Kbps);

        time.Advance(TimeSpan.FromSeconds(1));
        state.ApplyStreamStatus(Wait.Json("""{"outputActive":true,"outputBytes":750000,"outputTotalFrames":200,"outputSkippedFrames":4}"""));
        Assert.Equal(6000, state.Stream.Kbps!.Value, 3);
        Assert.Equal(2, state.Stream.DroppedPercent, 3);
        Assert.Single(state.KbpsHistory);

        for (var i = 0; i < 50; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            state.ApplyStreamStatus(Wait.Json($$"""{"outputActive":true,"outputBytes":{{750000 * (i + 2)}},"outputTotalFrames":300}"""));
        }
        Assert.Equal(ObsState.KbpsHistoryLength, state.KbpsHistory.Count);

        state.ApplyStreamStatus(Wait.Json("""{"outputActive":false}"""));
        Assert.Empty(state.KbpsHistory);
        Assert.Null(state.Stream.Kbps);
    }

    [Fact]
    public void Stream_Reconnecting()
    {
        var state = new ObsState(new ManualTime(T0));
        state.ApplyEvent("StreamStateChanged", Wait.Json("""{"outputActive":true,"outputState":"OBS_WEBSOCKET_OUTPUT_STARTED"}"""));
        state.ApplyEvent("StreamStateChanged", Wait.Json("""{"outputActive":true,"outputState":"OBS_WEBSOCKET_OUTPUT_RECONNECTING"}"""));
        Assert.True(state.Stream.Reconnecting);
        state.ApplyEvent("StreamStateChanged", Wait.Json("""{"outputActive":true,"outputState":"OBS_WEBSOCKET_OUTPUT_RECONNECTED"}"""));
        Assert.False(state.Stream.Reconnecting);
        Assert.True(state.Stream.Active);
    }

    [Fact]
    public void ReplayAndVirtualCam()
    {
        var time = new ManualTime(T0);
        var state = new ObsState(time);
        state.ApplyEvent("ReplayBufferStateChanged", Wait.Json("""{"outputActive":true,"outputState":"OBS_WEBSOCKET_OUTPUT_STARTED"}"""));
        state.ApplyEvent("ReplayBufferSaved", Wait.Json("""{"savedReplayPath":"C:\\Videos\\Replay 2026-11-03.mkv"}"""));
        state.ApplyEvent("VirtualcamStateChanged", Wait.Json("""{"outputActive":true}"""));
        Assert.True(state.ReplayBuffer);
        Assert.Equal(T0, state.LastReplayAt);
        Assert.EndsWith(".mkv", state.LastReplayPath);
        Assert.True(state.VirtualCam);
    }

    [Fact]
    public void Inputs_MuteVolumeAndMeters()
    {
        var state = new ObsState(new ManualTime(T0));
        state.ApplyAudioInputs([new AudioInput("Microfone", "wasapi_input_capture") { VolumeDb = -6 }]);

        state.ApplyEvent("InputMuteStateChanged", Wait.Json("""{"inputName":"Microfone","inputMuted":true}"""));
        state.ApplyEvent("InputVolumeChanged", Wait.Json("""{"inputName":"Microfone","inputVolumeDb":-12.5}"""));
        Assert.True(state.FindInput("Microfone")!.Muted);
        Assert.Equal(-12.5, state.FindInput("Microfone")!.VolumeDb);
        Assert.False(state.ApplyEvent("InputMuteStateChanged", Wait.Json("""{"inputName":"Outro","inputMuted":true}""")).Changed);

        // Dois canais: o maior pico (índice 1) manda. 0,1 = -20 dB = 2/3 do medidor.
        var meters = Wait.Json("""{"inputs":[{"inputName":"Microfone","inputLevelsMul":[[0.05,0.1,0.1],[0.01,0.02,0.02]]}]}""");
        Assert.True(state.ApplyEvent("InputVolumeMeters", meters).Changed);
        Assert.Equal(2.0 / 3, state.FindInput("Microfone")!.Level, 3);
        Assert.False(state.ApplyEvent("InputVolumeMeters", meters).Changed); // mesmo nível: não redesenha

        // Lista nova (fonte criada) mantém o nível de quem já estava.
        state.ApplyAudioInputs([new AudioInput("Microfone", "wasapi_input_capture"), new AudioInput("Música", "ffmpeg_source")]);
        Assert.Equal(2.0 / 3, state.FindInput("Microfone")!.Level, 3);
        Assert.Equal(["Microfone", "Música"], state.AudioInputs.Select(i => i.Name).Order());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(0.001, 0)] // -60 dB
    [InlineData(2, 1)]
    public void MeterLevel(double mul, double level) => Assert.Equal(level, ObsState.MeterLevel(mul), 3);

    [Fact]
    public void Thumbnails_ArePrunedWhenTheSceneGoes()
    {
        var state = new ObsState(new ManualTime(T0));
        state.ApplySceneList(Wait.Json("""{"scenes":[{"sceneName":"A","sceneIndex":0},{"sceneName":"B","sceneIndex":1}]}"""));
        state.ApplyThumbnail("A", [1]);
        state.ApplyThumbnail("B", [2]);
        state.ApplyEvent("SceneListChanged", Wait.Json("""{"scenes":[{"sceneName":"B","sceneIndex":0}]}"""));
        Assert.Null(state.Thumbnail("A"));
        Assert.Equal([2], state.Thumbnail("B")!.Jpeg);
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        var state = new ObsState(new ManualTime(T0));
        state.ApplySceneList(Wait.Json("""{"currentProgramSceneName":"A","scenes":[{"sceneName":"A","sceneIndex":0}]}"""));
        state.ApplyAudioInputs([new AudioInput("Mic", "x")]);
        state.ApplyEvent("VirtualcamStateChanged", Wait.Json("""{"outputActive":true}"""));
        state.Reset();
        Assert.Empty(state.Scenes);
        Assert.Null(state.ProgramScene);
        Assert.Empty(state.AudioInputs);
        Assert.False(state.VirtualCam);
    }
}
