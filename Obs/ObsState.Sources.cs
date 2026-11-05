using System.Text.Json;

namespace OpenWigiDash.ObsPlugin.Obs;

/// <summary>Uma fonte do OBS (qualquer tipo), pelo nome e pelo tipo sem versão (ex.: <c>ffmpeg_source</c>).</summary>
public sealed record InputInfo(string Name, string Kind);

/// <summary>Um item de uma cena (a fonte posta na cena), com o id que o OBS usa para mostrar e esconder.</summary>
public sealed record SceneItem(int Id, string SourceName, string? InputKind, bool Enabled, bool IsGroup);

/// <summary>Um filtro de uma fonte ou cena.</summary>
public sealed record SourceFilter(string Name, string Kind, bool Enabled);

/// <summary>Estado de uma fonte de mídia na última leitura. O cursor anda sozinho enquanto toca.</summary>
public sealed record MediaStatus(string State, double? DurationMs, double? CursorMs, DateTimeOffset ObservedAt)
{
    public bool Playing => State is "OBS_MEDIA_STATE_PLAYING" or "OBS_MEDIA_STATE_OPENING" or "OBS_MEDIA_STATE_BUFFERING";
    public bool Paused => State == "OBS_MEDIA_STATE_PAUSED";
    public bool Ended => State is "OBS_MEDIA_STATE_ENDED" or "OBS_MEDIA_STATE_STOPPED" or "OBS_MEDIA_STATE_NONE";
    public bool Failed => State == "OBS_MEDIA_STATE_ERROR";

    /// <summary>Posição agora (o OBS manda o cursor só quando perguntamos).</summary>
    public TimeSpan? Position(DateTimeOffset now)
    {
        if (CursorMs is not { } cursor)
            return null;
        var ms = cursor + (Playing && State == "OBS_MEDIA_STATE_PLAYING" ? Math.Max(0, (now - ObservedAt).TotalMilliseconds) : 0);
        return TimeSpan.FromMilliseconds(DurationMs is { } d and > 0 ? Math.Min(ms, d) : ms);
    }

    public TimeSpan? Remaining(DateTimeOffset now) =>
        DurationMs is { } d and > 0 && Position(now) is { } p ? TimeSpan.FromMilliseconds(d) - p : null;

    /// <summary>0 a 1; <c>null</c> quando a mídia não tem duração (ex.: stream).</summary>
    public double? Progress(DateTimeOffset now) =>
        DurationMs is { } d and > 0 && Position(now) is { } p ? Math.Clamp(p.TotalMilliseconds / d, 0, 1) : null;
}

/// <summary>GetStats: carga do OBS e quadros perdidos no render (GPU) e na saída (codificação).</summary>
public sealed record ObsStats(double CpuPercent, double MemoryMb, double Fps, double FrameTimeMs,
    long RenderSkipped, long RenderTotal, long OutputSkipped, long OutputTotal)
{
    public double RenderDroppedPercent => RenderTotal > 0 ? RenderSkipped * 100.0 / RenderTotal : 0;
    public double OutputDroppedPercent => OutputTotal > 0 ? OutputSkipped * 100.0 / OutputTotal : 0;
}

public sealed partial class ObsState
{
    internal const int StatsHistoryLength = 40;

    private IReadOnlyList<InputInfo> _allInputs = [];
    private Dictionary<string, IReadOnlyList<SceneItem>> _sceneItems = new(StringComparer.Ordinal);
    private Dictionary<string, IReadOnlyList<SourceFilter>> _filters = new(StringComparer.Ordinal);
    private Dictionary<string, MediaStatus> _media = new(StringComparer.Ordinal);

    /// <summary>Todas as fontes (não só as de áudio), na ordem do OBS.</summary>
    public IReadOnlyList<InputInfo> Inputs
    {
        get
        {
            lock (_gate)
                return _allInputs;
        }
    }

    public ObsStats? Stats { get; private set; }

    /// <summary>CPU do OBS nas últimas leituras, para o minigráfico do Status.</summary>
    public IReadOnlyList<double> CpuHistory { get; private set; } = [];

    /// <summary>Transição atual do modo estúdio (ex.: "Fade") e a duração em ms.</summary>
    public string? Transition { get; private set; }
    public double? TransitionDurationMs { get; private set; }

    /// <summary>Uma transição está rodando agora.</summary>
    public bool InTransition { get; private set; }

    /// <summary>Itens da cena, de cima para baixo como na lista do OBS; <c>null</c> se ainda não foi lida.</summary>
    public IReadOnlyList<SceneItem>? SceneItems(string? scene)
    {
        if (scene is null)
            return null;
        lock (_gate)
            return _sceneItems.GetValueOrDefault(scene);
    }

    /// <summary>Filtros da fonte (ou cena); <c>null</c> se ainda não foram lidos.</summary>
    public IReadOnlyList<SourceFilter>? Filters(string? source)
    {
        if (source is null)
            return null;
        lock (_gate)
            return _filters.GetValueOrDefault(source);
    }

    public MediaStatus? Media(string? input)
    {
        if (input is null)
            return null;
        lock (_gate)
            return _media.GetValueOrDefault(input);
    }

    private void ResetMore()
    {
        _allInputs = [];
        _sceneItems = new(StringComparer.Ordinal);
        _filters = new(StringComparer.Ordinal);
        _media = new(StringComparer.Ordinal);
        Stats = null;
        CpuHistory = [];
        Transition = null;
        TransitionDurationMs = null;
        InTransition = false;
    }

    // ---- respostas ----

    /// <summary>GetInputList inteiro (o mixer filtra as de áudio à parte).</summary>
    internal ObsUpdate ApplyInputList(JsonElement data)
    {
        IReadOnlyList<InputInfo> list = [.. ObsProtocol.Arr(data, "inputs").EnumerateArrayOrEmpty()
            .Where(i => ObsProtocol.Str(i, "inputName") is not null)
            .Select(i => new InputInfo(ObsProtocol.Str(i, "inputName")!,
                ObsProtocol.Str(i, "unversionedInputKind") ?? ObsProtocol.Str(i, "inputKind") ?? ""))];
        lock (_gate)
        {
            if (list.SequenceEqual(_allInputs))
                return ObsUpdate.None;
            _allInputs = list;
            // Mídia de fonte que sumiu não volta.
            if (_media.Keys.Any(k => list.All(i => i.Name != k)))
                _media = _media.Where(m => list.Any(i => i.Name == m.Key)).ToDictionary(StringComparer.Ordinal);
        }
        return ObsUpdate.Change;
    }

    /// <summary>GetSceneItemList: o índice 0 é o de baixo; invertemos para ficar como o OBS mostra.</summary>
    internal ObsUpdate ApplySceneItems(string scene, JsonElement data)
    {
        IReadOnlyList<SceneItem> items = [.. ObsProtocol.Arr(data, "sceneItems").EnumerateArrayOrEmpty()
            .Where(i => ObsProtocol.Num(i, "sceneItemId") is not null && ObsProtocol.Str(i, "sourceName") is not null)
            .Select(i => (Index: ObsProtocol.Num(i, "sceneItemIndex") ?? 0, Item: new SceneItem(
                (int)ObsProtocol.Num(i, "sceneItemId")!.Value,
                ObsProtocol.Str(i, "sourceName")!,
                ObsProtocol.Str(i, "inputKind"),
                ObsProtocol.Bool(i, "sceneItemEnabled") ?? false,
                ObsProtocol.Bool(i, "isGroup") ?? false)))
            .OrderByDescending(x => x.Index)
            .Select(x => x.Item)];
        lock (_gate)
        {
            if (_sceneItems.TryGetValue(scene, out var old) && old.SequenceEqual(items))
                return ObsUpdate.None;
            _sceneItems = new Dictionary<string, IReadOnlyList<SceneItem>>(_sceneItems, StringComparer.Ordinal) { [scene] = items };
        }
        return ObsUpdate.Change;
    }

    internal ObsUpdate ApplyFilters(string source, JsonElement data)
    {
        IReadOnlyList<SourceFilter> filters = [.. ObsProtocol.Arr(data, "filters").EnumerateArrayOrEmpty()
            .Where(f => ObsProtocol.Str(f, "filterName") is not null)
            .OrderBy(f => ObsProtocol.Num(f, "filterIndex") ?? 0)
            .Select(f => new SourceFilter(ObsProtocol.Str(f, "filterName")!, ObsProtocol.Str(f, "filterKind") ?? "",
                ObsProtocol.Bool(f, "filterEnabled") ?? false))];
        lock (_gate)
        {
            if (_filters.TryGetValue(source, out var old) && old.SequenceEqual(filters))
                return ObsUpdate.None;
            _filters = new Dictionary<string, IReadOnlyList<SourceFilter>>(_filters, StringComparer.Ordinal) { [source] = filters };
        }
        return ObsUpdate.Change;
    }

    /// <summary>GetMediaInputStatus: estado, duração e cursor em ms (os dois podem vir nulos).</summary>
    internal ObsUpdate ApplyMedia(string input, JsonElement data)
    {
        var next = new MediaStatus(ObsProtocol.Str(data, "mediaState") ?? "OBS_MEDIA_STATE_NONE",
            ObsProtocol.Num(data, "mediaDuration"), ObsProtocol.Num(data, "mediaCursor"), time.GetUtcNow());
        lock (_gate)
        {
            var old = _media.GetValueOrDefault(input);
            _media = new Dictionary<string, MediaStatus>(_media, StringComparer.Ordinal) { [input] = next };
            // O cursor sempre muda; só redesenha de verdade se o estado ou a duração mudou, ou o cursor pulou.
            var jumped = old?.Position(next.ObservedAt) is { } guess && next.Position(next.ObservedAt) is { } real
                && Math.Abs((guess - real).TotalSeconds) > 1.5;
            return new ObsUpdate(old is null || old.State != next.State || old.DurationMs != next.DurationMs || jumped);
        }
    }

    /// <summary>GetStats. A CPU entra no histórico do minigráfico.</summary>
    internal ObsUpdate ApplyStats(JsonElement data)
    {
        var next = new ObsStats(
            ObsProtocol.Num(data, "cpuUsage") ?? 0,
            ObsProtocol.Num(data, "memoryUsage") ?? 0,
            ObsProtocol.Num(data, "activeFps") ?? 0,
            ObsProtocol.Num(data, "averageFrameRenderTime") ?? 0,
            (long)(ObsProtocol.Num(data, "renderSkippedFrames") ?? 0),
            (long)(ObsProtocol.Num(data, "renderTotalFrames") ?? 0),
            (long)(ObsProtocol.Num(data, "outputSkippedFrames") ?? 0),
            (long)(ObsProtocol.Num(data, "outputTotalFrames") ?? 0));
        var old = Stats;
        Stats = next;
        CpuHistory = [.. CpuHistory.TakeLast(StatsHistoryLength - 1), next.CpuPercent];
        return new ObsUpdate(old is null
            || Math.Round(old.CpuPercent, 1) != Math.Round(next.CpuPercent, 1)
            || Math.Round(old.Fps) != Math.Round(next.Fps)
            || old.RenderSkipped != next.RenderSkipped
            || old.OutputSkipped != next.OutputSkipped);
    }

    /// <summary>GetCurrentSceneTransition.</summary>
    internal ObsUpdate ApplyTransition(JsonElement data)
    {
        var name = ObsProtocol.Str(data, "transitionName");
        var duration = ObsProtocol.Num(data, "transitionDuration");
        if (name == Transition && duration == TransitionDurationMs)
            return ObsUpdate.None;
        Transition = name;
        TransitionDurationMs = duration;
        return ObsUpdate.Change;
    }

    // ---- eventos ----

    private ObsUpdate ApplyMoreEvent(string type, JsonElement data)
    {
        switch (type)
        {
            case "SceneItemEnableStateChanged":
            {
                var scene = ObsProtocol.Str(data, "sceneName");
                var id = ObsProtocol.Num(data, "sceneItemId");
                var enabled = ObsProtocol.Bool(data, "sceneItemEnabled");
                if (scene is null || id is null || enabled is null)
                    return ObsUpdate.None;
                lock (_gate)
                {
                    if (!_sceneItems.TryGetValue(scene, out var items))
                        return ObsUpdate.None;
                    var updated = items.Select(i => i.Id == (int)id.Value ? i with { Enabled = enabled.Value } : i).ToList();
                    if (updated.SequenceEqual(items))
                        return ObsUpdate.None;
                    _sceneItems = new Dictionary<string, IReadOnlyList<SceneItem>>(_sceneItems, StringComparer.Ordinal) { [scene] = updated };
                }
                return ObsUpdate.Change;
            }
            case "SceneItemCreated" or "SceneItemRemoved" or "SceneItemListReindexed":
                return ObsProtocol.Str(data, "sceneName") is { } changedScene && SceneItems(changedScene) is not null
                    ? new ObsUpdate(false, SceneItemsOf: changedScene) : ObsUpdate.None;
            case "SourceFilterEnableStateChanged":
            {
                var source = ObsProtocol.Str(data, "sourceName");
                var name = ObsProtocol.Str(data, "filterName");
                var enabled = ObsProtocol.Bool(data, "filterEnabled");
                if (source is null || name is null || enabled is null)
                    return ObsUpdate.None;
                lock (_gate)
                {
                    if (!_filters.TryGetValue(source, out var filters))
                        return ObsUpdate.None;
                    var updated = filters.Select(f => f.Name == name ? f with { Enabled = enabled.Value } : f).ToList();
                    if (updated.SequenceEqual(filters))
                        return ObsUpdate.None;
                    _filters = new Dictionary<string, IReadOnlyList<SourceFilter>>(_filters, StringComparer.Ordinal) { [source] = updated };
                }
                return ObsUpdate.Change;
            }
            case "SourceFilterCreated" or "SourceFilterRemoved" or "SourceFilterNameChanged" or "SourceFilterListReindexed":
                return ObsProtocol.Str(data, "sourceName") is { } filtered && Filters(filtered) is not null
                    ? new ObsUpdate(false, FiltersOf: filtered) : ObsUpdate.None;
            case "MediaInputPlaybackStarted" or "MediaInputPlaybackEnded" or "MediaInputActionTriggered":
            {
                if (ObsProtocol.Str(data, "inputName") is not { } input)
                    return ObsUpdate.None;
                // Muda o estado na hora (o tile responde ao toque sem esperar) e pede o status para acertar o cursor.
                var guess = type switch
                {
                    "MediaInputPlaybackStarted" => "OBS_MEDIA_STATE_PLAYING",
                    "MediaInputPlaybackEnded" => "OBS_MEDIA_STATE_ENDED",
                    _ => ObsProtocol.Str(data, "mediaAction") switch
                    {
                        "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PLAY" or "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_RESTART" => "OBS_MEDIA_STATE_PLAYING",
                        "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PAUSE" => "OBS_MEDIA_STATE_PAUSED",
                        "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_STOP" => "OBS_MEDIA_STATE_STOPPED",
                        _ => null,
                    },
                };
                var changed = false;
                lock (_gate)
                {
                    if (_media.TryGetValue(input, out var media) && guess is not null && guess != media.State)
                    {
                        var now = time.GetUtcNow();
                        var frozen = media with { CursorMs = media.Position(now)?.TotalMilliseconds ?? media.CursorMs, ObservedAt = now, State = guess };
                        _media = new Dictionary<string, MediaStatus>(_media, StringComparer.Ordinal) { [input] = frozen };
                        changed = true;
                    }
                }
                return Media(input) is not null ? new ObsUpdate(changed, MediaOf: input) : ObsUpdate.None;
            }
            case "CurrentSceneTransitionChanged":
                // O nome vem no evento; a duração da transição nova vem no próximo GetCurrentSceneTransition.
                if (ObsProtocol.Str(data, "transitionName") is not { } transition || transition == Transition)
                    return ObsUpdate.None;
                Transition = transition;
                return new ObsUpdate(true, NeedsTransition: true);
            case "CurrentSceneTransitionDurationChanged":
                TransitionDurationMs = ObsProtocol.Num(data, "transitionDuration");
                return ObsUpdate.Change;
            case "SceneTransitionStarted":
                InTransition = true;
                return ObsUpdate.Change;
            case "SceneTransitionEnded" or "SceneTransitionVideoEnded":
                if (!InTransition)
                    return ObsUpdate.None;
                InTransition = false;
                return ObsUpdate.Change;
            default:
                return ObsUpdate.None;
        }
    }
}
