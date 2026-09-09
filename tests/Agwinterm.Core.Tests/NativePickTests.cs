using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class NativePickTests
{
    private static PickSpec Parse(string json) { using var doc = JsonDocument.Parse(json); return PickSpec.Parse(doc.RootElement); }
    private static PickSpec Spec(params PickItem[] items) => new(items, null, null, true);

    [Theory]
    [InlineData("{}")] [InlineData("[]")] [InlineData("{\"items\":[]}")]
    [InlineData("{\"items\":[{}]}")] [InlineData("{\"items\":[null]}")]
    [InlineData("{\"items\":[{\"id\":1,\"label\":\"x\"}]}")]
    [InlineData("{\"items\":[{\"id\":\"a\",\"label\":\"\"}]}")]
    [InlineData("{\"items\":[{\"id\":\"a\",\"label\":\"x\\ny\"}]}")]
    [InlineData("{\"items\":[{\"id\":\"a\",\"label\":\"x\",\"subtitle\":\"\\u007f\"}]}")]
    [InlineData("{\"items\":[],\"allowCustom\":\"true\"}")]
    [InlineData("{\"items\":[],\"allowCustom\":true,\"query\":\"\\t\"}")]
    [InlineData("{\"items\":[],\"allowCustom\":true,\"prompt\":false}")]
    public void InvalidInputRefuses(string json) => Assert.Throws<ArgumentException>(() => Parse(json));

    [Fact] public void EmptyIdsUnicodeAndDuplicateLabelsAreLegalButDuplicateIdsAreNot()
    {
        var spec = Parse("{\"items\":[{\"id\":\"\",\"label\":\"中文 🦊\"},{\"id\":\"A\",\"label\":\"中文 🦊\"},{\"id\":\"a\",\"label\":\"same\"}]}");
        Assert.Equal(3, spec.Items.Count); Assert.Equal("", spec.Items[0].Id);
        Assert.Throws<ArgumentException>(() => Parse("{\"items\":[{\"id\":\"a\",\"label\":\"x\"},{\"id\":\"a\",\"label\":\"y\"}]}"));
    }

    [Fact] public void InputCapsApplyBeforeOpening()
    {
        string Wire(int count, int length) => JsonSerializer.Serialize(new { items = Enumerable.Range(0,count).Select(i => new { id=i.ToString(), label=new string('x',length) }) });
        Assert.Equal(1000, Parse(Wire(1000,1)).Items.Count);
        Assert.Throws<ArgumentException>(() => Parse(Wire(1001,1)));
        Assert.Equal(4096, Parse(Wire(1,4096)).Items[0].Label.Length);
        Assert.Throws<ArgumentException>(() => Parse(Wire(1,4097)));
        Assert.Throws<ArgumentException>(() => Parse(Wire(300,4096)));
    }

    [Fact] public void FilteringUsesLabelsOnlyAndPreservesOriginalIndex()
    {
        var model = new PickSelection(Spec(new("z","Zulu","danger"),new("a","Alpha"),new("b","Alpine")));
        Assert.Equal(new[]{0,1,2},model.Matches);
        model.SetQuery(" AL "); Assert.Equal(new[]{1,2},model.Matches);
        model.Move(500); Assert.Equal(new PickOutcome("picked","b","Alpine",2), model.Choose());
        model.Move(-500); Assert.Equal(0,model.Selected);
        model.SetQuery("danger"); Assert.Empty(model.Matches); Assert.True(model.Custom);
        Assert.Equal(new PickOutcome("custom",Query:"danger"),model.Choose());
        model.SetQuery("   "); Assert.False(model.Custom); Assert.Equal(new[]{0,1,2},model.Matches);
    }

    [Fact] public void CustomRequiresNonblankNoMatchesAndOptIn()
    {
        var spec = Spec(new PickItem("a","alpha")); var model = new PickSelection(spec);
        model.SetQuery("  custom  "); Assert.Equal("custom",model.Choose()!.Query);
        model.SetQuery("alpha"); Assert.False(model.Custom);
        model = new PickSelection(spec with {AllowCustom=false}); model.SetQuery("xyz");
        Assert.Equal(0,model.Count); Assert.Null(model.Choose());
    }

    [Theory][InlineData("al","Alpha",0)][InlineData("ph","Alpha",7)][InlineData("aa","Alpha",43)]
    [InlineData("zz","Alpha",null)][InlineData("AL PH","Alpha",7)]
    public void FuzzyTermsHaveStableScores(string query,string label,int? score) => Assert.Equal(score,PickSelection.Score(query,label));

    [Fact] public void PendingIsPerWindowAndTerminalResultsAreImmutable()
    {
        var registry = new PickRegistry(); var spec = Spec();
        var a = registry.Open("a",spec)!; var b=registry.Open("b",spec)!;
        Assert.Null(registry.Open("a",spec)); Assert.Equal("pending",registry.Find(a.Id)!.Outcome.Result);
        Assert.True(registry.Resolve(a.Id,new("custom",Query:"one")));
        Assert.False(registry.Resolve(a.Id,new("cancelled"))); Assert.Equal("custom",registry.Find(a.Id)!.Outcome.Result);
        registry.CloseWindow("b"); Assert.Equal("cancelled",registry.Find(b.Id)!.Outcome.Result);
        Assert.Equal("b",registry.Find(b.Id)!.Window); Assert.Null(registry.Find("active"));
    }

    [Fact] public void LiveResultsRetainEightAndClosedResultsRetainNewestThirtyTwoByAnswerTime()
    {
        var registry=new PickRegistry(); var ids=new List<string>();
        for(int n=0;n<40;n++) { var p=registry.Open("w"+(n/8),Spec())!; ids.Add(p.Id); registry.Resolve(p.Id,new("cancelled")); }
        // Reverse closure order must not replace the newer answers with older ones.
        for(int n=4;n>=0;n--) registry.CloseWindow("w"+n);
        Assert.All(ids.Take(8),id=>Assert.Null(registry.Find(id)));
        Assert.All(ids.Skip(8),id=>Assert.NotNull(registry.Find(id)));
        var live=new List<string>();
        for(int n=0;n<9;n++) { var p=registry.Open("live",Spec())!; live.Add(p.Id); registry.Resolve(p.Id,new("cancelled")); }
        Assert.Null(registry.Find(live[0])); Assert.All(live.Skip(1),id=>Assert.NotNull(registry.Find(id)));
    }
}
