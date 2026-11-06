using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenWigiDash.ObsPlugin.Obs;

namespace ObsPlugin.Tests;

/// <summary>
/// Um OBS de mentira: servidor obs-websocket v5 local (Hello, Identify com ou sem senha, pedidos e eventos), com
/// cenas, fontes de áudio e saídas que os pedidos mudam como o OBS faria.
/// </summary>
internal sealed class FakeObs : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private WebSocket? _socket;
    private int _connections;

    public int Port { get; private init; }
    public ObsEndpoint Endpoint => new("127.0.0.1", Port, Password);

    /// <summary>Senha que o servidor pede; <c>null</c> = sem autenticação.</summary>
    public string? Password { get; set; }

    public ConcurrentQueue<JsonNode> Received { get; } = new();
    public int Connections => Volatile.Read(ref _connections);

    // Estado do OBS simulado (índice 0 embaixo, como no OBS).
    public List<string> Scenes { get; } = ["Jogo", "Câmera", "Intervalo"];
    public string Program { get; set; } = "Jogo";
    public bool StudioMode { get; set; }
    public string Preview { get; set; } = "Câmera";
    public bool Recording { get; set; }
    public bool Streaming { get; set; }
    public bool Replay { get; set; }
    public bool VirtualCam { get; set; }
    public Dictionary<string, (bool Muted, double Db)> Audio { get; } = new() { ["Microfone"] = (false, -6), ["Áudio do PC"] = (true, 0) };

    /// <summary>Itens por cena, de cima para baixo (como o OBS mostra).</summary>
    public Dictionary<string, List<(int Id, string Name, string Kind, bool Enabled)>> Items { get; } = new()
    {
        ["Jogo"] = [(2, "Webcam", "dshow_input", true), (1, "Captura do jogo", "game_capture", true)],
        ["Câmera"] = [(1, "Webcam", "dshow_input", true)],
    };

    public Dictionary<string, List<(string Name, string Kind, bool Enabled)>> SourceFilters { get; } = new()
    {
        ["Webcam"] = [("Correção de cor", "color_filter_v2", false), ("Chroma key", "chroma_key_filter_v2", true)],
    };

    public Dictionary<string, (string State, double Duration, double Cursor)> MediaInputs { get; } = new()
    {
        ["Vinheta"] = ("OBS_MEDIA_STATE_STOPPED", 30_000, 0),
    };

    public double Cpu { get; set; } = 4.5;
    public string TransitionName { get; set; } = "Fade";
    public List<string> Collections { get; } = ["Principal", "Podcast"];
    public string Collection { get; set; } = "Principal";
    public List<string> ProfileNames { get; } = ["Twitch", "YouTube"];
    public string Profile { get; set; } = "Twitch";
    /// <summary>Como o OBS real na troca de perfil: responde na hora e só troca (com os eventos) depois disso.</summary>
    public TimeSpan ProfileSwitchDelay { get; set; }

    /// <summary>Pedidos que o OBS recusa (ex.: CreateRecordChapter fora do Hybrid MP4).</summary>
    public HashSet<string> Refuse { get; } = [];

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static FakeObs Start(string? password = null)
    {
        var port = FreePort();
        var server = new FakeObs { Port = port, Password = password };
        server._listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        server._listener.Start();
        _ = server.AcceptLoopAsync();
        return server;
    }

    public IEnumerable<string> Requests => Received
        .Where(m => (int?)m["op"] == 6)
        .Select(m => (string)m["d"]!["requestType"]!);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }
            var ws = (await context.AcceptWebSocketAsync("obsws.json")).WebSocket;
            _socket = ws;
            Interlocked.Increment(ref _connections);
            _ = ServeAsync(ws);
        }
    }

    private async Task ServeAsync(WebSocket ws)
    {
        var buffer = new byte[256 * 1024];
        const string salt = "c2FsdC1kZS10ZXN0ZQ==";
        const string challenge = "ZGVzYWZpby1kZS10ZXN0ZQ==";
        try
        {
            var hello = new JsonObject { ["obsWebSocketVersion"] = "5.5.4", ["rpcVersion"] = 1 };
            if (Password is not null)
                hello["authentication"] = new JsonObject { ["salt"] = salt, ["challenge"] = challenge };
            await SendAsync(ws, new JsonObject { ["op"] = 0, ["d"] = hello });

            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                var message = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!;
                Received.Enqueue(message);
                switch ((int?)message["op"])
                {
                    case 1:
                        if (Password is not null && (string?)message["d"]!["authentication"] != ExpectedAuth(Password, salt, challenge))
                        {
                            await ws.CloseOutputAsync((WebSocketCloseStatus)4009, "Authentication failed.", CancellationToken.None);
                            return;
                        }
                        await SendAsync(ws, new JsonObject { ["op"] = 2, ["d"] = new JsonObject { ["negotiatedRpcVersion"] = 1 } });
                        break;
                    case 3:
                        await SendAsync(ws, new JsonObject { ["op"] = 2, ["d"] = new JsonObject { ["negotiatedRpcVersion"] = 1 } });
                        break;
                    case 6:
                        await AnswerAsync(ws, message["d"]!);
                        break;
                }
            }
        }
        catch (Exception) when (ws.State != WebSocketState.Open || _stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>A conta do obs-websocket feita de novo aqui, separada da do plugin.</summary>
    private static string ExpectedAuth(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private async Task AnswerAsync(WebSocket ws, JsonNode d)
    {
        var type = (string)d["requestType"]!;
        var id = (string)d["requestId"]!;
        var data = d["requestData"];
        JsonObject? response = null;
        var ok = !Refuse.Contains(type);
        var code = ok ? 100 : 702;
        var events = new List<(string Type, JsonObject Data)>();

        lock (_gate)
        {
            if (ok)
            {
                switch (type)
                {
                    case "GetSceneList":
                        response = new JsonObject
                        {
                            ["currentProgramSceneName"] = Program,
                            ["currentPreviewSceneName"] = StudioMode ? Preview : null,
                            ["scenes"] = new JsonArray([.. Scenes.Select((s, i) => (JsonNode)new JsonObject { ["sceneName"] = s, ["sceneIndex"] = Scenes.Count - 1 - i })]),
                        };
                        break;
                    case "GetStudioModeEnabled":
                        response = new JsonObject { ["studioModeEnabled"] = StudioMode };
                        break;
                    case "GetCurrentPreviewScene":
                        response = new JsonObject { ["sceneName"] = Preview, ["currentPreviewSceneName"] = Preview };
                        break;
                    case "GetRecordStatus":
                        response = new JsonObject
                        {
                            ["outputActive"] = Recording, ["outputPaused"] = false, ["outputTimecode"] = Recording ? "00:01:05.000" : "00:00:00.000",
                            ["outputBytes"] = Recording ? 2_000_000_000 : 0,
                        };
                        break;
                    case "GetStreamStatus":
                        response = new JsonObject
                        {
                            ["outputActive"] = Streaming, ["outputReconnecting"] = false, ["outputTimecode"] = Streaming ? "00:10:00.000" : "00:00:00.000",
                            ["outputBytes"] = 0, ["outputSkippedFrames"] = 0, ["outputTotalFrames"] = 100,
                        };
                        break;
                    case "GetReplayBufferStatus":
                        response = new JsonObject { ["outputActive"] = Replay };
                        break;
                    case "GetVirtualCamStatus":
                        response = new JsonObject { ["outputActive"] = VirtualCam };
                        break;
                    case "GetInputList":
                        response = new JsonObject
                        {
                            ["inputs"] = new JsonArray(
                                new JsonObject { ["inputName"] = "Microfone", ["unversionedInputKind"] = "wasapi_input_capture" },
                                new JsonObject { ["inputName"] = "Áudio do PC", ["unversionedInputKind"] = "wasapi_output_capture" },
                                new JsonObject { ["inputName"] = "Webcam", ["unversionedInputKind"] = "dshow_input" },
                                new JsonObject { ["inputName"] = "Vinheta", ["unversionedInputKind"] = "ffmpeg_source" }),
                        };
                        break;
                    case "GetInputMute" when Audio.TryGetValue((string)data!["inputName"]!, out var a):
                        response = new JsonObject { ["inputMuted"] = a.Muted };
                        break;
                    case "GetInputVolume" when Audio.TryGetValue((string)data!["inputName"]!, out var a):
                        response = new JsonObject { ["inputVolumeDb"] = a.Db, ["inputVolumeMul"] = Math.Pow(10, a.Db / 20) };
                        break;
                    case "GetInputMute" or "GetInputVolume":
                        ok = false;
                        code = 604; // InvalidInputKind: fonte sem áudio
                        break;
                    case "SetInputMute":
                    {
                        var name = (string)data!["inputName"]!;
                        var muted = (bool)data["inputMuted"]!;
                        Audio[name] = (muted, Audio[name].Db);
                        events.Add(("InputMuteStateChanged", new JsonObject { ["inputName"] = name, ["inputMuted"] = muted }));
                        break;
                    }
                    case "SetInputVolume":
                    {
                        var name = (string)data!["inputName"]!;
                        var db = (double)data["inputVolumeDb"]!;
                        Audio[name] = (Audio[name].Muted, db);
                        events.Add(("InputVolumeChanged", new JsonObject { ["inputName"] = name, ["inputVolumeDb"] = db }));
                        break;
                    }
                    case "SetCurrentProgramScene":
                        Program = (string)data!["sceneName"]!;
                        events.Add(("CurrentProgramSceneChanged", new JsonObject { ["sceneName"] = Program }));
                        break;
                    case "SetCurrentPreviewScene":
                        Preview = (string)data!["sceneName"]!;
                        events.Add(("CurrentPreviewSceneChanged", new JsonObject { ["sceneName"] = Preview }));
                        break;
                    case "ToggleRecord":
                        Recording = !Recording;
                        events.Add(("RecordStateChanged", Output(Recording)));
                        break;
                    case "ToggleVirtualCam":
                        VirtualCam = !VirtualCam;
                        events.Add(("VirtualcamStateChanged", new JsonObject { ["outputActive"] = VirtualCam }));
                        break;
                    case "StartStream":
                        Streaming = true;
                        events.Add(("StreamStateChanged", Output(true)));
                        break;
                    case "StopStream":
                        Streaming = false;
                        events.Add(("StreamStateChanged", Output(false)));
                        break;
                    case "GetSceneItemList" when Items.TryGetValue((string)data!["sceneName"]!, out var items):
                        response = new JsonObject
                        {
                            ["sceneItems"] = new JsonArray([.. items.Select((it, i) => (JsonNode)new JsonObject
                            {
                                ["sceneItemId"] = it.Id, ["sourceName"] = it.Name, ["inputKind"] = it.Kind,
                                ["sceneItemEnabled"] = it.Enabled, ["isGroup"] = false, ["sceneItemIndex"] = items.Count - 1 - i,
                            })]),
                        };
                        break;
                    case "SetSceneItemEnabled":
                    {
                        var scene = (string)data!["sceneName"]!;
                        var itemId = (int)data["sceneItemId"]!;
                        var enabled = (bool)data["sceneItemEnabled"]!;
                        var list = Items[scene];
                        var at = list.FindIndex(i => i.Id == itemId);
                        list[at] = list[at] with { Enabled = enabled };
                        events.Add(("SceneItemEnableStateChanged", new JsonObject { ["sceneName"] = scene, ["sceneItemId"] = itemId, ["sceneItemEnabled"] = enabled }));
                        break;
                    }
                    case "GetSourceFilterList":
                        response = new JsonObject
                        {
                            ["filters"] = new JsonArray([.. SourceFilters.GetValueOrDefault((string)data!["sourceName"]!, []).Select((f, i) => (JsonNode)new JsonObject
                            {
                                ["filterName"] = f.Name, ["filterKind"] = f.Kind, ["filterEnabled"] = f.Enabled, ["filterIndex"] = i,
                            })]),
                        };
                        break;
                    case "SetSourceFilterEnabled":
                    {
                        var source = (string)data!["sourceName"]!;
                        var name = (string)data["filterName"]!;
                        var enabled = (bool)data["filterEnabled"]!;
                        var list = SourceFilters[source];
                        var at = list.FindIndex(f => f.Name == name);
                        list[at] = list[at] with { Enabled = enabled };
                        events.Add(("SourceFilterEnableStateChanged", new JsonObject { ["sourceName"] = source, ["filterName"] = name, ["filterEnabled"] = enabled }));
                        break;
                    }
                    case "GetMediaInputStatus" when MediaInputs.TryGetValue((string)data!["inputName"]!, out var media):
                        response = new JsonObject { ["mediaState"] = media.State, ["mediaDuration"] = media.Duration, ["mediaCursor"] = media.Cursor };
                        break;
                    case "TriggerMediaInputAction":
                    {
                        var name = (string)data!["inputName"]!;
                        var action = (string)data["mediaAction"]!;
                        var media = MediaInputs[name];
                        MediaInputs[name] = action switch
                        {
                            "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PLAY" => media with { State = "OBS_MEDIA_STATE_PLAYING" },
                            "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PAUSE" => media with { State = "OBS_MEDIA_STATE_PAUSED" },
                            "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_STOP" => media with { State = "OBS_MEDIA_STATE_STOPPED", Cursor = 0 },
                            _ => media with { State = "OBS_MEDIA_STATE_PLAYING", Cursor = 0 },
                        };
                        events.Add(("MediaInputActionTriggered", new JsonObject { ["inputName"] = name, ["mediaAction"] = action }));
                        break;
                    }
                    case "GetStats":
                        response = new JsonObject
                        {
                            ["cpuUsage"] = Cpu, ["memoryUsage"] = 512.0, ["activeFps"] = 60.0, ["averageFrameRenderTime"] = 3.2,
                            ["renderSkippedFrames"] = 30, ["renderTotalFrames"] = 1000, ["outputSkippedFrames"] = 0, ["outputTotalFrames"] = 1000,
                        };
                        break;
                    case "GetCurrentSceneTransition":
                        response = new JsonObject { ["transitionName"] = TransitionName, ["transitionDuration"] = 300 };
                        break;
                    case "SetStudioModeEnabled":
                        StudioMode = (bool)data!["studioModeEnabled"]!;
                        events.Add(("StudioModeStateChanged", new JsonObject { ["studioModeEnabled"] = StudioMode }));
                        break;
                    case "TriggerStudioModeTransition" when !StudioMode:
                        ok = false;
                        code = 506; // StudioModeNotActive
                        break;
                    case "TriggerStudioModeTransition":
                        (Program, Preview) = (Preview, Program);
                        events.Add(("SceneTransitionStarted", new JsonObject { ["transitionName"] = TransitionName }));
                        events.Add(("CurrentProgramSceneChanged", new JsonObject { ["sceneName"] = Program }));
                        events.Add(("CurrentPreviewSceneChanged", new JsonObject { ["sceneName"] = Preview }));
                        events.Add(("SceneTransitionEnded", new JsonObject { ["transitionName"] = TransitionName }));
                        break;
                    case "GetSceneCollectionList":
                        response = new JsonObject
                        {
                            ["currentSceneCollectionName"] = Collection,
                            ["sceneCollections"] = new JsonArray([.. Collections.Select(c => (JsonNode)c)]),
                        };
                        break;
                    case "GetProfileList":
                        response = new JsonObject
                        {
                            ["currentProfileName"] = Profile,
                            ["profiles"] = new JsonArray([.. ProfileNames.Select(p => (JsonNode)p)]),
                        };
                        break;
                    case "SetCurrentSceneCollection":
                        Collection = (string)data!["sceneCollectionName"]!;
                        events.Add(("CurrentSceneCollectionChanging", new JsonObject { ["sceneCollectionName"] = Collection }));
                        events.Add(("CurrentSceneCollectionChanged", new JsonObject { ["sceneCollectionName"] = Collection }));
                        break;
                    case "SetCurrentProfile" when ProfileSwitchDelay > TimeSpan.Zero:
                    {
                        var target = (string)data!["profileName"]!;
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(ProfileSwitchDelay);
                            lock (_gate)
                                Profile = target;
                            await EmitAsync("CurrentProfileChanging", new JsonObject { ["profileName"] = target });
                            await EmitAsync("CurrentProfileChanged", new JsonObject { ["profileName"] = target });
                        });
                        break;
                    }
                    case "SetCurrentProfile":
                        Profile = (string)data!["profileName"]!;
                        events.Add(("CurrentProfileChanging", new JsonObject { ["profileName"] = Profile }));
                        events.Add(("CurrentProfileChanged", new JsonObject { ["profileName"] = Profile }));
                        break;
                    case "GetSourceScreenshot":
                        response = new JsonObject { ["imageData"] = "data:image/jpg;base64," + Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xD9]) };
                        break;
                }
            }
        }

        var reply = new JsonObject
        {
            ["requestType"] = type,
            ["requestId"] = id,
            ["requestStatus"] = new JsonObject { ["result"] = ok, ["code"] = code, ["comment"] = ok ? null : "Recusado pelo teste." },
        };
        if (response is not null)
            reply["responseData"] = response;
        await SendAsync(ws, new JsonObject { ["op"] = 7, ["d"] = reply });
        foreach (var (eventType, eventData) in events)
            await EmitAsync(eventType, eventData);
    }

    private static JsonObject Output(bool active) => new()
    {
        ["outputActive"] = active,
        ["outputState"] = active ? "OBS_WEBSOCKET_OUTPUT_STARTED" : "OBS_WEBSOCKET_OUTPUT_STOPPED",
    };

    /// <summary>Manda um evento (op 5), como o OBS quando algo muda nele.</summary>
    public Task EmitAsync(string type, JsonObject data, int intent = 0) =>
        _socket is { } ws
            ? SendAsync(ws, new JsonObject { ["op"] = 5, ["d"] = new JsonObject { ["eventType"] = type, ["eventIntent"] = intent, ["eventData"] = data } })
            : Task.CompletedTask;

    private readonly SemaphoreSlim _send = new(1, 1);

    private async Task SendAsync(WebSocket ws, JsonObject message)
    {
        if (ws.State != WebSocketState.Open)
            return;
        await _send.WaitAsync();
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>Derruba a conexão atual (como o OBS fechando).</summary>
    public async Task DropAsync()
    {
        if (_socket is { } ws)
        {
            try
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "bye", CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
            }
            ws.Abort();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await DropAsync();
        _listener.Close();
    }
}

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("OBS_TEST_WAIT"), out var s) ? s : 10);

    public static async Task Until(Func<bool> condition, string? what = null)
    {
        var end = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > end)
                throw new TimeoutException("Não aconteceu a tempo" + (what is null ? "." : $": {what}"));
            await Task.Delay(20);
        }
    }

    public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
