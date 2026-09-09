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

    [Fact] public void RepeatedTermsKeepRankingButDoNotMultiplyMatchingWork()
    {
        var items=Enumerable.Range(0,240).Select(i=>new PickItem(i.ToString(),"ab"+new string('x',4092)+"a")).ToArray();
        string query=string.Join(' ',Enumerable.Repeat("aa",1365));
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var model=new PickSelection(new(items,null,query,false));
        Assert.Equal(240,model.Count);Assert.True(watch.Elapsed<TimeSpan.FromSeconds(3));
        Assert.Equal(PickSelection.Score("aa",items[0].Label)*1365,PickSelection.Score(query,items[0].Label));
    }

    [Fact] public void AggregateWorkRejectsBeforeChangingSelection()
    {
        var items=Enumerable.Range(0,240).Select(i=>new PickItem(i.ToString(),new string('a',4096))).ToArray();
        var model=new PickSelection(new(items,null,"a",false));
        string query=string.Join(' ',Enumerable.Range(0,70).Select(i=>"term"+i));
        Assert.Throws<ArgumentException>(()=>model.SetQuery(query));Assert.Equal("a",model.Query);Assert.Equal(240,model.Count);
    }

    [Fact] public void AbortedConstructionDoesNotEvictAnswers()
    {
        var registry=new PickRegistry();var ids=new List<string>();
        for(int i=0;i<8;i++){var p=registry.Open("w",Spec())!;ids.Add(p.Id);registry.Resolve(p.Id,new("cancelled"));}
        var aborted=registry.Open("w",Spec())!;registry.Abort(aborted.Id);
        Assert.False(registry.Resolve(aborted.Id,new("cancelled")));Assert.Null(registry.Find(aborted.Id));
        Assert.All(ids,id=>Assert.NotNull(registry.Find(id)));
    }

    [Fact] public async Task WithdrawnQueuedOpenNeverRunsAndInFlightOpenRollsBack()
    {
        var before=new PickOpenCall<string>();Assert.True(before.Withdraw());
        before.Run(()=>throw new Exception("must not run"),_=>Assert.Fail("nothing to roll back"));
        Assert.True(before.Task.IsCanceled);
        var during=new PickOpenCall<string>();var rolledBack=new List<string>();
        during.Run(()=>{Assert.True(during.Withdraw());return "created";},rolledBack.Add);
        Assert.True(during.Task.IsCanceled);Assert.Equal("created",Assert.Single(rolledBack));
        var completed=new PickOpenCall<string>();completed.Run(()=>"published",_=>Assert.Fail("published result retained"));
        Assert.False(completed.Withdraw());Assert.Equal("published",await completed.Task);
    }

    [Fact] public void PickerWindowsRequireExactOrUniquePrefix()
    {
        string[] ids=["abc-one","abc-two","z-last"];
        Assert.Null(PickWindowSelector.Resolve("abc",ids,"abc-one"));
        Assert.Equal("abc-two",PickWindowSelector.Resolve("abc-two",ids,"abc-one"));
        Assert.Equal("z-last",PickWindowSelector.Resolve("z",ids,"abc-one"));
        Assert.Equal("abc-one",PickWindowSelector.Resolve(null,ids,"abc-one"));
        Assert.Null(PickWindowSelector.Resolve("",ids,"abc-one"));
    }

    [Fact] public void InputPolicySeparatesDropCleanupFromKeyboardAndFocus()
    {
        Assert.Equal(PickInputRoute.Drop,PickInputPolicy.Route(0x233));
        Assert.Equal(PickInputRoute.Ignore,PickInputPolicy.Route(0x200));
        foreach(uint message in new uint[]{0x100,0x101,0x102,0x104,0x105,0x106})
            Assert.Equal(PickInputRoute.Keyboard,PickInputPolicy.Route(message));
        foreach(uint message in new uint[]{7,0x201,0x202,0x203,0x204,0x205,0x207,0x208,0x20a,0x7b})
            Assert.Equal(PickInputRoute.Focus,PickInputPolicy.Route(message));
        foreach(uint message in new uint[]{0xf,0x10,0x82}) Assert.Equal(PickInputRoute.None,PickInputPolicy.Route(message));
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
