using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Pty.Tests;

public class PickDispatchTests
{
    private sealed class Host : IWindowHost, IPickHost
    {
        public int Calls; public string? Window; public bool Follow; public PickSpec? Spec;
        public string OpenPick(PickSpec spec,string? window,bool follow) {Calls++;Spec=spec;Window=window;Follow=follow;return "pick-id";}
        public PickOutcome ReadPick(string id,string? window) {Calls++;Window=window;return new("pending");}
        public void CancelPick(string id,string? window) {Calls++;Window=window;}
        public ISessionHost? ResolveWindow(string? selector) => throw new Exception("picker must not resolve a session host");
        public IReadOnlyList<WindowSnapshot> Windows()=>[];
        public string WindowNew(string? n)=>throw new NotSupportedException();
        public bool WindowSelect(string? s)=>false; public bool WindowClose(string? s)=>false;
        public bool WindowDelete(string? s)=>false; public bool WindowRename(string? s,string n)=>false;
        public bool WindowResize(string? s,int w,int h)=>false; public bool WindowMove(string? s,int x,int y)=>false; public bool WindowZoom(string? s)=>false;
    }
    private static JsonElement Call(Host host,string raw)
    { using var server=new ControlServer(new FakeSessionHost(),host);using var doc=JsonDocument.Parse(server.Dispatch(raw));return doc.RootElement.Clone(); }

    [Fact] public void PickerRoutesBeforeSessionResolutionAndPreservesTypedOptions()
    {
        var host=new Host();var result=Call(host,"{\"cmd\":\"pick.open\",\"window\":\"w1\",\"args\":{\"items\":[{\"id\":\"\",\"label\":\"A\"}],\"follow\":true}}");
        Assert.True(result.GetProperty("ok").GetBoolean());Assert.Equal("pick-id",result.GetProperty("result").GetProperty("id").GetString());
        Assert.Equal("w1",host.Window);Assert.True(host.Follow);Assert.Single(host.Spec!.Items);
        result=Call(host,"{\"cmd\":\"pick.result\",\"target\":\"pick-id\"}");
        Assert.Equal("pending",result.GetProperty("result").GetProperty("pick").GetProperty("result").GetString());Assert.Null(host.Window);
        Assert.True(Call(host,"{\"cmd\":\"pick.cancel\",\"target\":\"pick-id\"}").GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("{\"cmd\":\"pick.result\"}")][InlineData("{\"cmd\":\"pick.cancel\",\"target\":3}")]
    [InlineData("{\"cmd\":\"pick.result\",\"target\":\"id\",\"window\":\"\"}")]
    [InlineData("{\"cmd\":\"pick.open\",\"target\":\"active\",\"args\":{\"items\":[],\"allowCustom\":true}}")]
    [InlineData("{\"cmd\":\"pick.open\",\"window\":false,\"args\":{\"items\":[],\"allowCustom\":true}}")]
    [InlineData("{\"cmd\":\"pick.open\",\"args\":{\"items\":[],\"allowCustom\":true,\"follow\":\"true\"}}")]
    public void InvalidRequestNeverCallsHost(string raw)
    {var host=new Host();Assert.False(Call(host,raw).GetProperty("ok").GetBoolean());Assert.Equal(0,host.Calls);}
}
