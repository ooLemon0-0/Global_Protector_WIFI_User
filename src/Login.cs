using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;

namespace GlobalProtectorWiFi {
    public sealed class LoginProfile {
        public string Portal="", Username="", Password="";
        public bool Enabled, Remember;
        public LoginProfile Copy(){return new LoginProfile{Portal=Portal,Username=Username,Password=Password,Enabled=Enabled,Remember=Remember};}
    }
    public static class LoginRules {
        public static string PortalKey(string text) {
            string value=(text??"").Trim(); Uri uri;
            if(!value.Contains("://"))value="https://"+value;
            if(!Uri.TryCreate(value,UriKind.Absolute,out uri) || uri.Scheme!="https" || String.IsNullOrEmpty(uri.Host) || !String.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath!="/" || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))throw new ArgumentException("VPN 门户地址格式不正确。");
            return uri.IdnHost.ToLowerInvariant()+(uri.IsDefaultPort?"":":"+uri.Port);
        }
        public static void Validate(LoginProfile profile) {
            if(!profile.Enabled)return;
            PortalKey(profile.Portal);
            if(String.IsNullOrWhiteSpace(profile.Username) || profile.Username.Length>256 || String.IsNullOrEmpty(profile.Password) || profile.Password.Length>1024)throw new ArgumentException("请填写账号和密码。");
        }
        public static string Decide(bool trusted,bool portalMatches,bool blocked,int users,int passwords,bool submit,bool connect,bool allowConnect) {
            if(!trusted || !portalMatches || blocked)return "manual";
            if(passwords>0)return users==1 && passwords==1 && submit?"credentials":"manual";
            return connect && allowConnect?"connect":"wait";
        }
    }
    public static class CredentialVault {
        static readonly byte[] Entropy=Encoding.UTF8.GetBytes("GlobalProtectorWiFi.LoginProfile.v1");
        public static void Write(string path,LoginProfile profile) {
            byte[] plain=Encoding.UTF8.GetBytes(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(profile));
            try {byte[] encrypted=ProtectedData.Protect(plain,Entropy,DataProtectionScope.CurrentUser);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,encrypted);}
            finally{Array.Clear(plain,0,plain.Length);}
        }
        public static LoginProfile Read(string path) {
            if(!File.Exists(path))return new LoginProfile();
            byte[] plain=ProtectedData.Unprotect(File.ReadAllBytes(path),Entropy,DataProtectionScope.CurrentUser);
            try{return new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<LoginProfile>(Encoding.UTF8.GetString(plain))??new LoginProfile();}
            finally{Array.Clear(plain,0,plain.Length);}
        }
        internal static string SavedPath {get{return Path.Combine(Program.Root,"login-profile.dpapi");}}
        internal static string HandoffPath(string id){Guid value;if(!Guid.TryParseExact(id,"D",out value))throw new ArgumentException("Invalid session ID.");return Path.Combine(Program.Root,"handoff-"+value.ToString("D")+".dpapi");}
        internal static string CreateHandoff(LoginProfile profile){string id=Guid.NewGuid().ToString("D");Write(HandoffPath(id),profile);return id;}
        internal static LoginProfile ConsumeHandoff(string id){string path=HandoffPath(id);try{return Read(path);}finally{if(File.Exists(path))File.Delete(path);}}
    }
    internal static class ClientIdentity {
        [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
        internal static bool SameUser(Process process){IntPtr token=IntPtr.Zero;try{if(!OpenProcessToken(process.Handle,8,out token))return false;using(var user=new System.Security.Principal.WindowsIdentity(token))using(var current=System.Security.Principal.WindowsIdentity.GetCurrent())return user.User!=null && user.User.Equals(current.User);}catch{return false;}finally{if(token!=IntPtr.Zero)CloseHandle(token);}}
        internal static string ActivePortal(){using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Palo Alto Networks\GlobalProtect\Settings")){return key==null?"":Convert.ToString(key.GetValue("LastUrl"));}}
        internal static string ClientPath() {
            using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PanGPS")) {
                string image=key==null?null:Convert.ToString(key.GetValue("ImagePath"));
                if(String.IsNullOrWhiteSpace(image))throw new InvalidOperationException("未找到 GlobalProtect。");
                image=Environment.ExpandEnvironmentVariables(image).Trim();
                if(image.StartsWith("\""))image=image.Split('"')[1];
                else {int end=image.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);if(end<0)throw new InvalidOperationException("GlobalProtect 路径无效。");image=image.Substring(0,end+4);}
                string result=Path.Combine(Path.GetDirectoryName(image),"PanGPA.exe");
                if(!File.Exists(result))throw new InvalidOperationException("未找到 GlobalProtect 客户端。");return Path.GetFullPath(result);
            }
        }
        internal static bool Trusted(string path) {
            try {
                if(!String.Equals(Path.GetFullPath(path),ClientPath(),StringComparison.OrdinalIgnoreCase))return false;
                using(var cert=new X509Certificate2(X509Certificate.CreateFromSignedFile(path))) {
                    if(cert.Subject.IndexOf("O=Palo Alto Networks",StringComparison.OrdinalIgnoreCase)<0)return false;
                }
                return VerifySignature(path);
            }catch{return false;}
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct FileInfo {public uint Size;[MarshalAs(UnmanagedType.LPWStr)]public string Path;public IntPtr Handle,Subject;}
        [StructLayout(LayoutKind.Sequential)]struct TrustData {public uint Size;public IntPtr Policy,Sip;public uint Ui,Revoke,Choice;public IntPtr File;public uint StateAction;public IntPtr State,Url;public uint Flags,Context;}
        [DllImport("wintrust.dll",ExactSpelling=true,PreserveSig=true)]static extern int WinVerifyTrust(IntPtr window,[In]ref Guid action,[In,Out]ref TrustData data);
        static bool VerifySignature(string path) {
            var file=new FileInfo {Size=(uint)Marshal.SizeOf(typeof(FileInfo)),Path=path};IntPtr pointer=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
            var data=new TrustData {Size=(uint)Marshal.SizeOf(typeof(TrustData)),Ui=2,Choice=1,File=pointer,StateAction=1,Flags=0x1000};
            var action=new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            try {Marshal.StructureToPtr(file,pointer,false);return WinVerifyTrust(new IntPtr(-1),ref action,ref data)==0;}
            finally {data.StateAction=2;WinVerifyTrust(new IntPtr(-1),ref action,ref data);Marshal.DestroyStructure(pointer,typeof(FileInfo));Marshal.FreeHGlobal(pointer);}
        }
    }
    internal static class NativeLogin {
        static readonly string[] ConnectNames={"连接","Connect"};
        static readonly string[] SubmitNames={"连接","Connect","登录","Login","Sign In","Sign in","确定","OK"};
        static bool Match(string value,string[] choices){return choices.Any(x=>String.Equals((value??"").Trim(),x,StringComparison.OrdinalIgnoreCase));}
        static bool Visible(AutomationElement item){try{return item.Current.IsEnabled && !item.Current.IsOffscreen;}catch{return false;}}
        static bool PortalMatches(LoginProfile profile){try{return LoginRules.PortalKey(ClientIdentity.ActivePortal())==LoginRules.PortalKey(profile.Portal);}catch{return false;}}
        static string Label(AutomationElement item){try {var label=item.Current.LabeledBy;return (item.Current.Name??"")+" "+(label==null?"":label.Current.Name);}catch{return "";}}
        static bool Warning(string text){string lower=text.ToLowerInvariant();return new[]{"验证码","动态密码","一次性","安全码","多因素","证书","代理","proxy","certificate","one-time","otp","saml","token","verification code","passcode","security code"," pin"}.Any(x=>lower.Contains(x));}
        internal static string Run(LoginProfile profile,bool allowConnect,CancellationToken cancel,Action<string> notice) {
            bool clicked=false,notified=false;int? boundPid=null;DateTime boundStart=DateTime.MinValue;string trustedPath=null;DateTime until=allowConnect?DateTime.UtcNow.AddMinutes(2):DateTime.MaxValue;
            try {
                LoginRules.Validate(profile);
                while(DateTime.UtcNow<until) {
                    cancel.ThrowIfCancellationRequested();
                    if(!PortalMatches(profile))return "自动登录未执行：当前 VPN 门户与登录设置不一致。";
                    foreach(var process in Process.GetProcessesByName("PanGPA")) {
                        using(process) {
                            if(process.SessionId!=Process.GetCurrentProcess().SessionId || !ClientIdentity.SameUser(process))continue;
                            string path;try{path=process.MainModule.FileName;}catch{continue;}
                            DateTime started=process.StartTime.ToUniversalTime();
                            if(boundPid!=process.Id || boundStart!=started || !String.Equals(path,trustedPath,StringComparison.OrdinalIgnoreCase)) {
                                if(!ClientIdentity.Trusted(path))return "自动登录未执行：无法验证 GlobalProtect 程序签名。";
                                boundPid=process.Id;boundStart=started;trustedPath=path;
                            }
                            var roots=AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ProcessIdProperty,process.Id));
                            foreach(AutomationElement root in roots) {
                                if(!Visible(root))continue;
                                var all=root.FindAll(TreeScope.Descendants,Condition.TrueCondition).Cast<AutomationElement>().Where(Visible).ToArray();
                                bool web=all.Any(x=>x.Current.ControlType==ControlType.Document || x.Current.ClassName=="Internet Explorer_Server");
                                string text=(root.Current.Name??"")+" "+String.Join(" ",all.Where(x=>x.Current.ControlType==ControlType.Text).Select(x=>x.Current.Name));
                                bool blocked=web || Warning(text);
                                var edits=all.Where(x=>x.Current.ControlType==ControlType.Edit).ToArray();
                                var passwords=edits.Where(x=>x.Current.IsPassword).ToArray();
                                var users=edits.Where(x=>!x.Current.IsPassword).ToArray();
                                var buttons=all.Where(x=>x.Current.ControlType==ControlType.Button).ToArray();
                                var submissions=buttons.Where(x=>Match(x.Current.Name,SubmitNames)).ToArray();
                                var connections=buttons.Where(x=>Match(x.Current.Name,ConnectNames)).ToArray();
                                var submit=submissions.Length==1?submissions[0]:null;
                                var connect=connections.Length==1?connections[0]:null;
                                if(passwords.Length==0 && connect!=null && users.Length>0) {
                                    object portalValue;
                                    if(users.Length!=1 || !users[0].TryGetCurrentPattern(ValuePattern.Pattern,out portalValue))blocked=true;
                                    else {try{if(LoginRules.PortalKey(((ValuePattern)portalValue).Current.Value)!=LoginRules.PortalKey(profile.Portal))blocked=true;}catch{blocked=true;}}
                                }
                                if(passwords.Length>0 && (Warning(Label(passwords[0])) || !text.ToLowerInvariant().Contains("password") && !text.Contains("密码")))blocked=true;
                                if(users.Length==1 && Warning(Label(users[0])))blocked=true;
                                string decision=LoginRules.Decide(true,PortalMatches(profile),blocked,users.Length,passwords.Length,submit!=null,connect!=null,allowConnect && !clicked);
                                if(decision=="manual") {
                                    if(!web && passwords.Length==0 && blocked){if(!notified){notice("请先在 GlobalProtect 中手动处理当前窗口；助手未提交凭据。");notified=true;}continue;}
                                    return "此窗口需手动处理；自动登录已停止。";
                                }
                                if(decision=="credentials") {
                                    if(users[0].Current.ProcessId!=process.Id || passwords[0].Current.ProcessId!=process.Id || submit.Current.ProcessId!=process.Id)return "登录控件不属于预期进程，未发送凭据。";
                                    object u,p,b;
                                    if(!users[0].TryGetCurrentPattern(ValuePattern.Pattern,out u) || !passwords[0].TryGetCurrentPattern(ValuePattern.Pattern,out p) || !submit.TryGetCurrentPattern(InvokePattern.Pattern,out b))return "登录窗口不支持安全填写，请手动登录。";
                                    if(((ValuePattern)u).Current.IsReadOnly || ((ValuePattern)p).Current.IsReadOnly)return "登录窗口不可编辑，请手动登录。";
                                    cancel.ThrowIfCancellationRequested();if(!PortalMatches(profile))return "VPN 门户已变化，未提交凭据。";
                                    ((ValuePattern)u).SetValue(profile.Username);cancel.ThrowIfCancellationRequested();
                                    if(!PortalMatches(profile))return "VPN 门户已变化，未填写密码。";
                                    ((ValuePattern)p).SetValue(profile.Password);cancel.ThrowIfCancellationRequested();
                                    if(!PortalMatches(profile))return "VPN 门户已变化，未提交登录。";
                                    ((InvokePattern)b).Invoke();return "已提交登录，等待 GlobalProtect 连接。";
                                }
                                if(decision=="connect") {object pattern;if(connect.TryGetCurrentPattern(InvokePattern.Pattern,out pattern)){cancel.ThrowIfCancellationRequested();((InvokePattern)pattern).Invoke();clicked=true;}}
                            }
                        }
                    }
                    if(cancel.WaitHandle.WaitOne(600))cancel.ThrowIfCancellationRequested();
                }
                return "未检测到受支持的登录窗口，请手动登录。";
            }catch(OperationCanceledException){return "";}catch{return "自动登录未完成，请在 GlobalProtect 中手动登录。";}
        }
    }
}
