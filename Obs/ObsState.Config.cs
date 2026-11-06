using System.Text.Json;

namespace OpenWigiDash.ObsPlugin.Obs;

/// <summary>
/// Fase 3: coleções de cenas e perfis (categoria Config do obs-websocket). A lista e a atual vêm do
/// GetSceneCollectionList/GetProfileList; os eventos "Changing"/"Changed" marcam a troca em andamento, mesmo quando
/// ela é feita pela tela do OBS.
/// </summary>
public sealed partial class ObsState
{
    public IReadOnlyList<string> SceneCollections { get; private set; } = [];
    public string? CurrentSceneCollection { get; private set; }
    /// <summary>Entre o CurrentSceneCollectionChanging e o Changed: o OBS está recarregando cenas e fontes.</summary>
    public bool SwitchingSceneCollection { get; private set; }

    public IReadOnlyList<string> Profiles { get; private set; } = [];
    public string? CurrentProfile { get; private set; }
    public bool SwitchingProfile { get; private set; }

    /// <summary>Live, gravação, replay ou câmera virtual ligados: o OBS não deixa trocar de perfil assim.</summary>
    public bool OutputsActive => Stream.Active || Record.Active || ReplayBuffer || VirtualCam;

    private void ResetConfig()
    {
        SceneCollections = [];
        Profiles = [];
        CurrentSceneCollection = CurrentProfile = null;
        SwitchingSceneCollection = SwitchingProfile = false;
    }

    /// <summary>GetSceneCollectionList: currentSceneCollectionName e sceneCollections (lista de nomes).</summary>
    internal ObsUpdate ApplySceneCollectionList(JsonElement data)
    {
        SceneCollections = ReadNames(ObsProtocol.Arr(data, "sceneCollections"));
        CurrentSceneCollection = ObsProtocol.Str(data, "currentSceneCollectionName") ?? CurrentSceneCollection;
        return ObsUpdate.Change;
    }

    /// <summary>GetProfileList: currentProfileName e profiles (lista de nomes).</summary>
    internal ObsUpdate ApplyProfileList(JsonElement data)
    {
        Profiles = ReadNames(ObsProtocol.Arr(data, "profiles"));
        CurrentProfile = ObsProtocol.Str(data, "currentProfileName") ?? CurrentProfile;
        return ObsUpdate.Change;
    }

    private ObsUpdate ApplyConfigEvent(string type, JsonElement data)
    {
        switch (type)
        {
            case "CurrentSceneCollectionChanging":
                SwitchingSceneCollection = true;
                return ObsUpdate.Change;
            case "CurrentSceneCollectionChanged":
                SwitchingSceneCollection = false;
                CurrentSceneCollection = ObsProtocol.Str(data, "sceneCollectionName") ?? CurrentSceneCollection;
                // Coleção nova: cenas e fontes são outras.
                return new ObsUpdate(true, NeedsScenes: true, NeedsInputs: true);
            case "SceneCollectionListChanged":
                SceneCollections = ReadNames(ObsProtocol.Arr(data, "sceneCollections"));
                return ObsUpdate.Change;
            case "CurrentProfileChanging":
                SwitchingProfile = true;
                return ObsUpdate.Change;
            case "CurrentProfileChanged":
                SwitchingProfile = false;
                CurrentProfile = ObsProtocol.Str(data, "profileName") ?? CurrentProfile;
                // O perfil traz as configurações de saída: relê o estado da live e da gravação.
                return new ObsUpdate(true, NeedsOutputs: true);
            case "ProfileListChanged":
                Profiles = ReadNames(ObsProtocol.Arr(data, "profiles"));
                return ObsUpdate.Change;
            default:
                return ApplyMoreEvent(type, data);
        }
    }

    private static string[] ReadNames(JsonElement names) =>
        [.. names.EnumerateArrayOrEmpty().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!)];
}
