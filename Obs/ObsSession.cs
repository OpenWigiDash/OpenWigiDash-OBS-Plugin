using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenWigiDash.ObsPlugin.Obs;

/// <summary>Em que pé está a conexão com o OBS (o que o tile mostra antes de ficar pronto).</summary>
public enum ObsConnectionState
{
    Connecting,
    /// <summary>Nada respondeu no endereço: OBS fechado ou servidor WebSocket desligado.</summary>
    Unavailable,
    /// <summary>O OBS pede senha e não temos nenhuma.</summary>
    NeedsPassword,
    /// <summary>O OBS recusou a senha (fechamento 4009).</summary>
    AuthFailed,
    /// <summary>Respondeu, mas não é o obs-websocket v5 (OBS antes do 28).</summary>
    Unsupported,
    Ready,
}

/// <summary>
/// Conexão com o OBS pelo obs-websocket v5. Uma só por plugin, aberta enquanto houver widget na tela; todos os
/// tiles leem o mesmo <see cref="ObsState"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Reage aos eventos do OBS; só pergunta o status uma vez por segundo enquanto grava ou está ao vivo
/// (o tempo e os quadros perdidos não têm evento).</item>
/// <item>O medidor de áudio (InputVolumeMeters, ~20 por segundo) só é assinado enquanto há um mixer acordado.</item>
/// <item>Miniaturas: só das cenas que algum tile acordado pediu, no máximo uma por cena a cada segundo.</item>
/// <item>OBS fechou: tenta de novo cada vez mais espaçado (até 30 s). Senha errada: espera a pessoa mudar.</item>
/// </list>
/// </remarks>
public sealed partial class ObsSession : IAsyncDisposable
{
    private static readonly TimeSpan[] DefaultBackoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Coleção grande pode levar mais que um pedido comum para carregar.</summary>
    private static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MeterFrame = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan ThumbnailInterval = TimeSpan.FromSeconds(1);
    internal const int ThumbnailWidth = 400;

    private const ObsProtocol.Events BaseEvents = ObsProtocol.Events.All;

    public static ObsSession Shared { get; } = new(TimeProvider.System, () => ObsConnectionStore.Default.Load(), log: DiagnosticLog.Default);

    private readonly Func<ObsEndpoint> _endpoint;
    private readonly TimeSpan[] _backoff;
    private readonly TimeProvider _time;
    private readonly DiagnosticLog _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Lock _lifetime = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ObsResponse>> _pending = new();
    private readonly ConcurrentDictionary<string, int> _thumbnailScenes = new(StringComparer.Ordinal);
    private ILogger _logger = NullLogger.Instance;
    private int _users;
    private int _meterUsers;
    private long _requestIds;
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _attempt;
    private Task? _loop;
    private ClientWebSocket? _socket;
    private ObsProtocol.Events _subscribed;
    private DateTimeOffset _lastMeterRaise;
    private int _meterRaiseQueued;
    private int _wake;

    internal ObsSession(TimeProvider time, Func<ObsEndpoint> endpoint, TimeSpan[]? backoff = null, DiagnosticLog? log = null)
    {
        _time = time;
        _endpoint = endpoint;
        _backoff = backoff ?? DefaultBackoff;
        _log = log ?? new DiagnosticLog(null);
        State = new ObsState(time);
    }

    public ObsState State { get; }

    public ObsConnectionState ConnectionState { get; private set; } = ObsConnectionState.Connecting;

    /// <summary>Por que a última tentativa falhou, para a tela de conexão; <c>null</c> se conectou.</summary>
    public string? LastError { get; private set; }

    /// <summary>Endereço da tentativa atual (sem a senha).</summary>
    public string? Address { get; private set; }

    /// <summary>Endereço da tentativa atual (sem a senha fora daqui: só para saber se o OBS é deste PC).</summary>
    internal ObsEndpoint? Endpoint { get; private set; }

    /// <summary>O OBS configurado roda neste PC (dá para abrir ele daqui).</summary>
    public bool IsLocal => Endpoint?.IsLocal ?? true;

    /// <summary>Algo que os tiles mostram mudou. Chamado fora do laço de rede.</summary>
    public event Action? Changed;

    public void UseLogger(ILoggerFactory factory) => _logger = factory.CreateLogger("OpenWigiDash.ObsPlugin");

    // ---- ciclo de vida ----

    /// <summary>Um widget passou a usar a conexão. O primeiro abre. Devolve o que solta.</summary>
    public IDisposable Acquire()
    {
        lock (_lifetime)
        {
            if (_users++ == 0)
            {
                _stop = new CancellationTokenSource();
                var token = _stop.Token;
                _log.Write("Primeiro widget: abrindo a conexão com o OBS.");
                _loop = Task.Run(() => GuardedRunAsync(token));
            }
        }
        return new Lease(this);
    }

    private async Task ReleaseAsync()
    {
        Task? loop;
        lock (_lifetime)
        {
            if (_users == 0 || --_users > 0)
                return;
            _log.Write("Último widget saiu: fechando a conexão com o OBS.");
            _stop?.Cancel();
            loop = _loop;
            _loop = null;
        }
        if (loop is not null)
            await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifetime)
            _users = Math.Min(_users, 1);
        await ReleaseAsync().ConfigureAwait(false);
    }

    /// <summary>A conexão mudou (tela de opções): derruba a tentativa atual e tenta já com o endereço novo.</summary>
    public void Reconnect()
    {
        Interlocked.Exchange(ref _wake, 1);
        _attempt?.Cancel();
    }

    /// <summary>Um mixer acordou ou dormiu: liga ou desliga o medidor de áudio.</summary>
    public IDisposable WatchMeters()
    {
        if (Interlocked.Increment(ref _meterUsers) == 1)
            _ = UpdateSubscriptionsAsync();
        return new Callback(() =>
        {
            if (Interlocked.Decrement(ref _meterUsers) == 0)
                _ = UpdateSubscriptionsAsync();
        });
    }

    /// <summary>Um tile acordado quer a miniatura desta cena.</summary>
    public IDisposable WatchThumbnail(string scene)
    {
        _thumbnailScenes.AddOrUpdate(scene, 1, (_, n) => n + 1);
        return new Callback(() =>
        {
            if (_thumbnailScenes.AddOrUpdate(scene, 0, (_, n) => n - 1) <= 0)
                _thumbnailScenes.TryRemove(new KeyValuePair<string, int>(scene, 0));
        });
    }

    internal IReadOnlyCollection<string> WatchedThumbnails => [.. _thumbnailScenes.Where(p => p.Value > 0).Select(p => p.Key)];

    internal bool MetersSubscribed => (_subscribed & ObsProtocol.Events.InputVolumeMeters) != 0;

    // ---- pedidos ----

    /// <summary>Manda um pedido e espera a resposta. Sem conexão, devolve uma resposta de falha (não lança).</summary>
    public Task<ObsResponse> RequestAsync(string requestType, JsonObject? data = null, CancellationToken cancellationToken = default) =>
        RequestAsync(requestType, data, RequestTimeout, cancellationToken);

    /// <summary>Com prazo próprio: a troca de coleção só responde depois de recarregar tudo.</summary>
    internal async Task<ObsResponse> RequestAsync(string requestType, JsonObject? data, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open } || ConnectionState != ObsConnectionState.Ready)
            return new ObsResponse(false, 0, "Sem conexão com o OBS.", default);
        var id = Interlocked.Increment(ref _requestIds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pending = new TaskCompletionSource<ObsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        try
        {
            await SendAsync(ObsProtocol.RequestMessage(requestType, id, data), cancellationToken).ConfigureAwait(false);
            return await pending.Task.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            return new ObsResponse(false, 0, ex.Message, default);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task<ObsResponse> ToggleRecordAsync() => RequestAsync("ToggleRecord");
    public Task<ObsResponse> ToggleRecordPauseAsync() => RequestAsync("ToggleRecordPause");
    public Task<ObsResponse> CreateRecordChapterAsync() => RequestAsync("CreateRecordChapter");
    public Task<ObsResponse> StartStreamAsync() => RequestAsync("StartStream");
    public Task<ObsResponse> StopStreamAsync() => RequestAsync("StopStream");
    public Task<ObsResponse> StartReplayBufferAsync() => RequestAsync("StartReplayBuffer");
    public Task<ObsResponse> StopReplayBufferAsync() => RequestAsync("StopReplayBuffer");
    public Task<ObsResponse> SaveReplayBufferAsync() => RequestAsync("SaveReplayBuffer");
    public Task<ObsResponse> ToggleVirtualCamAsync() => RequestAsync("ToggleVirtualCam");

    public Task<ObsResponse> SetProgramSceneAsync(string scene) =>
        RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = scene });

    /// <summary>Troca a coleção de cenas. O OBS só responde quando terminou de carregar a nova.</summary>
    public Task<ObsResponse> SetSceneCollectionAsync(string collection) =>
        RequestAsync("SetCurrentSceneCollection", new JsonObject { ["sceneCollectionName"] = collection }, SwitchTimeout, CancellationToken.None);

    public Task<ObsResponse> SetProfileAsync(string profile) =>
        RequestAsync("SetCurrentProfile", new JsonObject { ["profileName"] = profile }, SwitchTimeout, CancellationToken.None);

    public Task<ObsResponse> SetPreviewSceneAsync(string scene) =>
        RequestAsync("SetCurrentPreviewScene", new JsonObject { ["sceneName"] = scene });

    public Task<ObsResponse> SetInputMuteAsync(string input, bool muted) =>
        RequestAsync("SetInputMute", new JsonObject { ["inputName"] = input, ["inputMuted"] = muted });

    /// <summary>Volume do fader em dB, entre -100 (mudo) e 26, como aceita o OBS.</summary>
    public Task<ObsResponse> SetInputVolumeDbAsync(string input, double db) =>
        RequestAsync("SetInputVolume", new JsonObject { ["inputName"] = input, ["inputVolumeDb"] = Math.Clamp(db, -100, 26) });

    // ---- laço ----

    private async Task GuardedRunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                LastError = Describe(ex);
                _log.Write("Laço do OBS caiu; recomeçando em 5 s.", ex);
                _logger.LogError(ex, "Laço do OBS caiu.");
                await Delay(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        SetConnectionState(ObsConnectionState.Connecting);
        _log.Write("Laço do OBS encerrado.");
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _wake, 0);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stop);
            _attempt = attempt;
            var endpoint = _endpoint();
            Address = $"{endpoint.Host}:{endpoint.Port}";
            Endpoint = endpoint;
            var result = await ConnectOnceAsync(endpoint, attempt.Token).ConfigureAwait(false);
            _attempt = null;
            if (stop.IsCancellationRequested)
                break;
            if (Volatile.Read(ref _wake) == 1)
            {
                failures = 0; // a pessoa mudou a conexão: tenta já
                continue;
            }

            var wait = result switch
            {
                ObsConnectionState.Ready => _backoff[0], // estava conectado e caiu (OBS fechou)
                ObsConnectionState.NeedsPassword or ObsConnectionState.AuthFailed or ObsConnectionState.Unsupported => TimeSpan.FromSeconds(60),
                _ => _backoff[Math.Min(failures++, _backoff.Length - 1)],
            };
            if (result == ObsConnectionState.Ready)
                failures = 0;
            await DelayOrWake(wait, stop).ConfigureAwait(false);
        }
    }

    /// <summary>Uma tentativa inteira: conecta, identifica, sincroniza e fica recebendo até cair. Devolve o estado final.</summary>
    private async Task<ObsConnectionState> ConnectOnceAsync(ObsEndpoint endpoint, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("obsws.json");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var final = ObsConnectionState.Unavailable;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(endpoint.Uri, timeout.Token).ConfigureAwait(false);
            }
            _socket = socket;

            var identified = await HandshakeAsync(socket, endpoint, token).ConfigureAwait(false);
            // Sem return dentro do try: o estado final é gravado depois do finally.
            final = identified;
            if (identified == ObsConnectionState.Ready)
                await RunSessionAsync(socket, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            LastError = Describe(ex);
            if (ConnectionState != ObsConnectionState.Unavailable)
                _log.Write($"OBS indisponível em {Address}: {LastError}");
        }
        finally
        {
            _socket = null;
            _subscribed = ObsProtocol.Events.None;
            foreach (var pending in _pending.Values)
                pending.TrySetResult(new ObsResponse(false, 0, "Conexão caiu.", default));
            _pending.Clear();
            await CloseQuietlyAsync(socket).ConfigureAwait(false);
        }

        if (final == ObsConnectionState.Ready)
            State.Reset();
        // Caiu depois de pronto = OBS fechou; cancelado = a pessoa mudou a conexão (ou o último widget saiu).
        SetConnectionState(token.IsCancellationRequested ? ObsConnectionState.Connecting
            : final == ObsConnectionState.Ready ? ObsConnectionState.Unavailable : final);
        return final;
    }

    /// <summary>Conexão pronta: recebe, sincroniza e acompanha até o OBS fechar ou a tentativa ser cancelada.</summary>
    private async Task RunSessionAsync(ClientWebSocket socket, CancellationToken token)
    {
        LastError = null;
        _log.Write($"Conectado ao OBS em {Address}.");
        _logger.LogInformation("Conectado ao OBS em {Address}.", Address);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var receiver = ReceiveLoopAsync(socket, session.Token);
        SetConnectionState(ObsConnectionState.Ready);
        var sync = SyncAllAsync(session.Token);
        var status = StatusLoopAsync(session.Token);
        var thumbnails = ThumbnailLoopAsync(session.Token);
        var polls = PollLoopAsync(session.Token);
        try
        {
            await receiver.ConfigureAwait(false);
        }
        finally
        {
            session.Cancel();
            await Task.WhenAll(sync, status, thumbnails, polls).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        if (socket.CloseStatus is { } close)
            _log.Write($"OBS fechou a conexão ({(int)close} {socket.CloseStatusDescription}).");
    }

    /// <summary>Hello → Identify (com a senha, se pedida) → Identified. Senha errada fecha com 4009.</summary>
    private async Task<ObsConnectionState> HandshakeAsync(ClientWebSocket socket, ObsEndpoint endpoint, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var hello = await ReceiveMessageAsync(socket, timeout.Token).ConfigureAwait(false);
        if (hello is not { Op: ObsProtocol.Hello } h)
        {
            LastError = "O servidor não respondeu como o obs-websocket v5.";
            return ObsConnectionState.Unsupported;
        }
        var (salt, challenge, rpc) = ObsProtocol.ReadHello(h.Data);
        State.ObsVersion = ObsProtocol.Str(h.Data, "obsWebSocketVersion");
        if (rpc < ObsProtocol.RpcVersion)
        {
            LastError = $"obs-websocket com rpcVersion {rpc}.";
            return ObsConnectionState.Unsupported;
        }
        string? auth = null;
        if (salt is not null && challenge is not null)
        {
            if (string.IsNullOrEmpty(endpoint.Password))
            {
                LastError = "O OBS pede senha.";
                return ObsConnectionState.NeedsPassword;
            }
            auth = ObsProtocol.Authentication(endpoint.Password, salt, challenge);
        }

        _subscribed = Wanted();
        await SendAsync(ObsProtocol.IdentifyMessage(auth, _subscribed), token).ConfigureAwait(false);
        var reply = await ReceiveMessageAsync(socket, timeout.Token).ConfigureAwait(false);
        if (reply is { Op: ObsProtocol.Identified })
            return ObsConnectionState.Ready;

        if ((int?)socket.CloseStatus == ObsProtocol.CloseAuthenticationFailed)
        {
            LastError = "Senha recusada pelo OBS.";
            return ObsConnectionState.AuthFailed;
        }
        if ((int?)socket.CloseStatus == ObsProtocol.CloseUnsupportedRpcVersion)
        {
            LastError = "Versão do obs-websocket não suportada.";
            return ObsConnectionState.Unsupported;
        }
        LastError = socket.CloseStatus is { } status ? $"OBS fechou ({(int)status} {socket.CloseStatusDescription})." : "Sem resposta ao Identify.";
        return ObsConnectionState.Unavailable;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            if (await ReceiveMessageAsync(socket, token).ConfigureAwait(false) is not { } message)
            {
                if (socket.State != WebSocketState.Open)
                    return;
                continue;
            }
            switch (message.Op)
            {
                case ObsProtocol.RequestResponse:
                    if (ObsProtocol.Str(message.Data, "requestId") is { } id && _pending.TryRemove(id, out var pending))
                        pending.TrySetResult(ObsResponse.From(message.Data));
                    break;
                case ObsProtocol.Event:
                    HandleEvent(message.Data);
                    break;
            }
        }
    }

    private void HandleEvent(JsonElement d)
    {
        var type = ObsProtocol.Str(d, "eventType") ?? "";
        var data = ObsProtocol.Obj(d, "eventData");
        if (type == "ExitStarted")
        {
            _log.Write("OBS está fechando.");
            return;
        }
        var update = State.ApplyEvent(type, data);
        if (type != "InputVolumeMeters")
            _log.Write($"<- {type}");
        if (update.NeedsScenes)
            _ = SyncScenesAsync(CancellationToken.None);
        if (update.NeedsInputs)
            _ = SyncInputsAsync(CancellationToken.None);
        SyncMore(update);
        if (update.NeedsOutputs)
            _ = SyncOutputsAsync(CancellationToken.None);
        if (!update.Changed)
            return;
        if (type == "InputVolumeMeters")
            RaiseMeterChanged();
        else
            RaiseChanged();
    }

    // ---- sincronização ----

    private async Task SyncAllAsync(CancellationToken token)
    {
        await SyncScenesAsync(token).ConfigureAwait(false);
        var studio = await RequestAsync("GetStudioModeEnabled", null, token).ConfigureAwait(false);
        if (studio.Ok)
            State.ApplyStudioMode(studio.Data);
        if (State.StudioMode)
        {
            var preview = await RequestAsync("GetCurrentPreviewScene", null, token).ConfigureAwait(false);
            if (preview.Ok)
                State.ApplyPreview(ObsProtocol.Str(preview.Data, "sceneName") ?? ObsProtocol.Str(preview.Data, "currentPreviewSceneName"));
        }
        await SyncOutputsAsync(token).ConfigureAwait(false);
        var replay = await RequestAsync("GetReplayBufferStatus", null, token).ConfigureAwait(false);
        if (replay.Ok)
            State.ApplyReplayBuffer(replay.Data);
        var vcam = await RequestAsync("GetVirtualCamStatus", null, token).ConfigureAwait(false);
        if (vcam.Ok)
            State.ApplyVirtualCam(vcam.Data);
        await SyncInputsAsync(token).ConfigureAwait(false);
        await SyncConfigAsync(token).ConfigureAwait(false);
        await SyncMoreAsync(token).ConfigureAwait(false);
        RaiseChanged();
    }

    private async Task SyncScenesAsync(CancellationToken token)
    {
        var scenes = await RequestAsync("GetSceneList", null, token).ConfigureAwait(false);
        if (scenes.Ok && State.ApplySceneList(scenes.Data).Changed)
            RaiseChanged();
    }

    /// <summary>Coleções de cenas e perfis: as listas e qual está ativa.</summary>
    private async Task SyncConfigAsync(CancellationToken token)
    {
        var collections = await RequestAsync("GetSceneCollectionList", null, token).ConfigureAwait(false);
        if (collections.Ok)
            State.ApplySceneCollectionList(collections.Data);
        var profiles = await RequestAsync("GetProfileList", null, token).ConfigureAwait(false);
        if (profiles.Ok)
            State.ApplyProfileList(profiles.Data);
    }

    private async Task SyncOutputsAsync(CancellationToken token)
    {
        var record = await RequestAsync("GetRecordStatus", null, token).ConfigureAwait(false);
        var changed = record.Ok && State.ApplyRecordStatus(record.Data).Changed;
        var stream = await RequestAsync("GetStreamStatus", null, token).ConfigureAwait(false);
        changed |= stream.Ok && State.ApplyStreamStatus(stream.Data).Changed;
        if (changed)
            RaiseChanged();
    }

    /// <summary>
    /// Só fontes com áudio entram no mixer: são as que respondem ao GetInputMute (vídeo e imagem respondem erro).
    /// </summary>
    private async Task SyncInputsAsync(CancellationToken token)
    {
        var list = await RequestAsync("GetInputList", null, token).ConfigureAwait(false);
        if (!list.Ok)
            return;
        if (State.ApplyInputList(list.Data).Changed)
            RaiseChanged();
        var audio = new List<AudioInput>();
        foreach (var item in ObsProtocol.Arr(list.Data, "inputs").EnumerateArrayOrEmpty())
        {
            if (ObsProtocol.Str(item, "inputName") is not { } name)
                continue;
            var mute = await RequestAsync("GetInputMute", new JsonObject { ["inputName"] = name }, token).ConfigureAwait(false);
            if (!mute.Ok)
                continue;
            var volume = await RequestAsync("GetInputVolume", new JsonObject { ["inputName"] = name }, token).ConfigureAwait(false);
            audio.Add(new AudioInput(name, ObsProtocol.Str(item, "unversionedInputKind") ?? ObsProtocol.Str(item, "inputKind") ?? "")
            {
                Muted = ObsProtocol.Bool(mute.Data, "inputMuted") ?? false,
                VolumeDb = volume.Ok ? ObsProtocol.Num(volume.Data, "inputVolumeDb") ?? 0 : 0,
            });
        }
        State.ApplyAudioInputs(audio);
        RaiseChanged();
    }

    /// <summary>Gravando ou ao vivo: status a cada segundo (tempo, kbps, quadros perdidos).</summary>
    private async Task StatusLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var changed = false;
                if (State.Record.Active)
                {
                    var record = await RequestAsync("GetRecordStatus", null, token).ConfigureAwait(false);
                    changed |= record.Ok && State.ApplyRecordStatus(record.Data).Changed;
                }
                if (State.Stream.Active)
                {
                    var stream = await RequestAsync("GetStreamStatus", null, token).ConfigureAwait(false);
                    changed |= stream.Ok && State.ApplyStreamStatus(stream.Data).Changed;
                }
                if (changed)
                    RaiseChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Miniatura das cenas pedidas, em JPEG pequeno, uma rodada por segundo.</summary>
    private async Task ThumbnailLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(ThumbnailInterval, _time);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var changed = false;
                foreach (var scene in WatchedThumbnails)
                {
                    if (!State.Scenes.Contains(scene))
                        continue;
                    var shot = await RequestAsync("GetSourceScreenshot", new JsonObject
                    {
                        ["sourceName"] = scene,
                        ["imageFormat"] = "jpg",
                        ["imageWidth"] = ThumbnailWidth,
                        ["imageCompressionQuality"] = 70,
                    }, token).ConfigureAwait(false);
                    if (shot.Ok && DecodeDataUri(ObsProtocol.Str(shot.Data, "imageData")) is { } jpeg)
                        changed |= State.ApplyThumbnail(scene, jpeg).Changed;
                }
                if (changed)
                    RaiseChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static byte[]? DecodeDataUri(string? uri)
    {
        if (uri is null)
            return null;
        var comma = uri.IndexOf(',');
        try
        {
            return Convert.FromBase64String(comma >= 0 ? uri[(comma + 1)..] : uri);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private ObsProtocol.Events Wanted() =>
        BaseEvents | (Volatile.Read(ref _meterUsers) > 0 ? ObsProtocol.Events.InputVolumeMeters : ObsProtocol.Events.None);

    private async Task UpdateSubscriptionsAsync()
    {
        var wanted = Wanted();
        if (ConnectionState != ObsConnectionState.Ready || wanted == _subscribed)
            return;
        _subscribed = wanted;
        try
        {
            await SendAsync(ObsProtocol.ReidentifyMessage(wanted), CancellationToken.None).ConfigureAwait(false);
            _log.Write((wanted & ObsProtocol.Events.InputVolumeMeters) != 0 ? "Medidor de áudio ligado." : "Medidor de áudio desligado.");
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    // ---- transporte ----

    private static async Task<ObsMessage?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseQuietlyAsync(socket).ConfigureAwait(false);
                return null;
            }
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return result.MessageType == WebSocketMessageType.Text
            ? ObsProtocol.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length))
            : null;
    }

    private async Task SendAsync(string text, CancellationToken token)
    {
        if (_socket is not { State: WebSocketState.Open } socket)
            throw new InvalidOperationException("Sem conexão com o OBS.");
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, token).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task Delay(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, _time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Espera, mas acorda na hora se a pessoa mudou a conexão (<see cref="Reconnect"/>).</summary>
    private async Task DelayOrWake(TimeSpan delay, CancellationToken stop)
    {
        var end = _time.GetUtcNow() + delay;
        while (!stop.IsCancellationRequested && _time.GetUtcNow() < end && Volatile.Read(ref _wake) == 0)
            await Delay(TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(1, (end - _time.GetUtcNow()).TotalMilliseconds))), stop).ConfigureAwait(false);
    }

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException is { } inner)
            text += $" ({inner.GetType().Name}: {inner.Message})";
        return text;
    }

    private void SetConnectionState(ObsConnectionState state)
    {
        if (ConnectionState == state)
            return;
        ConnectionState = state;
        _log.Write($"Estado: {state}{(LastError is { } e && state != ObsConnectionState.Ready ? $" ({e})" : "")}");
        RaiseChanged();
    }

    /// <summary>Medidor chega ~20 vezes por segundo; os tiles redesenham no máximo 10.</summary>
    private void RaiseMeterChanged()
    {
        var now = _time.GetUtcNow();
        if (now - _lastMeterRaise >= MeterFrame)
        {
            _lastMeterRaise = now;
            RaiseChanged();
            return;
        }
        if (Interlocked.Exchange(ref _meterRaiseQueued, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            await Delay(MeterFrame, CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref _meterRaiseQueued, 0);
            _lastMeterRaise = _time.GetUtcNow();
            RaiseChanged();
        });
    }

    /// <summary>Avisa os widgets fora do laço: um handler lento (tela, opções) não pode travar a conexão.</summary>
    private void RaiseChanged()
    {
        if (Changed is not { } handlers)
            return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                handlers.Invoke();
            }
            catch (Exception ex)
            {
                _log.Write("Erro ao avisar os widgets.", ex);
                _logger.LogWarning(ex, "Erro ao avisar os widgets.");
            }
        });
    }

    private sealed class Lease(ObsSession session) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                _ = session.ReleaseAsync();
        }
    }

    private sealed class Callback(Action action) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                action();
        }
    }
}
