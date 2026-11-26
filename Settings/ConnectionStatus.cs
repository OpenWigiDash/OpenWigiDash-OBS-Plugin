using MudBlazor;
using OpenWigiDash.ObsPlugin.Obs;

namespace OpenWigiDash.ObsPlugin.Settings;

/// <summary>Estado da conexão em texto e cor, igual na página do plugin e no aviso da tela de cada widget.</summary>
internal static class ConnectionStatus
{
    public static Severity StateSeverity(ObsSession session) => session.ConnectionState switch
    {
        ObsConnectionState.Ready => Severity.Success,
        ObsConnectionState.Connecting => Severity.Info,
        ObsConnectionState.Unavailable => Severity.Warning,
        _ => Severity.Error,
    };

    /// <param name="name">Nome da conexão, quando há mais de uma e vale dizer qual.</param>
    public static string Text(ObsSession session, string? name = null)
    {
        var where = name is null ? session.Address : $"{name} ({session.Address})";
        return session.ConnectionState switch
        {
            ObsConnectionState.Ready => $"Conectado ao OBS{(session.State.ObsVersion is { } v ? " " + v : "")} em {where}.",
            ObsConnectionState.Unavailable when session.IsLocal =>
                "O OBS não está aberto (ou o servidor WebSocket está desligado). Os tiles voltam sozinhos quando ele abrir.",
            // Em outro PC, nada responder pode ser o PC desligado, o OBS fechado ou o firewall de lá.
            ObsConnectionState.Unavailable =>
                $"Sem resposta em {where}. Confira se o outro PC está ligado, com o OBS aberto, o servidor WebSocket ativo e a porta liberada no firewall dele. Os tiles voltam sozinhos quando ele responder.",
            ObsConnectionState.NeedsPassword => "O OBS pede a senha do servidor WebSocket.",
            ObsConnectionState.AuthFailed => "O OBS recusou a senha. Confira em Mostrar informações de conexão.",
            ObsConnectionState.Unsupported => "Este OBS é antigo demais. Precisa do OBS 28 ou mais novo (obs-websocket 5).",
            _ => "Conectando ao OBS…",
        };
    }
}
