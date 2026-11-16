using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace GlobalProtectorWiFi {
    public enum UpdateStatus { UpToDate, Available, Unavailable }
    public sealed class UpdateResult {
        public UpdateStatus Status;
        public string LatestVersion="", PageUrl="";
    }
    public static class UpdateChecker {
        public const string RepositoryUrl="https://github.com/ooLemon0-0/Global_Protector_WIFI_User";
        const string Endpoint="https://api.github.com/repos/ooLemon0-0/Global_Protector_WIFI_User/releases/latest";
        const int MaximumResponse=512*1024;
        public static Version CurrentVersion {get{return Assembly.GetExecutingAssembly().GetName().Version;}}
        public static string VersionText {get{return CurrentVersion.ToString(3);}}
        public static bool TryParseVersion(string tag,out Version version) {
            version=null;
            if(String.IsNullOrEmpty(tag) || tag.Length>64 || !Regex.IsMatch(tag,@"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z",RegexOptions.CultureInvariant))return false;
            Version parsed;
            if(!Version.TryParse(tag.TrimStart('v'),out parsed))return false;
            version=new Version(parsed.Major,parsed.Minor,parsed.Build,0);return true;
        }
        public static UpdateResult Evaluate(string json,Version current) {
            if(String.IsNullOrEmpty(json) || json.Length>MaximumResponse || current==null)return new UpdateResult{Status=UpdateStatus.Unavailable};
            try {
                var release=new JavaScriptSerializer{MaxJsonLength=MaximumResponse}.Deserialize<Dictionary<string,object>>(json);
                object tag,draft,preview,assets;
                Version version;
                if(release==null || !release.TryGetValue("tag_name",out tag) || !TryParseVersion(tag as string,out version) ||
                    !release.TryGetValue("draft",out draft) || !(draft is bool) || (bool)draft ||
                    !release.TryGetValue("prerelease",out preview) || !(preview is bool) || (bool)preview ||
                    !release.TryGetValue("assets",out assets) || !(assets is IEnumerable))return new UpdateResult{Status=UpdateStatus.Unavailable};
                bool hasExecutable=false;
                foreach(object item in (IEnumerable)assets){
                    var asset=item as Dictionary<string,object>;object name,size;
                    if(asset!=null && asset.TryGetValue("name",out name) && (name as string)=="GlobalProtectorWiFi.exe" && asset.TryGetValue("size",out size) && Convert.ToInt64(size)>0)hasExecutable=true;
                }
                if(!hasExecutable)return new UpdateResult{Status=UpdateStatus.Unavailable};
                var installed=new Version(current.Major,current.Minor,Math.Max(current.Build,0),Math.Max(current.Revision,0));
                return new UpdateResult{Status=version>installed?UpdateStatus.Available:UpdateStatus.UpToDate,LatestVersion=version.ToString(3),PageUrl=RepositoryUrl+"/releases/tag/"+Uri.EscapeDataString((string)tag)};
            }catch{return new UpdateResult{Status=UpdateStatus.Unavailable};}
        }
        public static UpdateResult Check(Func<string> fetch,Version current) {
            try{return Evaluate(fetch(),current);}catch{return new UpdateResult{Status=UpdateStatus.Unavailable};}
        }
        public static UpdateResult Check(CancellationToken cancel) {
            return Check(delegate {
                cancel.ThrowIfCancellationRequested();
                ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
                var request=(HttpWebRequest)WebRequest.Create(Endpoint);
                request.UserAgent="GlobalProtectorWiFi/"+VersionText;
                request.Accept="application/vnd.github+json";
                request.Headers["X-GitHub-Api-Version"]="2022-11-28";
                request.Timeout=8000;request.ReadWriteTimeout=8000;request.AllowAutoRedirect=false;
                request.UseDefaultCredentials=false;request.Credentials=null;
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel)) {
                    deadline.CancelAfter(10000);
                    using(deadline.Token.Register(delegate {request.Abort();}))
                    using(var response=(HttpWebResponse)request.GetResponse()) {
                        if(response.StatusCode!=HttpStatusCode.OK || response.ContentLength>MaximumResponse)throw new IOException("Update metadata is unavailable.");
                        using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8)) {
                            var text=new StringBuilder();var buffer=new char[4096];int count;
                            while((count=reader.Read(buffer,0,buffer.Length))>0){deadline.Token.ThrowIfCancellationRequested();if(text.Length+count>MaximumResponse)throw new IOException("Update metadata is too large.");text.Append(buffer,0,count);}
                            return text.ToString();
                        }
                    }
                }
            },CurrentVersion);
        }
    }
}
