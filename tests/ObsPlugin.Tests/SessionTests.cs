using System.Text.Json.Nodes;
using OpenWigiDash.ObsPlugin.Obs;

namespace ObsPlugin.Tests;

/// <summary>A sessão contra um OBS de mentira (servidor obs-websocket v5 local).</summary>
public class SessionTests
{
    private static readonly TimeSpan[] Fast = [TimeSpan.FromMilliseconds(100)];

    private static ObsSession Session(Func<ObsEndpoint> endpoint) => new(TimeProvider.System, endpoint, Fast, Environment.GetEnvironmentVariable("OBS_TEST_LOG") is { } p ? new DiagnosticLog(p) : null);

    [Fact]
    public async Task NoPassword_ConnectsAndSyncsEverything()
    {
        await using var obs = FakeObs.Start();
        obs.Recording = true;
        obs.VirtualCam = true;
        await using var session = Session(() => obs.Endpoint);
        using var lease = session.Acquire();

        await Wait.Until(() => session.ConnectionState == ObsConnectionState.Ready && session.State.AudioInputs.Count == 2, "sincronizar");
        Assert.Equal(["Jogo", "Câmera", "Intervalo"], session.State.Scenes);
        Assert.Equal("Jogo", session.State.ProgramScene);
        Assert.True(session.State.Record.Active);
        Assert.True(session.State.VirtualCam);
        Assert.Equal("5.5.4", session.State.ObsVersion);
        Assert.Equal($"127.0.0.1:{obs.Port}", session.Address);

        // Só fontes com áudio: a webcam responde erro ao GetInputMute e fica de fora.
        Assert.Null(session.State.FindInput("Webcam"));
        Assert.True(session.State.FindInput("Áudio do PC")!.Muted);
        Assert.Equal(-6, session.State.FindInput("Microfone")!.VolumeDb);
        Assert.Equal("wasapi_input_capture", session.State.FindInput("Microfone")!.Kind);

        // Sem senha, o Identify vai sem authentication e sem medidor.
        var identify = obs.Received.First(m => (int?)m["op"] == 1);
        Assert.Null(identify["d"]!["authentication"]);
        Assert.Equal((int)ObsProtocol.Events.All, (int)identify["d"]!["eventSubscriptions"]!);
    }

    [Fact]
    public async Task RightPassword_Identifies()
    {
        await using var obs = FakeObs.Start(password: "s3nh4-de-teste");
        await using var session = Session(() => obs.Endpoint);
        using var lease = session.Acquire();
        await Wait.Until(() => session.ConnectionState == ObsConnectionState.Ready);
        Assert.Null(session.LastError);
    }

    [Fact]
    public async Task WrongPassword_IsAuthFailed_AndNoPasswordIsNeedsPassword()
    {
        await using var obs = FakeObs.Start(password: "certa");
        var endpoint = obs.Endpoint with { Password = "errada" };
        await using var session = Session(() => endpoint);
        using var lease = session.Acquire();
        await Wait.Until(() => session.ConnectionState == ObsConnectionState.AuthFailed, "senha errada");

        // A pessoa apaga a senha e salva: tenta na hora (sem esperar o minuto) e pede a senha.
        endpoint = endpoint with { Password = null };
        session.Reconnect();
        await Wait.Until(() => session.ConnectionState == ObsConnectionState.NeedsPassword, "falta a senha");

        // Agora a certa.
        endpoint = endpoint with { Password = "certa" };
        session.Reconnect();
        await Wait.Until(() => session.ConnectionState == ObsConnectionState.Ready, "senha certa");
    }

    [Fact]
    public async Task NoObs_IsUnavailable_AndRequestsFailWithoutThrowing()
    {
        var port = FakeObs.FreePort();
        await using var session = Session(() => new ObsEndpoint("127.0.0.1", port, null));
        using var lease = session.Acquire();
        await Wait.Until(() => session.ConnectionState == ObsConnectionState.Unavailable);
        Assert.NotNull(session.LastError);

        var response = await session.ToggleRecordAsync();
        Assert.False(response.Ok);
    }

    [Fact]
    public async Task ObsClosing_IsUnavailable_ThenReconnectsAlone()
    {
        await using var obs = FakeObs.Start();
        await using var session = Session(() => obs.Endpoint);
        using var lease = session.Acquire();
        await Wait.Until(() => session.ConnectionState == ObsConnectionState.Ready && session.State.Scenes.Count == 3);
        var connections = obs.Connections;

        await obs.DropAsync();
        await Wait.Until(() => session.ConnectionState != ObsConnectionState.Ready, "cair");
        await Wait.Until(() => obs.Connections > connections && session.ConnectionState == ObsConnectionState.Ready && session.State.Scenes.Count == 3, "voltar");
    }

    [Fact]
    public async Task Events_UpdateTheState()
    {
        await using var obs = FakeObs.Start();
        await using var session = Session(() => obs.Endpoint);
        using var lease = session.Acquire();
        await Wait.Until(() => session.ConnectionState == ObsConnectionState.Ready && session.State.AudioInputs.Count == 2);

        await obs.EmitAsync("CurrentProgramSceneChanged", new JsonObject { ["sceneName"] = "Intervalo" });
        await obs.EmitAsync("InputMuteStateChanged", new JsonObject { ["inputName"] = "Microfone", ["inputMuted"] = true });
        await Wait.Until(() => session.State.ProgramScene == "Intervalo" && session.State.FindInput("Microfone")!.Muted);

        // Cena nova: o plugin pede a lista de novo.
        obs.Scenes.Insert(0, "Nova");
        await obs.EmitAsync("SceneCreated", new JsonObject { ["sceneName"] = "Nova", ["isGroup"] = false });
        await Wait.Until(() => session.State.Scenes.Contains("Nova"), "cena nova");
    }

    [Fact]
    public async Task Requests_ChangeObs_AndRefusalsComeBackAsFailure()
    {
        await using var obs = FakeObs.Start();
        await using var session = Session(() => obs.Endpoint);
        using var lease = session.Acquire();
        await Wait.Until(() => session.ConnectionState == ObsConnectionState.Ready && session.State.Scenes.Count == 3);

        Assert.True((await session.SetProgramSceneAsync("Câmera")).Ok);
        await Wait.Until(() => session.State.ProgramScene == "Câmera");

        Assert.True((await session.SetInputVolumeDbAsync("Microfone", -200)).Ok);
        Assert.Equal(-100, obs.Audio["Microfone"].Db); // o OBS só aceita de -100 a 26

        obs.Refuse.Add("CreateRecordChapter");
        var chapter = await session.CreateRecordChapterAsync();
        Assert.False(chapter.Ok);
        Assert.Equal(702, chapter.Code);
    }

    [Fact]
    public async Task Meters_TurnOnWithReidentify_AndOffAgain()
    {
        await using var obs = FakeObs.Start();
        await using var session = Session(() => obs.Endpoint);
        using var lease = session.Acquire();
        await Wait.Until(() => session.ConnectionState == ObsConnectionState.Ready);

        var watch = session.WatchMeters();
        await Wait.Until(() => obs.Received.Any(m => (int?)m["op"] == 3 && ((int)m["d"]!["eventSubscriptions"]! & (int)ObsProtocol.Events.InputVolumeMeters) != 0), "ligar medidor");
        Assert.True(session.MetersSubscribed);

        watch.Dispose();
        await Wait.Until(() => obs.Received.Count(m => (int?)m["op"] == 3) == 2 && !session.MetersSubscribed, "desligar medidor");
    }

    [Fact]
    public async Task Thumbnail_OnlyForWatchedScenes()
    {
        await using var obs = FakeObs.Start();
        await using var session = Session(() => obs.Endpoint);
        using var lease = session.Acquire();
        await Wait.Until(() => session.ConnectionState == ObsConnectionState.Ready && session.State.Scenes.Count == 3);

        using (session.WatchThumbnail("Câmera"))
        {
            await Wait.Until(() => session.State.Thumbnail("Câmera") is not null, "miniatura");
            Assert.Equal([0xFF, 0xD8, 0xFF, 0xD9], session.State.Thumbnail("Câmera")!.Jpeg);
        }
        Assert.Empty(session.WatchedThumbnails);
        var shots = obs.Requests.Count(r => r == "GetSourceScreenshot");
        await Task.Delay(1500);
        Assert.InRange(obs.Requests.Count(r => r == "GetSourceScreenshot"), shots, shots + 1); // no máximo a rodada que já tinha começado
        Assert.Null(session.State.Thumbnail("Jogo"));
    }

    [Fact]
    public async Task LastLease_ClosesTheConnection()
    {
        await using var obs = FakeObs.Start();
        await using var session = Session(() => obs.Endpoint);
        var first = session.Acquire();
        var second = session.Acquire();
        await Wait.Until(() => session.ConnectionState == ObsConnectionState.Ready);

        first.Dispose();
        await Task.Delay(200);
        Assert.Equal(ObsConnectionState.Ready, session.ConnectionState);

        second.Dispose();
        await Wait.Until(() => session.ConnectionState == ObsConnectionState.Connecting, "fechar");
    }
}
