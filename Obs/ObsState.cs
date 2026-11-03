using System.Text.Json;

namespace OpenWigiDash.ObsPlugin.Obs;

/// <summary>Uma saída do OBS com tempo (gravação ou live), como o último status contou.</summary>
public sealed record OutputStatus
{
    public bool Active { get; init; }
    public bool Paused { get; init; }
    public bool Reconnecting { get; init; }
    /// <summary>Tempo da saída na leitura <see cref="ObservedAt"/>.</summary>
    public TimeSpan Timecode { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public long Bytes { get; init; }
    public long SkippedFrames { get; init; }
    public long TotalFrames { get; init; }
    /// <summary>Taxa entre as duas últimas leituras, em kbps; <c>null</c> antes da segunda.</summary>
    public double? Kbps { get; init; }

    /// <summary>Tempo agora, contando o que passou desde a leitura (parado se pausado).</summary>
    public TimeSpan Elapsed(DateTimeOffset now) =>
        !Active ? TimeSpan.Zero : Paused ? Timecode : Timecode + (now - ObservedAt).Max(TimeSpan.Zero);

    /// <summary>Quadros perdidos em porcentagem (0..100).</summary>
    public double DroppedPercent => TotalFrames > 0 ? SkippedFrames * 100.0 / TotalFrames : 0;
}

/// <summary>Uma fonte de áudio do mixer.</summary>
public sealed record AudioInput(string Name, string Kind)
{
    public bool Muted { get; init; }
    /// <summary>Volume do fader em dB (-100 = mudo no OBS).</summary>
    public double VolumeDb { get; init; }
    /// <summary>Pico atual de 0 a 1 (do InputVolumeMeters), já em escala de dB para o medidor.</summary>
    public double Level { get; init; }
}

/// <summary>Miniatura de uma cena (JPEG do GetSourceScreenshot) e quando chegou.</summary>
public sealed record SceneThumbnail(byte[] Jpeg, DateTimeOffset At);

/// <summary>O que um evento ou resposta mudou e o que falta pedir.</summary>
internal readonly record struct ObsUpdate(bool Changed, bool NeedsScenes = false, bool NeedsInputs = false, bool NeedsOutputs = false)
{
    public static ObsUpdate None => default;
    public static ObsUpdate Change => new(true);
}

/// <summary>
/// Espelho do OBS que os tiles leem. Só muda pelas respostas e eventos que a <see cref="ObsSession"/> repassa;
/// cada leitura é uma foto (records imutáveis trocados inteiros).
/// </summary>
public sealed class ObsState(TimeProvider time)
{
    private readonly Lock _gate = new();
    private Dictionary<string, AudioInput> _inputs = new(StringComparer.Ordinal);
    private Dictionary<string, SceneThumbnail> _thumbnails = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Scenes { get; private set; } = [];
    public string? ProgramScene { get; private set; }
    public string? PreviewScene { get; private set; }
    public bool StudioMode { get; private set; }
    public OutputStatus Record { get; private set; } = new();
    public OutputStatus Stream { get; private set; } = new();
    /// <summary>Últimas taxas da live (kbps), uma por leitura, para o minigráfico.</summary>
    public IReadOnlyList<double> KbpsHistory { get; private set; } = [];
    internal const int KbpsHistoryLength = 40;
    public bool ReplayBuffer { get; private set; }
    public string? LastReplayPath { get; private set; }
    public DateTimeOffset? LastReplayAt { get; private set; }
    public bool VirtualCam { get; private set; }
    public string? ObsVersion { get; internal set; }

    /// <summary>Fontes de áudio pelo nome, na ordem em que o OBS listou.</summary>
    public IReadOnlyList<AudioInput> AudioInputs
    {
        get
        {
            lock (_gate)
                return [.. _inputs.Values];
        }
    }

    public AudioInput? FindInput(string? name)
    {
        if (name is null)
            return null;
        lock (_gate)
            return _inputs.GetValueOrDefault(name);
    }

    public SceneThumbnail? Thumbnail(string? scene)
    {
        if (scene is null)
            return null;
        lock (_gate)
            return _thumbnails.GetValueOrDefault(scene);
    }

    /// <summary>Esquece tudo (desconectou).</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            Scenes = [];
            ProgramScene = PreviewScene = null;
            StudioMode = ReplayBuffer = VirtualCam = false;
            Record = new();
            Stream = new();
            KbpsHistory = [];
            _inputs = new(StringComparer.Ordinal);
            _thumbnails = new(StringComparer.Ordinal);
        }
    }

    // ---- respostas ----

    /// <summary>GetSceneList: a lista vem com índice 0 embaixo; o OBS mostra de cima para baixo, então invertemos.</summary>
    internal ObsUpdate ApplySceneList(JsonElement data)
    {
        var list = ReadScenes(ObsProtocol.Arr(data, "scenes"));
        lock (_gate)
        {
            Scenes = list;
            if (ObsProtocol.Str(data, "currentProgramSceneName") is { } program)
                ProgramScene = program;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("currentPreviewSceneName", out var preview))
                PreviewScene = preview.ValueKind == JsonValueKind.String ? preview.GetString() : null;
            PruneThumbnails();
        }
        return ObsUpdate.Change;
    }

    internal ObsUpdate ApplyPreview(string? scene) => Set(scene, PreviewScene, v => PreviewScene = v);

    internal ObsUpdate ApplyStudioMode(JsonElement data)
    {
        if (ObsProtocol.Bool(data, "studioModeEnabled") is not { } enabled || enabled == StudioMode)
            return ObsUpdate.None;
        StudioMode = enabled;
        if (!enabled)
            PreviewScene = null;
        return ObsUpdate.Change;
    }

    /// <summary>GetRecordStatus: outputActive, outputPaused, outputTimecode, outputBytes.</summary>
    internal ObsUpdate ApplyRecordStatus(JsonElement data)
    {
        var now = time.GetUtcNow();
        var old = Record;
        Record = old with
        {
            Active = ObsProtocol.Bool(data, "outputActive") ?? false,
            Paused = ObsProtocol.Bool(data, "outputPaused") ?? false,
            Timecode = ObsProtocol.ParseTimecode(ObsProtocol.Str(data, "outputTimecode")) ?? TimeSpan.Zero,
            Bytes = (long)(ObsProtocol.Num(data, "outputBytes") ?? 0),
            ObservedAt = now,
        };
        return new ObsUpdate(Visible(old) != Visible(Record));
    }

    /// <summary>GetStreamStatus: além do tempo, quadros pulados, bytes (para o kbps) e reconexão.</summary>
    internal ObsUpdate ApplyStreamStatus(JsonElement data)
    {
        var now = time.GetUtcNow();
        var old = Stream;
        var bytes = (long)(ObsProtocol.Num(data, "outputBytes") ?? 0);
        double? kbps = null;
        var seconds = (now - old.ObservedAt).TotalSeconds;
        if (old.Active && bytes >= old.Bytes && seconds is > 0.2 and < 10)
            kbps = (bytes - old.Bytes) * 8 / 1000.0 / seconds;
        var active = ObsProtocol.Bool(data, "outputActive") ?? false;
        Stream = old with
        {
            Active = active,
            Reconnecting = ObsProtocol.Bool(data, "outputReconnecting") ?? false,
            Timecode = ObsProtocol.ParseTimecode(ObsProtocol.Str(data, "outputTimecode")) ?? TimeSpan.Zero,
            Bytes = bytes,
            SkippedFrames = (long)(ObsProtocol.Num(data, "outputSkippedFrames") ?? 0),
            TotalFrames = (long)(ObsProtocol.Num(data, "outputTotalFrames") ?? 0),
            Kbps = active ? kbps ?? old.Kbps : null,
            ObservedAt = now,
        };
        if (Stream.Kbps is { } rate)
            KbpsHistory = [.. KbpsHistory.TakeLast(KbpsHistoryLength - 1), rate];
        else if (!active && KbpsHistory.Count > 0)
            KbpsHistory = [];
        return new ObsUpdate(Visible(old) != Visible(Stream) || old.Kbps != Stream.Kbps || old.SkippedFrames != Stream.SkippedFrames);
    }

    internal ObsUpdate ApplyReplayBuffer(JsonElement data)
    {
        var active = ObsProtocol.Bool(data, "outputActive") ?? false;
        if (active == ReplayBuffer)
            return ObsUpdate.None;
        ReplayBuffer = active;
        return ObsUpdate.Change;
    }

    internal ObsUpdate ApplyVirtualCam(JsonElement data)
    {
        var active = ObsProtocol.Bool(data, "outputActive") ?? false;
        if (active == VirtualCam)
            return ObsUpdate.None;
        VirtualCam = active;
        return ObsUpdate.Change;
    }

    /// <summary>Lista de fontes de áudio já filtrada (só as que responderam ao GetInputMute).</summary>
    internal ObsUpdate ApplyAudioInputs(IEnumerable<AudioInput> inputs)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, AudioInput>(StringComparer.Ordinal);
            foreach (var input in inputs)
                next[input.Name] = _inputs.TryGetValue(input.Name, out var old) ? input with { Level = old.Level } : input;
            _inputs = next;
        }
        return ObsUpdate.Change;
    }

    internal ObsUpdate ApplyThumbnail(string scene, byte[] jpeg)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, SceneThumbnail>(_thumbnails, StringComparer.Ordinal) { [scene] = new(jpeg, time.GetUtcNow()) };
            _thumbnails = next;
        }
        return ObsUpdate.Change;
    }

    // ---- eventos ----

    internal ObsUpdate ApplyEvent(string type, JsonElement data)
    {
        switch (type)
        {
            case "CurrentProgramSceneChanged":
                return Set(ObsProtocol.Str(data, "sceneName"), ProgramScene, v => ProgramScene = v);
            case "CurrentPreviewSceneChanged":
                return Set(ObsProtocol.Str(data, "sceneName"), PreviewScene, v => PreviewScene = v);
            case "SceneListChanged":
                lock (_gate)
                {
                    Scenes = ReadScenes(ObsProtocol.Arr(data, "scenes"));
                    PruneThumbnails();
                }
                return ObsUpdate.Change;
            case "SceneCreated" or "SceneRemoved" or "SceneNameChanged" or "CurrentSceneCollectionChanged":
                return new ObsUpdate(true, NeedsScenes: true, NeedsInputs: type == "CurrentSceneCollectionChanged");
            case "StudioModeStateChanged":
                return ApplyStudioMode(data);
            case "RecordStateChanged":
                return ApplyOutputEvent(data, record: true);
            case "StreamStateChanged":
                return ApplyOutputEvent(data, record: false);
            case "ReplayBufferStateChanged":
                return ApplyReplayBuffer(data);
            case "ReplayBufferSaved":
                LastReplayPath = ObsProtocol.Str(data, "savedReplayPath");
                LastReplayAt = time.GetUtcNow();
                return ObsUpdate.Change;
            case "VirtualcamStateChanged":
                return ApplyVirtualCam(data);
            case "InputMuteStateChanged":
                return UpdateInput(ObsProtocol.Str(data, "inputName"), i => ObsProtocol.Bool(data, "inputMuted") is { } m ? i with { Muted = m } : i);
            case "InputVolumeChanged":
                return UpdateInput(ObsProtocol.Str(data, "inputName"), i => ObsProtocol.Num(data, "inputVolumeDb") is { } db ? i with { VolumeDb = db } : i);
            case "InputVolumeMeters":
                return ApplyMeters(ObsProtocol.Arr(data, "inputs"));
            case "InputCreated" or "InputRemoved" or "InputNameChanged":
                return new ObsUpdate(true, NeedsInputs: true);
            default:
                return ObsUpdate.None;
        }
    }

    /// <summary>
    /// RecordStateChanged/StreamStateChanged: o estado muda na hora; o tempo vem no próximo status (pedimos já).
    /// </summary>
    private ObsUpdate ApplyOutputEvent(JsonElement data, bool record)
    {
        var state = ObsProtocol.Str(data, "outputState") ?? "";
        var active = ObsProtocol.Bool(data, "outputActive") ?? false;
        var now = time.GetUtcNow();
        var old = record ? Record : Stream;
        var next = state switch
        {
            "OBS_WEBSOCKET_OUTPUT_PAUSED" => old with { Active = true, Paused = true, Timecode = old.Elapsed(now), ObservedAt = now },
            "OBS_WEBSOCKET_OUTPUT_RESUMED" => old with { Active = true, Paused = false, ObservedAt = now },
            "OBS_WEBSOCKET_OUTPUT_RECONNECTING" => old with { Reconnecting = true },
            "OBS_WEBSOCKET_OUTPUT_RECONNECTED" => old with { Reconnecting = false },
            "OBS_WEBSOCKET_OUTPUT_STARTED" => new OutputStatus { Active = true, ObservedAt = now },
            "OBS_WEBSOCKET_OUTPUT_STOPPED" => new OutputStatus(),
            _ => old with { Active = active || old.Active },
        };
        if (record)
            Record = next;
        else
            Stream = next;
        return new ObsUpdate(true, NeedsOutputs: true);
    }

    /// <summary>
    /// InputVolumeMeters: para cada fonte, <c>inputLevelsMul</c> = canais x [magnitude, pico, pico da entrada].
    /// O medidor usa o maior pico, em escala de dB (-60 dB = vazio, 0 dB = cheio), como o mixer do OBS.
    /// </summary>
    private ObsUpdate ApplyMeters(JsonElement inputs)
    {
        if (inputs.ValueKind != JsonValueKind.Array)
            return ObsUpdate.None;
        var changed = false;
        lock (_gate)
        {
            Dictionary<string, AudioInput>? next = null;
            foreach (var item in inputs.EnumerateArray())
            {
                if (ObsProtocol.Str(item, "inputName") is not { } name || !_inputs.TryGetValue(name, out var input))
                    continue;
                var peak = 0.0;
                foreach (var channel in ObsProtocol.Arr(item, "inputLevelsMul").EnumerateArrayOrEmpty())
                {
                    if (channel.ValueKind == JsonValueKind.Array && channel.GetArrayLength() >= 2 && channel[1].ValueKind == JsonValueKind.Number)
                        peak = Math.Max(peak, channel[1].GetDouble());
                }
                var level = MeterLevel(peak);
                if (Math.Abs(level - input.Level) < 0.005)
                    continue;
                next ??= new Dictionary<string, AudioInput>(_inputs, StringComparer.Ordinal);
                next[name] = input with { Level = level };
            }
            if (next is not null)
            {
                _inputs = next;
                changed = true;
            }
        }
        return new ObsUpdate(changed);
    }

    /// <summary>Multiplicador linear (0..1) para a posição no medidor: -60 dB a 0 dB.</summary>
    internal static double MeterLevel(double mul)
    {
        if (mul <= 0)
            return 0;
        var db = 20 * Math.Log10(mul);
        return Math.Clamp((db + 60) / 60, 0, 1);
    }

    private ObsUpdate UpdateInput(string? name, Func<AudioInput, AudioInput> change)
    {
        if (name is null)
            return ObsUpdate.None;
        lock (_gate)
        {
            if (!_inputs.TryGetValue(name, out var input))
                return ObsUpdate.None;
            var updated = change(input);
            if (updated == input)
                return ObsUpdate.None;
            _inputs = new Dictionary<string, AudioInput>(_inputs, StringComparer.Ordinal) { [name] = updated };
        }
        return ObsUpdate.Change;
    }

    private static ObsUpdate Set(string? value, string? current, Action<string?> apply)
    {
        if (value == current)
            return ObsUpdate.None;
        apply(value);
        return ObsUpdate.Change;
    }

    private static string[] ReadScenes(JsonElement scenes) =>
        [.. scenes.EnumerateArrayOrEmpty()
            .Select(s => (Name: ObsProtocol.Str(s, "sceneName"), Index: ObsProtocol.Num(s, "sceneIndex") ?? 0))
            .Where(s => s.Name is not null)
            .OrderByDescending(s => s.Index)
            .Select(s => s.Name!)];

    private void PruneThumbnails()
    {
        if (_thumbnails.Keys.All(Scenes.Contains))
            return;
        _thumbnails = _thumbnails.Where(t => Scenes.Contains(t.Key)).ToDictionary(StringComparer.Ordinal);
    }

    /// <summary>O que muda o desenho do tile (o tempo anda sozinho a partir da leitura).</summary>
    private static (bool, bool, bool) Visible(OutputStatus s) => (s.Active, s.Paused, s.Reconnecting);
}

internal static class JsonElementExtensions
{
    public static IEnumerable<JsonElement> EnumerateArrayOrEmpty(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
}

internal static class TimeSpanExtensions
{
    public static TimeSpan Max(this TimeSpan a, TimeSpan b) => a > b ? a : b;
}
