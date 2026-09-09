using System.Text;
using System.Text.Json;
using Agwinterm.Ctl;

namespace Agwinterm.Pty.Tests;

public class PickCliTests
{
    private static JsonElement Json(string raw) { using var doc=JsonDocument.Parse(raw); return doc.RootElement.Clone(); }
    private static JsonElement Reply(string state) => Json("{\"ok\":true,\"result\":{\"pick\":"+state+"}}");
    private static readonly JsonElement Opened=Json("{\"ok\":true,\"result\":{\"id\":\"picker-1\"}}");

    [Theory][InlineData("pending",1)][InlineData("cancelled",2)]
    public void OneShotResultIsPayloadAndHasHonestExit(string state,int code)
    {
        var output=new List<string>();
        Assert.Equal(code,PickCli.Execute(["pick","result","picker-1","--json"],[],_=>Reply("{\"result\":\""+state+"\"}"),_=>Assert.Fail("no wait"),output.Add,Assert.Fail));
        Assert.Equal(state,Json(Assert.Single(output)).GetProperty("result").GetString());
    }

    [Fact] public void BlockingWaitPinsOnlyIdAndUsesTwoPhaseBackoff()
    {
        var requests=new List<JsonElement>(); var delays=new List<int>(); var output=new List<string>();
        int SendCount=0;
        JsonElement Send(string raw) {
            var request=Json(raw);requests.Add(request);
            return SendCount++ switch { 0=>Opened, <=12=>Reply("{\"result\":\"pending\"}"), _=>Reply("{\"result\":\"picked\",\"id\":\"\",\"label\":\"A\",\"index\":0}") };
        }
        Assert.Equal(0,PickCli.Execute(["pick","--window","active","--follow"],Encoding.UTF8.GetBytes("A\n"),Send,delays.Add,output.Add,Assert.Fail));
        Assert.Equal("active",requests[0].GetProperty("window").GetString());
        Assert.True(requests[0].GetProperty("args").GetProperty("follow").GetBoolean());
        Assert.All(requests.Skip(1),r=>{Assert.False(r.TryGetProperty("window",out _));Assert.Equal("picker-1",r.GetProperty("target").GetString());});
        Assert.Equal(Enumerable.Repeat(100,10).Concat(new[]{500,500}),delays);
        Assert.Equal("",Json(Assert.Single(output)).GetProperty("id").GetString());
    }

    [Fact] public void NoBlockTransfersPendingOwnershipWithoutPollingOrCancelling()
    {
        int count=0; var output=new List<string>();
        Assert.Equal(0,PickCli.Execute(["pick","open","--no-block"],Encoding.UTF8.GetBytes("A"),_=>{count++;return Opened;},_=>Assert.Fail("wait"),output.Add,Assert.Fail));
        Assert.Equal(1,count);Assert.Equal("picker-1",Json(Assert.Single(output)).GetProperty("id").GetString());
    }

    [Theory][InlineData("{\"result\":\"picked\"}")][InlineData("{\"result\":\"custom\",\"query\":\" \"}")]
    [InlineData("{\"result\":\"unknown\"}")]
    public void MalformedTerminalReplyFailsAndBestEffortCancels(string result)
    {
        var requests=new List<JsonElement>();var errors=new List<string>();
        int code=PickCli.Execute(["pick"],Encoding.UTF8.GetBytes("A"),raw=>{
            requests.Add(Json(raw)); return requests.Count==1?Opened:Reply(result);
        },_=>{},_=>Assert.Fail("no successful output"),errors.Add);
        Assert.Equal(1,code); Assert.Single(errors);
        Assert.Equal("pick.cancel",requests[^1].GetProperty("cmd").GetString());
        Assert.False(requests[^1].TryGetProperty("window",out _));
    }

    [Fact] public void CancellationCleansUpOwnedPendingRequest()
    {
        using var stop=new CancellationTokenSource();var commands=new List<string>();
        int code=PickCli.Execute(["pick"],Encoding.UTF8.GetBytes("A"),raw=>{
            commands.Add(Json(raw).GetProperty("cmd").GetString()!);return commands.Count==1?Opened:Reply("{\"result\":\"pending\"}");
        },_=>stop.Cancel(),_=>{},_=>{},stop.Token);
        Assert.Equal(1,code);Assert.Equal(new[]{"pick.open","pick.result","pick.cancel"},commands);
    }

    [Theory][InlineData("--target")][InlineData("--unknown")][InlineData("--follow=true")]
    public void UnknownFlagsFailBeforeConnecting(string flag) => Assert.Equal(2,PickCli.Execute(["pick",flag],[],_=>throw new Exception("must not connect"),_=>{},_=>{},_=>{}));

    [Fact] public void Utf8LinesPreserveTextAndRejectDuplicatesAndControlsBeforeConnecting()
    {
        Assert.Equal(new[]{" A ","中文 🦊"},PickCli.ParseItems(Encoding.UTF8.GetBytes(" A \r\n \n中文 🦊\n")).Select(i=>i.Label));
        Assert.Throws<DecoderFallbackException>(()=>PickCli.ParseItems([0xff]));
        foreach(string input in new[]{"A\nA", "A\tB", ""})
            Assert.Equal(2,PickCli.Execute(["pick"],Encoding.UTF8.GetBytes(input),_=>throw new Exception("must not connect"),_=>{},_=>{},_=>{}));
    }
}
