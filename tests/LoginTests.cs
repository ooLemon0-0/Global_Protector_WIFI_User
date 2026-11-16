using System;
using System.IO;
using System.Text;
using GlobalProtectorWiFi;
class LoginTests {
    static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    static int Main(){
        Assert(LoginRules.PortalKey("VPN.example.com")==LoginRules.PortalKey("https://vpn.example.com:443/"),"Portal normalization");
        foreach(string input in new[]{"","http://vpn.example.com","https://user:password@vpn.example.com","https://vpn.example.com/path","https://vpn.example.com/?secret=x","https://vpn.example.com/#x"}){bool rejected=false;try{LoginRules.PortalKey(input);}catch(ArgumentException){rejected=true;}Assert(rejected,"Unsafe portal accepted");}
        Assert(!LoginRules.IsComplete(null),"Null profile accepted");
        for(int bits=0;bits<8;bits++){
            var candidate=new LoginProfile {Portal=(bits&1)!=0?"vpn.example.com":"",Username=(bits&2)!=0?"test-user":"",Password=(bits&4)!=0?"test-password":""};
            Assert(LoginRules.IsComplete(candidate)==(bits==7),"Incomplete credentials bypassed startup gate");
        }
        Assert(!LoginRules.IsComplete(new LoginProfile{Portal="vpn.example.com",Username=" ",Password="test-password"}),"Whitespace username accepted");
        Assert(!LoginRules.IsComplete(new LoginProfile{Portal="vpn.example.com",Username="test-user",Password=" "}),"Whitespace password accepted");
        Assert(!LoginRules.IsComplete(new LoginProfile{Portal="http://vpn.example.com",Username="test-user",Password="test-password"}),"Invalid portal bypassed startup gate");
        Assert(typeof(LoginProfile).GetField("Enabled")==null,"Automatic login remains optional");
        var legacy=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<LoginProfile>("{\"Portal\":\"vpn.example.com\",\"Username\":\"test-user\",\"Password\":\"test-password\",\"Enabled\":false,\"Remember\":true}");
        Assert(LoginRules.IsComplete(legacy) && legacy.Remember,"Legacy disabled-login profile failed migration");
        Assert(LoginRules.PasswordLabelRecognized("Enter login credentials","Password"),"GlobalProtect 5.2 password description was rejected");
        Assert(LoginRules.PasswordLabelRecognized("登录","密码"),"Chinese password description was rejected");
        Assert(LoginRules.PasswordLabelRecognized("Password",""),"Static password label was rejected");
        Assert(!LoginRules.PasswordLabelRecognized("Enter login credentials","Username"),"Unrecognized credential label was accepted");
        Assert(LoginRules.KnownNativeCredentialForm("#32770","1166","1167","1168"),"Verified GlobalProtect 5.2 native credential controls rejected");
        Assert(!LoginRules.KnownNativeCredentialForm("Browser","1166","1167","1168"),"Different window class accepted");
        Assert(!LoginRules.KnownNativeCredentialForm("#32770","1166","1167","1179"),"Cancel button accepted as submit");
        Assert(LoginRules.Decide(true,true,false,1,1,true,false,true)=="credentials","Native username/password layout");
        Assert(LoginRules.Decide(false,true,false,1,1,true,false,true)=="manual","Untrusted process");
        Assert(LoginRules.Decide(true,false,false,1,1,true,false,true)=="manual","Different portal");
        Assert(LoginRules.Decide(true,true,true,1,1,true,false,true)=="manual","Certificate, OTP or browser barrier");
        Assert(LoginRules.Decide(true,true,false,2,1,true,false,true)=="manual","Ambiguous username");
        Assert(LoginRules.Decide(true,true,false,1,2,true,false,true)=="manual","Ambiguous password");
        Assert(LoginRules.Decide(true,true,false,1,1,false,false,true)=="manual","Missing submit");
        Assert(LoginRules.Decide(true,true,false,0,0,false,true,true)=="connect","First explicit connect");
        Assert(LoginRules.Decide(true,true,false,0,0,false,true,false)=="wait","Passive reconnect must not click connect");
        string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-profile.dpapi");
        var profile=new LoginProfile {Portal="vpn.example.com",Username="fake-login-user",Password="fake-test-secret-ONLY-12345",Remember=true};
        try{
            CredentialVault.Write(path,profile);string raw=Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert(!raw.Contains(profile.Password) && !raw.Contains(profile.Username),"Plaintext credential on disk");
            var restored=CredentialVault.Read(path);Assert(restored.Username==profile.Username && restored.Password==profile.Password && restored.Portal==profile.Portal,"DPAPI round trip");
            var bytes=File.ReadAllBytes(path);bytes[bytes.Length/2]^=0x5A;File.WriteAllBytes(path,bytes);bool rejected=false;try{CredentialVault.Read(path);}catch{rejected=true;}Assert(rejected,"Modified ciphertext was accepted");
        }finally{if(File.Exists(path))File.Delete(path);}
        Console.WriteLine("PASS: mandatory credentials, legacy profile migration, portal binding, guarded login decisions, passive reconnect, encrypted credential storage and tamper rejection.");return 0;
    }
}
