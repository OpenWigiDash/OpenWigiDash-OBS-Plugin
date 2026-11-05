using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace OpenWigiDash.ObsPlugin.Obs;

/// <summary>
/// Fase 2: itens de cena, filtros, mídia, estatísticas e transição. Cada um só é lido para quem pediu (um tile
/// acordado com aquela cena, fonte ou mídia), como a miniatura.
/// </summary>
public sealed partial class ObsSession
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    /// <summary>Mídia tocando: acerta o cursor a cada 2 s (no meio ele anda sozinho).</summary>
    internal static readonly TimeSpan MediaPollInterval = TimeSpan.FromSeconds(2);

    private readonly Watchers _sceneItemWatch = new();
    private readonly Watchers _filterWatch = new();
    private readonly Watchers _mediaWatch = new();
    private int _statsUsers;
    private DateTimeOffset _lastMediaPoll;

    /// <summary>Um tile de Fonte visível quer os itens desta cena.</summary>
    public IDisposable WatchSceneItems(string scene) => _sceneItemWatch.Add(scene, () => _ = SyncSceneItemsAsync(scene, CancellationToken.None));

    /// <summary>Um tile de Filtro quer os filtros desta fonte (ou cena).</summary>
    public IDisposable WatchFilters(string source) => _filterWatch.Add(source, () => _ = SyncFiltersAsync(source, CancellationToken.None));

    /// <summary>Um tile de Mídia quer o estado desta fonte.</summary>
    public IDisposable WatchMedia(string input) => _mediaWatch.Add(input, () => _ = SyncMediaAsync(input, CancellationToken.None));

    /// <summary>Um tile de Status acordado: GetStats a cada segundo.</summary>
    public IDisposable WatchStats()
    {
        if (Interlocked.Increment(ref _statsUsers) == 1)
            _ = SyncStatsAsync(CancellationToken.None);
        return new Callback(() => Interlocked.Decrement(ref _statsUsers));
    }

    internal IReadOnlyCollection<string> WatchedSceneItems => _sceneItemWatch.Keys;
    internal IReadOnlyCollection<string> WatchedFilters => _filterWatch.Keys;
    internal IReadOnlyCollection<string> WatchedMedia => _mediaWatch.Keys;
    internal bool StatsWatched => Volatile.Read(ref _statsUsers) > 0;

    // ---- comandos ----

    public Task<ObsResponse> SetSceneItemEnabledAsync(string scene, int itemId, bool enabled) =>
        RequestAsync("SetSceneItemEnabled", new JsonObject { ["sceneName"] = scene, ["sceneItemId"] = itemId, ["sceneItemEnabled"] = enabled });

    public Task<ObsResponse> SetFilterEnabledAsync(string source, string filter, bool enabled) =>
        RequestAsync("SetSourceFilterEnabled", new JsonObject { ["sourceName"] = source, ["filterName"] = filter, ["filterEnabled"] = enabled });

    public Task<ObsResponse> MediaActionAsync(string input, MediaAction action) =>
        RequestAsync("TriggerMediaInputAction", new JsonObject
        {
            ["inputName"] = input,
            ["mediaAction"] = action switch
            {
                MediaAction.Play => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PLAY",
                MediaAction.Pause => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PAUSE",
                MediaAction.Stop => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_STOP",
                _ => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_RESTART",
            },
        });

    public Task<ObsResponse> SetStudioModeAsync(bool enabled) =>
        RequestAsync("SetStudioModeEnabled", new JsonObject { ["studioModeEnabled"] = enabled });

    /// <summary>Manda a prévia para o programa com a transição atual (só no modo estúdio).</summary>
    public Task<ObsResponse> TriggerTransitionAsync() => RequestAsync("TriggerStudioModeTransition");

    // ---- sincronização ----

    /// <summary>Depois de conectar: transição e tudo o que algum tile está acompanhando.</summary>
    private async Task SyncMoreAsync(CancellationToken token)
    {
        await SyncTransitionAsync(token).ConfigureAwait(false);
        foreach (var scene in _sceneItemWatch.Keys)
            await SyncSceneItemsAsync(scene, token).ConfigureAwait(false);
        foreach (var source in _filterWatch.Keys)
            await SyncFiltersAsync(source, token).ConfigureAwait(false);
        foreach (var input in _mediaWatch.Keys)
            await SyncMediaAsync(input, token).ConfigureAwait(false);
        if (StatsWatched)
            await SyncStatsAsync(token).ConfigureAwait(false);
    }

    /// <summary>O que um evento pediu para ler de novo.</summary>
    private void SyncMore(ObsUpdate update)
    {
        if (update.SceneItemsOf is { } scene)
            _ = SyncSceneItemsAsync(scene, CancellationToken.None);
        if (update.FiltersOf is { } source)
            _ = SyncFiltersAsync(source, CancellationToken.None);
        if (update.MediaOf is { } input)
            _ = SyncMediaAsync(input, CancellationToken.None);
        if (update.NeedsTransition)
            _ = SyncTransitionAsync(CancellationToken.None);
    }

    private async Task SyncSceneItemsAsync(string scene, CancellationToken token)
    {
        var response = await RequestAsync("GetSceneItemList", new JsonObject { ["sceneName"] = scene }, token).ConfigureAwait(false);
        if (response.Ok && State.ApplySceneItems(scene, response.Data).Changed)
            RaiseChanged();
    }

    private async Task SyncFiltersAsync(string source, CancellationToken token)
    {
        var response = await RequestAsync("GetSourceFilterList", new JsonObject { ["sourceName"] = source }, token).ConfigureAwait(false);
        if (response.Ok && State.ApplyFilters(source, response.Data).Changed)
            RaiseChanged();
    }

    private async Task SyncMediaAsync(string input, CancellationToken token)
    {
        var response = await RequestAsync("GetMediaInputStatus", new JsonObject { ["inputName"] = input }, token).ConfigureAwait(false);
        if (response.Ok && State.ApplyMedia(input, response.Data).Changed)
            RaiseChanged();
    }

    private async Task SyncStatsAsync(CancellationToken token)
    {
        var response = await RequestAsync("GetStats", null, token).ConfigureAwait(false);
        if (response.Ok && State.ApplyStats(response.Data).Changed)
            RaiseChanged();
    }

    private async Task SyncTransitionAsync(CancellationToken token)
    {
        var response = await RequestAsync("GetCurrentSceneTransition", null, token).ConfigureAwait(false);
        if (response.Ok && State.ApplyTransition(response.Data).Changed)
            RaiseChanged();
    }

    /// <summary>Uma rodada por segundo: estatísticas (se alguém olha) e o cursor da mídia que está tocando.</summary>
    private async Task PollLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval, _time);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (StatsWatched)
                    await SyncStatsAsync(token).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                if (now - _lastMediaPoll < MediaPollInterval - TimeSpan.FromMilliseconds(50))
                    continue;
                _lastMediaPoll = now;
                foreach (var input in _mediaWatch.Keys)
                {
                    if (State.Media(input) is { Playing: true })
                        await SyncMediaAsync(input, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Contagem de quem acompanha cada nome; o primeiro dispara a leitura (se já estiver conectado).</summary>
    private sealed class Watchers
    {
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Keys => [.. _counts.Where(p => p.Value > 0).Select(p => p.Key)];

        public IDisposable Add(string key, Action first)
        {
            if (_counts.AddOrUpdate(key, 1, (_, n) => n + 1) == 1)
                first();
            return new Callback(() =>
            {
                if (_counts.AddOrUpdate(key, 0, (_, n) => n - 1) <= 0)
                    _counts.TryRemove(new KeyValuePair<string, int>(key, 0));
            });
        }
    }
}

/// <summary>Ações de mídia do obs-websocket.</summary>
public enum MediaAction
{
    Play,
    Pause,
    Stop,
    Restart,
}
