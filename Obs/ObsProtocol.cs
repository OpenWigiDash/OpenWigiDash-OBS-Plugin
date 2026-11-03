using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenWigiDash.ObsPlugin.Obs;

/// <summary>
/// Mensagens do obs-websocket v5 (o que vem dentro do OBS 28 em diante). Só montagem e leitura, sem rede.
/// Referência: https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md
/// </summary>
internal static class ObsProtocol
{
    public const int RpcVersion = 1;

    // OpCodes
    public const int Hello = 0;
    public const int Identify = 1;
    public const int Identified = 2;
    public const int Reidentify = 3;
    public const int Event = 5;
    public const int Request = 6;
    public const int RequestResponse = 7;

    // Códigos de fechamento que importam para a tela.
    public const int CloseAuthenticationFailed = 4009;
    public const int CloseUnsupportedRpcVersion = 4010;

    /// <summary>Assinaturas de eventos (EventSubscription). <see cref="InputVolumeMeters"/> é de alto volume e vai à parte.</summary>
    [Flags]
    public enum Events
    {
        None = 0,
        General = 1 << 0,
        Config = 1 << 1,
        Scenes = 1 << 2,
        Inputs = 1 << 3,
        Transitions = 1 << 4,
        Filters = 1 << 5,
        Outputs = 1 << 6,
        SceneItems = 1 << 7,
        MediaInputs = 1 << 8,
        Vendors = 1 << 9,
        Ui = 1 << 10,
        All = General | Config | Scenes | Inputs | Transitions | Filters | Outputs | SceneItems | MediaInputs | Vendors | Ui,
        InputVolumeMeters = 1 << 16,
    }

    /// <summary>
    /// Autenticação do v5: <c>base64(sha256(base64(sha256(senha + salt)) + challenge))</c>.
    /// </summary>
    public static string Authentication(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public static string IdentifyMessage(string? authentication, Events subscriptions)
    {
        var d = new JsonObject { ["rpcVersion"] = RpcVersion, ["eventSubscriptions"] = (int)subscriptions };
        if (authentication is not null)
            d["authentication"] = authentication;
        return new JsonObject { ["op"] = Identify, ["d"] = d }.ToJsonString();
    }

    public static string ReidentifyMessage(Events subscriptions) =>
        new JsonObject { ["op"] = Reidentify, ["d"] = new JsonObject { ["eventSubscriptions"] = (int)subscriptions } }.ToJsonString();

    public static string RequestMessage(string requestType, string requestId, JsonObject? data)
    {
        var d = new JsonObject { ["requestType"] = requestType, ["requestId"] = requestId };
        if (data is not null)
            d["requestData"] = data;
        return new JsonObject { ["op"] = Request, ["d"] = d }.ToJsonString();
    }

    /// <summary>Lê o envelope <c>{op, d}</c>; <c>null</c> se não for JSON ou não tiver op.</summary>
    public static ObsMessage? Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.Number)
                return null;
            var d = root.TryGetProperty("d", out var data) && data.ValueKind == JsonValueKind.Object ? data.Clone() : default;
            return new ObsMessage(op.GetInt32(), d);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Hello: o desafio de senha, se o servidor pedir.</summary>
    public static (string? Salt, string? Challenge, int RpcVersion) ReadHello(JsonElement d)
    {
        var rpc = d.TryGetProperty("rpcVersion", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
        if (d.TryGetProperty("authentication", out var auth) && auth.ValueKind == JsonValueKind.Object)
            return (Str(auth, "salt"), Str(auth, "challenge"), rpc);
        return (null, null, rpc);
    }

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    public static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    public static JsonElement Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v : default;

    /// <summary>Timecode do OBS ("01:02:03.456") em tempo.</summary>
    public static TimeSpan? ParseTimecode(string? text) =>
        TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>Um envelope do obs-websocket: opcode e o objeto <c>d</c> (default se não veio).</summary>
internal readonly record struct ObsMessage(int Op, JsonElement Data);

/// <summary>Resposta de um pedido: <c>requestStatus.result</c>, o código, o comentário e o <c>responseData</c>.</summary>
public sealed record ObsResponse(bool Ok, int Code, string? Comment, JsonElement Data)
{
    internal static ObsResponse From(JsonElement d)
    {
        var status = ObsProtocol.Obj(d, "requestStatus");
        return new ObsResponse(ObsProtocol.Bool(status, "result") ?? false, (int)(ObsProtocol.Num(status, "code") ?? 0),
            ObsProtocol.Str(status, "comment"), ObsProtocol.Obj(d, "responseData").ValueKind == JsonValueKind.Object
                ? ObsProtocol.Obj(d, "responseData").Clone() : default);
    }
}
