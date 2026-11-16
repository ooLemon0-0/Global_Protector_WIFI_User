using System;
using System.IO;
using System.Threading;
using GlobalProtectorWiFi;
class UpdateTests {
    static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    static string Release(string tag,bool preview=false,bool draft=false,bool executable=true){return "{\"tag_name\":\""+tag+"\",\"draft\":"+draft.ToString().ToLowerInvariant()+",\"prerelease\":"+preview.ToString().ToLowerInvariant()+",\"html_url\":\"https://untrusted.example/ignored\",\"assets\":[{\"name\":\""+(executable?"GlobalProtectorWiFi.exe":"source.zip")+"\",\"size\":12345}]}";}
    static int Main(){
        Version parsed;
        Assert(UpdateChecker.TryParseVersion("v1.10.0",out parsed) && parsed==new Version(1,10,0,0),"Semantic tag normalization failed");
        foreach(string bad in new[]{"v1.0.0-beta","v1.0","v1.0.0.1","v01.0.0","v1.0.0/../../other","https://untrusted.example","v999999999999.0.0","v1.0.0\n"})Assert(!UpdateChecker.TryParseVersion(bad,out parsed),"Invalid version tag accepted");
        var current=new Version(1,0,0,0);
        Assert(UpdateChecker.Evaluate(Release("v1.0.0"),current).Status==UpdateStatus.UpToDate,"Equal version prompted an update");
        Assert(UpdateChecker.Evaluate(Release("v0.3.2"),current).Status==UpdateStatus.UpToDate,"Older release prompted a downgrade");
        var update=UpdateChecker.Evaluate(Release("v1.10.0"),new Version(1,9,0));
        Assert(update.Status==UpdateStatus.Available && update.LatestVersion=="1.10.0","Numeric version comparison failed");
        Assert(update.PageUrl==UpdateChecker.RepositoryUrl+"/releases/tag/v1.10.0","Untrusted metadata controlled the update URL");
        Assert(UpdateChecker.Evaluate(Release("v2.0.0",true),current).Status!=UpdateStatus.Available,"Prerelease was offered");
        Assert(UpdateChecker.Evaluate(Release("v2.0.0",false,true),current).Status!=UpdateStatus.Available,"Draft was offered");
        Assert(UpdateChecker.Evaluate(Release("v2.0.0",false,false,false),current).Status==UpdateStatus.Unavailable,"Release without executable was offered");
        foreach(string invalid in new[]{"","{","null","[]","{\"tag_name\":\"v2.0.0\"}"})Assert(UpdateChecker.Evaluate(invalid,current).Status==UpdateStatus.Unavailable,"Invalid response was accepted");
        Assert(UpdateChecker.Check(()=>{throw new IOException("offline");},current).Status==UpdateStatus.Unavailable,"Network failure escaped update checker");
        using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();Assert(UpdateChecker.Check(cancelled.Token).Status==UpdateStatus.Unavailable,"Cancellation was not handled");}
        Console.WriteLine("PASS: update version ordering, stable release filtering, trusted download destination, invalid metadata, offline failure and cancellation.");return 0;
    }
}
