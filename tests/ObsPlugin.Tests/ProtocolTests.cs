using System.Text.Json;
using System.Text.Json.Nodes;
using OpenWigiDash.ObsPlugin.Obs;

namespace ObsPlugin.Tests;

public class ProtocolTests
{
    [Fact]
    public void Authentication_MatchesTheObsWebsocketFormula()
    {
        // Valor calculado à parte (Python: hashlib/base64) para senha "supersecretpassword",
        // salt "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=" e challenge "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=".
        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=",
            ObsProtocol.Authentication("supersecretpassword", "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=", "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY="));
    }

    [Fact]
    public void Identify_CarriesRpcSubscriptionsAndAuth()
    {
        var withAuth = JsonNode.Parse(ObsProtocol.IdentifyMessage("abc", ObsProtocol.Events.All))!;
        Assert.Equal(1, (int)withAuth["op"]!);
        Assert.Equal(1, (int)withAuth["d"]!["rpcVersion"]!);
        Assert.Equal(2047, (int)withAuth["d"]!["eventSubscriptions"]!);
        Assert.Equal("abc", (string)withAuth["d"]!["authentication"]!);

        var noAuth = JsonNode.Parse(ObsProtocol.IdentifyMessage(null, ObsProtocol.Events.All | ObsProtocol.Events.InputVolumeMeters))!;
        Assert.Null(noAuth["d"]!["authentication"]);
        Assert.Equal(2047 | 65536, (int)noAuth["d"]!["eventSubscriptions"]!);
    }

    [Fact]
    public void Request_PutsDataOnlyWhenGiven()
    {
        var plain = JsonNode.Parse(ObsProtocol.RequestMessage("GetSceneList", "7", null))!;
        Assert.Equal(6, (int)plain["op"]!);
        Assert.Equal("GetSceneList", (string)plain["d"]!["requestType"]!);
        Assert.Equal("7", (string)plain["d"]!["requestId"]!);
        Assert.Null(plain["d"]!["requestData"]);

        var withData = JsonNode.Parse(ObsProtocol.RequestMessage("SetInputMute", "8", new JsonObject { ["inputName"] = "Mic" }))!;
        Assert.Equal("Mic", (string)withData["d"]!["requestData"]!["inputName"]!);
    }

    [Fact]
    public void Hello_WithAndWithoutPassword()
    {
        var open = ObsProtocol.Parse("""{"op":0,"d":{"obsWebSocketVersion":"5.5.4","rpcVersion":1}}""")!.Value;
        Assert.Equal(ObsProtocol.Hello, open.Op);
        Assert.Equal((null, null, 1), ObsProtocol.ReadHello(open.Data));

        var locked = ObsProtocol.Parse("""{"op":0,"d":{"rpcVersion":1,"authentication":{"salt":"s","challenge":"c"}}}""")!.Value;
        Assert.Equal(("s", "c", 1), ObsProtocol.ReadHello(locked.Data));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"d":{}}""")]
    [InlineData("""{"op":"5"}""")]
    public void Parse_IgnoresGarbage(string text) => Assert.Null(ObsProtocol.Parse(text));

    [Fact]
    public void Response_ReadsStatusAndData()
    {
        var ok = ObsResponse.From(Wait.Json("""{"requestStatus":{"result":true,"code":100},"responseData":{"x":1}}"""));
        Assert.True(ok.Ok);
        Assert.Equal(100, ok.Code);
        Assert.Equal(1, ok.Data.GetProperty("x").GetInt32());

        var failed = ObsResponse.From(Wait.Json("""{"requestStatus":{"result":false,"code":702,"comment":"Não"}}"""));
        Assert.False(failed.Ok);
        Assert.Equal(702, failed.Code);
        Assert.Equal("Não", failed.Comment);
        Assert.Equal(JsonValueKind.Undefined, failed.Data.ValueKind);
    }

    [Theory]
    [InlineData("00:00:00.000", 0)]
    [InlineData("01:02:03.500", 3723.5)]
    public void Timecode(string text, double seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), ObsProtocol.ParseTimecode(text));

    [Fact]
    public void DataUri_Decodes()
    {
        Assert.Equal([1, 2, 3], OpenWigiDash.ObsPlugin.Obs.ObsSession.DecodeDataUri("data:image/jpg;base64,AQID"));
        Assert.Null(OpenWigiDash.ObsPlugin.Obs.ObsSession.DecodeDataUri("data:image/jpg;base64,@@@"));
        Assert.Null(OpenWigiDash.ObsPlugin.Obs.ObsSession.DecodeDataUri(null));
    }
}
