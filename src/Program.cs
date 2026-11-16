using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Global Protector WiFi")]
[assembly: AssemblyDescription("Independent GlobalProtect split-routing companion")]
[assembly: AssemblyVersion("0.3.2.0")]
[assembly: AssemblyFileVersion("0.3.2.0")]
namespace GlobalProtectorWiFi {
    internal static class Program {
        internal const string DefaultTarget = "10.213.134.31";
        internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlobalProtectorWiFi");
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        internal static bool Admin { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }
        [STAThread] static int Main(string[] args) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length==2 && args[0]=="--diagnose-login") {
                try{File.WriteAllText(args[1],Program.Json.Serialize(LoginDiagnostics.Capture()),Encoding.UTF8);return 0;}
                catch(Exception e){File.WriteAllText(args[1],e.GetType().Name,Encoding.UTF8);return 1;}
            }
            if((args.Length==2 || args.Length==3) && args[0]=="--render-preview") {
                using(var form=new MainForm(false,true,false)) {
                    if(args.Length==3 && args[2]=="login"){form.RenderLoginSettings(args[1]);return 0;}
                    form.Show(); if(args.Length==3)form.PreviewState(args[2]); Application.DoEvents();
                    using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(args[1],System.Drawing.Imaging.ImageFormat.Png); }
                    form.Hide();
                }
                return 0;
            }
            if(args.Length==2 && args[0]=="--ui-self-test") {
                try {using(var form=new MainForm(false,true,false)){bool ok=form.CheckMainUi();File.WriteAllText(args[1],ok?"passed":"failed");return ok?0:1;}}
                catch(Exception e){File.WriteAllText(args[1],e.ToString());return 1;}
            }
            using (var mutex = new Mutex(false, "Local\\GlobalProtectorWiFi-" + WindowsIdentity.GetCurrent().User.Value)) {
                bool acquired = false;
                try {
                    try { acquired = mutex.WaitOne(3000); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) { MessageBox.Show("助手已在运行，请在任务栏或系统托盘打开。", "Global Protector"); return 1; }
                    Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { MessageBox.Show(e.Exception.Message, "操作未完成"); };
                    string session=args.FirstOrDefault(x=>x.StartsWith("--login-session=",StringComparison.Ordinal));
                    Application.Run(new MainForm(args.Contains("--autostart"), args.Contains("--demo"), args.Contains("--recover"),session==null?null:session.Substring(16)));
                } catch (Exception e) { MessageBox.Show(e.Message, "Global Protector"); return 1; }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
            return 0;
        }
    }
    public static class InputRules {
        public static string[] Parse(string text, bool allowEmpty) {
            var parts = text.Split(new[] { ',', ';', ' ', '\r', '\n', '\t', '，', '；' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            if (allowEmpty && parts.Length == 0) return parts;
            if (parts.Length < 1 || parts.Length > 16) throw new ArgumentException("请填写 1～16 个VPN 内网目标 IPv4 地址。无需填写 VPN 登录地址。");
            foreach (string part in parts) {
                IPAddress ip;
                if (part.Split('.').Length != 4 || !IPAddress.TryParse(part, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.ToString() != part)
                    throw new ArgumentException("无效的 IPv4 地址：" + part);
                byte first = ip.GetAddressBytes()[0];
                if (first == 0 || first == 127 || first >= 224) throw new ArgumentException("不支持的远程目标：" + part);
            }
            return parts;
        }
    }
    public static class Lifecycle {
        public static bool ShouldRearm(bool armed,bool stopping,bool error,bool pending) { return armed && !stopping && !error && !pending; }
    }
    internal sealed class SoftButton : Button {
        bool hover;
        internal SoftButton(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnPaint(PaintEventArgs e){
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            var r=new Rectangle(0,0,Width-1,Height-1);var p=new GraphicsPath();int d=18;
            p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();
            Color fill=Enabled?BackColor:Color.FromArgb(223,230,234);
            if(Enabled && hover)fill=ControlPaint.Light(fill,0.08F);
            using(p)using(var b=new SolidBrush(fill))using(var pen=new Pen(FlatAppearance.BorderColor)){e.Graphics.FillPath(b,p);if(FlatAppearance.BorderSize>0)e.Graphics.DrawPath(pen,p);}
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Enabled?ForeColor:Color.FromArgb(129,144,153),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            if(Focused && ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(5,5,Width-10,Height-10),ForeColor,fill);
        }
    }
    internal sealed class WifiMark : Control {
        internal Color Tint=Color.FromArgb(13,125,113);
        internal WifiMark(){SetStyle(ControlStyles.SupportsTransparentBackColor|ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);BackColor=Color.Transparent;}
        protected override void OnPaint(PaintEventArgs e){
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.ScaleTransform(Width/72F,Height/72F);
            using(var b=new SolidBrush(Color.FromArgb(231,245,240)))e.Graphics.FillEllipse(b,0,0,71,71);
            using(var p=new Pen(Tint,3.5F)){p.StartCap=LineCap.Round;p.EndCap=LineCap.Round;e.Graphics.DrawArc(p,17,20,38,30,205,130);e.Graphics.DrawArc(p,25,30,22,18,210,120);}
            using(var b=new SolidBrush(Tint))e.Graphics.FillEllipse(b,33,43,6,6);
        }
    }
    internal sealed class MainForm : Form {
        readonly Color ink = Color.FromArgb(28, 45, 57), muted = Color.FromArgb(99, 116, 128), accent = Color.FromArgb(13, 125, 113);
        readonly bool demo, autoStart;
        readonly TextBox targets = new TextBox(), manualDns = new TextBox(), log = new TextBox();
        readonly CheckBox dnsMode = new CheckBox();
        readonly Label stateTitle = new Label();
        readonly Button start = new SoftButton(), restore = new Button(), vpn = new Button();
        bool armed, openedVpn; System.Windows.Forms.Timer reconnectTimer;
        readonly NotifyIcon tray = new NotifyIcon();
        Process worker; bool running, stopping, quitWhenStopped, allowClose, hadError;
        string lastError = "";
        readonly Button settingsButton = new Button();

        readonly WifiMark heroMark = new WifiMark();
        TextBox visibleLog;
        bool recoveryNeeded;
        LoginProfile loginProfile=new LoginProfile();
        CancellationTokenSource loginCancel;bool loginAttempted,resumeCycle;
        string settingsPreview;bool settingsTest,settingsTestPassed;
        public MainForm(bool auto, bool preview, bool recover,string handoff=null) {
            autoStart=auto; demo=preview;
            Text="Global Protector"; ClientSize=new Size(400,344);
            FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false;
            StartPosition=FormStartPosition.CenterScreen; BackColor=Color.White; ForeColor=ink;
            Font=new Font("Microsoft YaHei UI",9F); AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
            Icon=CreateIcon();
            Controls.Add(LabelAt("连接 GlobalProtect 时保留普通网络访问",24,17,352,30,9,false));
            heroMark.SetBounds(164,63,72,72); Controls.Add(heroMark);
            stateTitle.SetBounds(24,148,352,38); stateTitle.TextAlign=ContentAlignment.MiddleCenter;
            stateTitle.Font=new Font("Microsoft YaHei UI",18,FontStyle.Bold); Controls.Add(stateTitle);
            start.SetBounds(24,213,352,44); Style(start,true);start.Font=new Font("Microsoft YaHei UI",10,FontStyle.Bold);
            start.AccessibleName="启动或停止";
            start.Click+=delegate {if(recoveryNeeded) StartWorker("Restore"); else if(running || armed) StopWorker(false); else StartWorker("Start");};
            Controls.Add(start); AcceptButton=start;
            Controls.Add(new Panel {Bounds=new Rectangle(24,287,352,1),BackColor=Color.FromArgb(233,237,240)});
            settingsButton.Text="登录设置";StyleLink(settingsButton);settingsButton.SetBounds(18,300,88,27);settingsButton.Click+=delegate {ShowSettings();};
            var history=new Button {Text="日志"};StyleLink(history);history.SetBounds(110,300,64,27);history.Click+=delegate {ShowHistory();};
            Controls.Add(settingsButton);Controls.Add(history);
            targets.Text=Program.DefaultTarget; dnsMode.Checked=true;
            if(!demo) {LoadSettings();LoadLogin(handoff);}

            var menu=new ContextMenuStrip();
            menu.Items.Add("显示助手",null,delegate {Show();WindowState=FormWindowState.Normal;Activate();});
            menu.Items.Add("停止",null,delegate {StopWorker(false);});
            menu.Items.Add("退出并恢复",null,delegate {StopWorker(true);});
            tray.Icon=Icon; tray.Text="Global Protector · 就绪"; tray.ContextMenuStrip=menu;
            tray.DoubleClick+=delegate {Show();WindowState=FormWindowState.Normal;Activate();};
            FormClosing+=delegate(object s,FormClosingEventArgs e) {
                if(!allowClose && (running || armed)) {e.Cancel=true;Hide();tray.Visible=true;tray.ShowBalloonTip(1800,"已最小化到托盘","右键菜单可停止或退出。",ToolTipIcon.Info);}
            };
            FormClosed+=delegate {CancelLogin();loginProfile.Password="";tray.Dispose();};
            SetStatus("准备就绪");
            Shown+=delegate {if(!demo && (autoStart || recover)) BeginInvoke(new Action(delegate {StartWorker(recover?"Restore":"Start");}));};
        }
        void StyleLink(Button button) {
            button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderSize=0;
            button.BackColor=BackColor;button.ForeColor=muted;button.Cursor=Cursors.Hand;
            button.Size=new Size(94,26);button.Margin=new Padding(0,0,6,0);button.TabStop=true;
        }
        void RefreshActions() {
            if(stopping) {start.Text="恢复中";start.Enabled=false;}
            else if(recoveryNeeded) {start.Text="恢复";start.Enabled=true;}
            else if(running || armed) {start.Text="停止";start.Enabled=true;}
            else {start.Text="启动";start.Enabled=true;}
            settingsButton.Enabled=!running && !armed && !recoveryNeeded;
        }
        bool EnsureLoginForStart() {
            return LoginRules.IsComplete(loginProfile) || ShowSettings(true);
        }
        bool ShowSettings(bool forStart=false) {
            if(running || armed || recoveryNeeded)return false;
            using(var dialog=new Form {Text=forStart?"填写 VPN 登录信息":"设置",ClientSize=new Size(486,460),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,StartPosition=FormStartPosition.CenterParent,Font=Font,BackColor=Color.White,Icon=Icon}) {
                var tabs=new TabControl {Bounds=new Rectangle(16,16,454,374)};
                var loginTab=new TabPage("VPN 登录");var networkTab=new TabPage("网络");tabs.TabPages.Add(loginTab);tabs.TabPages.Add(networkTab);dialog.Controls.Add(tabs);
                loginTab.Controls.Add(LabelAt("VPN 门户",16,15,400,22,9,true));
                var portal=new TextBox {Text=loginProfile.Portal,Bounds=new Rectangle(16,41,410,26)};loginTab.Controls.Add(portal);
                loginTab.Controls.Add(LabelAt("账号",16,81,400,22,9,true));
                var username=new TextBox {Text=loginProfile.Username,MaxLength=256,Bounds=new Rectangle(16,107,410,26)};loginTab.Controls.Add(username);
                loginTab.Controls.Add(LabelAt("密码",16,147,400,22,9,true));
                var password=new TextBox {Text=loginProfile.Password,UseSystemPasswordChar=true,MaxLength=1024,Bounds=new Rectangle(16,173,410,26)};loginTab.Controls.Add(password);
                var remember=new CheckBox {Text="记住登录信息（当前 Windows 用户）",Checked=loginProfile.Remember,Bounds=new Rectangle(16,215,410,26)};
                loginTab.Controls.Add(remember);
                loginTab.Controls.Add(LabelAt("填写完整后，启动时自动连接 GlobalProtect。",16,253,410,26,9,false));
                loginTab.Controls.Add(LabelAt("网页、验证码和证书确认需手动处理。",16,290,410,26,8.5F,false));

                networkTab.Controls.Add(LabelAt("目标地址",16,15,410,22,9,true));
                var editTargets=new TextBox {Multiline=true,Text=targets.Text,Bounds=new Rectangle(16,44,410,65),Font=new Font("Consolas",10)};networkTab.Controls.Add(editTargets);
                var chooseDns=new CheckBox {Text="自动选择 DNS",Checked=dnsMode.Checked,Bounds=new Rectangle(16,139,230,25)};
                var editDns=new TextBox {Text=manualDns.Text,Bounds=new Rectangle(276,139,150,25),Enabled=!chooseDns.Checked};chooseDns.CheckedChanged+=delegate {editDns.Enabled=!chooseDns.Checked;};
                networkTab.Controls.AddRange(new Control[]{chooseDns,editDns});
                var cancel=new SoftButton {Text="取消",Bounds=new Rectangle(240,406,105,38),DialogResult=DialogResult.Cancel};Style(cancel,false);
                var save=new SoftButton {Text=forStart?"启动":"保存",Bounds=new Rectangle(357,406,113,38)};Style(save,true);
                Func<LoginProfile> readLogin=()=>new LoginProfile {Portal=portal.Text.Trim(),Username=username.Text.Trim(),Password=password.Text,Remember=remember.Checked};
                EventHandler updateSave=delegate {save.Enabled=LoginRules.IsComplete(readLogin());};
                portal.TextChanged+=updateSave;username.TextChanged+=updateSave;password.TextChanged+=updateSave;updateSave(null,EventArgs.Empty);
                save.Click+=delegate {
                    try {
                        var next=readLogin();LoginRules.Validate(next);
                        string value=String.IsNullOrWhiteSpace(editTargets.Text)?Program.DefaultTarget:editTargets.Text;var list=InputRules.Parse(value,false);string selectedDns="";
                        if(!chooseDns.Checked){var parsed=InputRules.Parse(editDns.Text,false);if(parsed.Length!=1)throw new ArgumentException("指定 DNS 只需填写一个地址。");selectedDns=parsed[0];}
                        if(!demo){SaveSettings(list,selectedDns);try{if(next.Remember)CredentialVault.Write(CredentialVault.SavedPath,next);else if(File.Exists(CredentialVault.SavedPath))File.Delete(CredentialVault.SavedPath);}catch{throw new InvalidOperationException("登录信息无法保存，请检查当前用户的文件权限。");}}
                        loginProfile=next;targets.Text=String.Join(Environment.NewLine,list);manualDns.Text=selectedDns;dnsMode.Checked=chooseDns.Checked;dialog.DialogResult=DialogResult.OK;
                    }catch(ArgumentException e){MessageBox.Show(dialog,e.Message,"请检查设置",MessageBoxButtons.OK,MessageBoxIcon.Information);}
                    catch{MessageBox.Show(dialog,"设置未保存，请检查文件权限后重试。","保存失败",MessageBoxButtons.OK,MessageBoxIcon.Information);}
                };
                dialog.Controls.AddRange(new Control[]{cancel,save});dialog.AcceptButton=save;dialog.CancelButton=cancel;
                if(demo && settingsTest){
                    bool incomplete=!save.Enabled;
                    portal.Text="vpn.example.com";username.Text="test-user";password.Text="test-password";bool complete=save.Enabled;
                    password.Clear();bool missingPassword=!save.Enabled;
                    settingsTestPassed=password.UseSystemPasswordChar && tabs.TabPages.Count==2 && !remember.Checked && forStart && save.Text=="启动" && incomplete && complete && missingPassword && !Descendants(dialog).Any(c=>c.Text=="自动填写并连接" || c.Text=="自动打开 GlobalProtect");
                    return false;
                }
                if(demo && !String.IsNullOrEmpty(settingsPreview)) {dialog.Show();Application.DoEvents();using(var image=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(settingsPreview,System.Drawing.Imaging.ImageFormat.Png);}dialog.Hide();return false;}
                dialog.Shown+=delegate {if(String.IsNullOrWhiteSpace(portal.Text))portal.Focus();else if(String.IsNullOrWhiteSpace(username.Text))username.Focus();else password.Focus();};
                return dialog.ShowDialog(this)==DialogResult.OK;
            }
        }
        void LoadLogin(string handoff) {
            try{loginProfile=String.IsNullOrEmpty(handoff)?CredentialVault.Read(CredentialVault.SavedPath):CredentialVault.ConsumeHandoff(handoff);}
            catch{loginProfile=new LoginProfile();Append("登录信息无法解密，请在设置中重新输入。");}
            if(String.IsNullOrWhiteSpace(loginProfile.Portal))loginProfile.Portal=ClientIdentity.ActivePortal();
        }
        void CancelLogin() {var previous=loginCancel;loginCancel=null;if(previous!=null){try{previous.Cancel();}catch(ObjectDisposedException){}}}
        void BeginLogin() {
            CancelLogin();var cancellation=new CancellationTokenSource();loginCancel=cancellation;var copy=loginProfile.Copy();bool clickConnect=!resumeCycle;
            SetStatus("自动登录");
            System.Threading.Tasks.Task.Factory.StartNew(delegate {return NativeLogin.Run(copy,clickConnect,cancellation.Token,message=>Dispatch(delegate {if(loginCancel==cancellation && !cancellation.IsCancellationRequested){Append(message);SetStatus("手动确认");}}));}).ContinueWith(task=>{
                copy.Password="";bool cancelled=cancellation.IsCancellationRequested;
                Dispatch(delegate {if(loginCancel==cancellation){loginCancel=null;if(!cancelled && !String.IsNullOrEmpty(task.Result)){Append(task.Result);if(running && armed)SetStatus(task.Result.StartsWith("已提交登录",StringComparison.Ordinal)?"等待连接":"登录失败");}}cancellation.Dispose();});
            });
        }
        void ShowHistory() {
            using(var dialog=new Form {Text="日志",ClientSize=new Size(580,350),StartPosition=FormStartPosition.CenterParent,Font=Font,Icon=Icon}) {
                var view=new TextBox {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,BackColor=Color.White,Text=log.Text,Font=new Font("Microsoft YaHei UI",9)};
                var folder=new Button {Text="打开日志文件夹",Dock=DockStyle.Bottom,Height=38};Style(folder,false);folder.Click+=delegate {OpenLogs();};
                dialog.Controls.Add(view);dialog.Controls.Add(folder);visibleLog=view;
                try{dialog.ShowDialog(this);}finally{visibleLog=null;}
            }
        }
        internal void PreviewState(string state) {
            if(!demo) return;
            if(state=="ready") {running=true;armed=true;SetStatus("分流正常");}
            else if(state=="waiting") {running=true;armed=true;SetStatus("等待连接");}
            else if(state=="error") {hadError=true;recoveryNeeded=true;SetStatus("需要恢复");}
        }
        internal bool CheckMainUi() {
            if(!demo) throw new InvalidOperationException("Read-only UI test required.");
            bool noInputs=!Descendants(this).Any(c=>c is TextBox || c is CheckBox);
            bool idle=start.Text=="启动" && start.Enabled && targets.Text==Program.DefaultTarget;
            PreviewState("waiting");bool wait=start.Text=="停止" && start.Enabled && !settingsButton.Enabled;
            PreviewState("ready");bool ready=start.Text=="停止" && stateTitle.Text=="已连接";
            PreviewState("error");bool recovery=start.Text=="恢复" && start.Enabled;
            running=false;armed=false;recoveryNeeded=false;settingsTest=true;bool missingBlocked=!EnsureLoginForStart();settingsTest=false;
            loginProfile=new LoginProfile{Portal="vpn.example.com",Username="test-user",Password="test-password"};bool completeAllowed=EnsureLoginForStart();loginProfile=new LoginProfile();
            bool purpose=Descendants(this).Any(x=>x.Text.Contains("GlobalProtect") && x.Text.Contains("普通网络访问"));
            return noInputs && idle && wait && ready && recovery && purpose && settingsTestPassed && missingBlocked && completeAllowed;
        }
        internal void RenderLoginSettings(string path){if(!demo)throw new InvalidOperationException();settingsPreview=path;ShowSettings(true);settingsPreview=null;}
        static IEnumerable<Control> Descendants(Control parent) {foreach(Control c in parent.Controls){yield return c;foreach(var d in Descendants(c)) yield return d;}}
        Label LabelAt(string text, int x, int y, int w, int h, float size, bool bold) { return new Label { Text = text, Bounds = new Rectangle(x,y,w,h), Font = new Font("Microsoft YaHei UI",size,bold?FontStyle.Bold:FontStyle.Regular), ForeColor = bold?ink:muted }; }
        void Style(Button b, bool primary) { b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = primary?accent:Color.FromArgb(214,224,228); b.FlatAppearance.BorderSize = 1; b.BackColor = primary?accent:Color.White; b.ForeColor = primary?Color.White:ink; b.Cursor = Cursors.Hand; b.Font = new Font(Font,FontStyle.Bold); }
        static Icon CreateIcon() { using(var b=new Bitmap(32,32)) using(var g=Graphics.FromImage(b)) { g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(Color.Transparent); using(var fill=new SolidBrush(Color.FromArgb(13,125,113))) g.FillEllipse(fill,1,1,30,30); using(var f=new Font("Segoe UI",18,FontStyle.Bold)) g.DrawString("G",f,Brushes.White,3,-1); var h=b.GetHicon(); try { return (Icon)Icon.FromHandle(h).Clone(); } finally { DestroyIcon(h); } } }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        void SetStatus(string tag) {

            string status="未启动";
            if(tag=="分流正常")status="已连接";
            else if(tag=="等待连接" || tag=="等待重连")status="等待 VPN";
            else if(tag=="检查中" || tag=="验证中")status="连接中";
            else if(tag=="恢复中")status="恢复中";
            else if(tag=="自动登录")status="登录中";
            else if(tag=="手动确认")status="等待确认";
            else if(tag=="登录失败")status="登录失败";
            else if(tag=="需要恢复")status="需要恢复";
            else if(tag=="需要查看" || tag=="未开始" || tag=="已停止" && hadError)status="连接失败";
            stateTitle.Text=status;
            bool warning=status=="需要恢复" || status=="连接失败" || status=="登录失败";
            stateTitle.ForeColor=warning?Color.FromArgb(184,102,35):ink;
            heroMark.Tint=warning?Color.FromArgb(184,102,35):accent;heroMark.Invalidate();
            tray.Text="Global Protector · "+status;
            RefreshActions();
        }
        void Append(string message) {
            log.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+message+Environment.NewLine);
            if(visibleLog!=null && !visibleLog.IsDisposed)visibleLog.Text=log.Text;
        }
        void LoadSettings() { try { string path=Path.Combine(Program.Root,"settings.json"); if(File.Exists(path)) { var d=Program.Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(path)); targets.Text=d.ContainsKey("targets") && !String.IsNullOrWhiteSpace(d["targets"])?d["targets"]:Program.DefaultTarget; manualDns.Text=d.ContainsKey("dns")?d["dns"]:""; dnsMode.Checked=String.IsNullOrEmpty(manualDns.Text); } } catch { targets.Text=Program.DefaultTarget; Append("设置未读取，已使用预设目标。"); } }
        void SaveSettings(string[] list,string dns) { Directory.CreateDirectory(Program.Root); File.WriteAllText(Path.Combine(Program.Root,"settings.json"),Program.Json.Serialize(new Dictionary<string,string>{{"targets",String.Join(Environment.NewLine,list)},{"dns",dns}}),Encoding.UTF8); }
        string ExtractEngine() { Directory.CreateDirectory(Program.Root); string path=Path.Combine(Program.Root,"engine-0.3.2.ps1"); using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Engine.ps1")) using(var reader=new StreamReader(stream,Encoding.UTF8)) File.WriteAllText(path,reader.ReadToEnd(),new UTF8Encoding(true)); return path; }
        void StartWorker(string mode, bool resume = false) {
            string loginSession=null;
            try {
                if (demo) { Append("预览模式仅展示界面，不执行网络修改。"); return; }
                if (running) return;
                if(mode=="Start" && !EnsureLoginForStart())return;
                if(mode=="Start" && String.IsNullOrWhiteSpace(targets.Text)) targets.Text=Program.DefaultTarget;
                string[] list=mode=="Start"?InputRules.Parse(targets.Text,false):new string[0]; string dns="";
                if(mode=="Start" && !dnsMode.Checked) { var parsed=InputRules.Parse(manualDns.Text,false); if(parsed.Length!=1) throw new ArgumentException("指定 DNS 只需填写一个 IPv4 地址。"); dns=parsed[0]; }
                if(mode=="Start") SaveSettings(list,dns);
                if(!Program.Admin) {
                    if(mode=="Start")loginSession=CredentialVault.CreateHandoff(loginProfile);
                    var elevated=new ProcessStartInfo(Application.ExecutablePath,(mode=="Start"?"--autostart":"--recover")+(loginSession==null?"":" --login-session="+loginSession)) {UseShellExecute=true,Verb="runas"};
                    Process.Start(elevated); allowClose=true; Close(); return;
                }
                CancelLogin();loginAttempted=false;resumeCycle=resume;
                hadError=false; lastError=""; stopping=mode=="Restore"; armed=mode=="Start"; if(!resume) openedVpn=false;
                string path=ExtractEngine();
                if(mode=="Start") { string stopPath=Path.Combine(Program.Root,"stop.request"); if(File.Exists(stopPath)) File.Delete(stopPath); }
                var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"));
                info.Arguments="-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""+path+"\" -Mode "+mode+" -DataDirectory \""+Program.Root+"\"";
                if(mode=="Start") info.Arguments+=" -Targets \""+String.Join(",",list)+"\""+(dns.Length>0?" -PreferredDns "+dns:"");
                if(resume) info.Arguments+=" -Resume";
                info.UseShellExecute=false; info.CreateNoWindow=true; info.RedirectStandardOutput=true; info.RedirectStandardError=true; info.StandardOutputEncoding=Encoding.UTF8; info.StandardErrorEncoding=Encoding.UTF8;
                worker=new Process {StartInfo=info,EnableRaisingEvents=true};
                worker.OutputDataReceived += delegate(object s,DataReceivedEventArgs e) { if(e.Data!=null) Dispatch(delegate { OnEvent(e.Data); }); };
                worker.ErrorDataReceived += delegate(object s,DataReceivedEventArgs e) { if(!String.IsNullOrWhiteSpace(e.Data)) Dispatch(delegate { Append(e.Data); hadError=true; lastError=e.Data; }); };
                var observedWorker=worker; worker.Exited += delegate { observedWorker.WaitForExit(); Dispatch(delegate { WorkerEnded(); }); };
                running=true; start.Enabled=false; restore.Enabled=true; targets.Enabled=false; dnsMode.Enabled=false; manualDns.Enabled=false;
                SetStatus(mode=="Restore"?"恢复中":"检查中"); worker.Start(); worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
            } catch(Exception e) { if(loginSession!=null){try{File.Delete(CredentialVault.HandoffPath(loginSession));}catch{}} running=false; armed=false; start.Enabled=true; restore.Enabled=false; targets.Enabled=true; dnsMode.Enabled=true; manualDns.Enabled=!dnsMode.Checked; SetStatus("未开始"); Append(e.Message); }
        }
        void Dispatch(Action action) { try { if(!IsDisposed && IsHandleCreated) BeginInvoke(action); } catch(InvalidOperationException) {} }
        void OnEvent(string line) {
            try {
                var evt=Program.Json.Deserialize<Dictionary<string,object>>(line); string name=Convert.ToString(evt["Event"]); object data=evt.ContainsKey("Data")?evt["Data"]:null;
                if(name=="WAITING_FOR_VPN") { SetStatus("等待连接"); Append("等待 VPN 连接。"); if(!openedVpn) { openedVpn=true; OpenVpn(); } if(!loginAttempted){loginAttempted=true;BeginLogin();} }
                else if(name=="APPLIED_TEMPORARY_SPLIT") { SetStatus("验证中"); Append("临时分流已应用，正在验证。"); }
                else if(name=="READY") { CancelLogin();SetStatus("分流正常"); Append("路由、公网和 DNS 验证通过。"); tray.Visible=true; }
                else if(name=="ERROR" || name=="ROLLBACK_FAILED") { CancelLogin();hadError=true; var d=data as Dictionary<string,object>; lastError=d!=null && d.ContainsKey("Message")?Convert.ToString(d["Message"]):Convert.ToString(data); Append(lastError); SetStatus("需要查看"); }
                else if(name=="RESTORED") { CancelLogin();Append("临时改动已恢复。"); SetStatus("已恢复"); }
                else if(name=="HEALTH") { }
                else if(name=="DNS_AFTER_APPLY") { var d=data as Dictionary<string,object>; if(d!=null && d.ContainsKey("NameServer")) Append("VPN DNS："+Convert.ToString(d["NameServer"])); }
            } catch { Append(line); }
        }
        string Friendly(string error) {
            if(error.IndexOf("Disconnect GlobalProtect",StringComparison.OrdinalIgnoreCase)>=0) return "请先断开 GlobalProtect，再开启助手。";
            if(error.IndexOf("earlier session",StringComparison.OrdinalIgnoreCase)>=0) return "上次有未恢复的改动，请点击“停止”后重试。";
            if(error.IndexOf("default route",StringComparison.OrdinalIgnoreCase)>=0) return "VPN 的路由策略发生变化，助手已停止，请查看日志。";
            return error.Length>180?error.Substring(0,180)+"…":error;
        }
        void WorkerEnded() {
            CancelLogin();running=false;
            bool pending=false;
            try { string path=Path.Combine(Program.Root,"state.json"); if(File.Exists(path)) { var saved=Program.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path)); pending=saved.ContainsKey("Stage") && Convert.ToString(saved["Stage"])!="restored"; } } catch { pending=true; }
            recoveryNeeded=pending;
            if(pending) { armed=false; quitWhenStopped=false; hadError=true; Show(); SetStatus("需要恢复"); }
            if(Lifecycle.ShouldRearm(armed,stopping,hadError,pending)) {
                SetStatus("等待重连");
                Append("已恢复，等待重连。");
                reconnectTimer=new System.Windows.Forms.Timer {Interval=1500};
                reconnectTimer.Tick += delegate { reconnectTimer.Stop(); reconnectTimer.Dispose(); reconnectTimer=null; if(armed && !stopping) StartWorker("Start",true); };
                reconnectTimer.Start(); return;
            }
            armed=false; stopping=false; start.Enabled=true; restore.Enabled=true; targets.Enabled=true; dnsMode.Enabled=true; manualDns.Enabled=!dnsMode.Checked;
            if(!hadError) SetStatus("已停止");
            else if(!pending) SetStatus("已停止");
            if(quitWhenStopped && !pending) { allowClose=true; Close(); }
        }
        void StopWorker(bool quit) {
            CancelLogin();quitWhenStopped=quit; armed=false; if(reconnectTimer!=null) {reconnectTimer.Stop(); reconnectTimer.Dispose(); reconnectTimer=null;}
            if(demo) { allowClose=true; if(quit) Close(); return; }
            if(running) { if(stopping) return; stopping=true; Directory.CreateDirectory(Program.Root); File.WriteAllText(Path.Combine(Program.Root,"stop.request"),DateTime.Now.ToString("o")); SetStatus("恢复中"); }
            else if(File.Exists(Path.Combine(Program.Root,"state.json"))) { StartWorker("Restore"); }
            else if(quit) { allowClose=true; Close(); }
        }
        void OpenLogs() { Directory.CreateDirectory(Program.Root); Process.Start("explorer.exe",Program.Root); }
        void OpenVpn() {
            try {Process.Start(ClientIdentity.ClientPath());}
            catch{Append("无法打开 GlobalProtect，请确认官方客户端已安装。");}
        }
    }
}
