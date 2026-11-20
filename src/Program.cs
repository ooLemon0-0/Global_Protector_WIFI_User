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
[assembly: AssemblyVersion("1.1.7.0")]
[assembly: AssemblyFileVersion("1.1.7.0")]
namespace GlobalProtectorWiFi {
    internal static class Program {
        internal const string DefaultTarget = "10.213.134.31";
        internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlobalProtectorWiFi");
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        internal static bool Admin { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }
        [STAThread] static int Main(string[] args) {
            if(args.Length==1 && args[0]=="--request-vpn-disconnect") {
                // UI Automation belongs on a worker thread that has no window.
                int result=2;
                var thread=new Thread(()=>result=NativeDisconnect.Request());
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start(); thread.Join();
                return result;
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length==1 && args[0]=="--vietnam-file-transfer")
                return VietnamWebSession.Bundled ? VietnamFileWindow.Run() : 2;
            if(args.Length==2 && args[0]=="--check-update") {
                var result=UpdateChecker.Check(CancellationToken.None);
                File.WriteAllText(args[1],Program.Json.Serialize(new{Status=result.Status.ToString(),CurrentVersion=UpdateChecker.VersionText,LatestVersion=result.LatestVersion,PageUrl=result.PageUrl}),Encoding.UTF8);return 0;
            }
            if(args.Length==2 && args[0]=="--diagnose-login") {
                try{File.WriteAllText(args[1],Program.Json.Serialize(LoginDiagnostics.Capture()),Encoding.UTF8);return 0;}
                catch(Exception e){File.WriteAllText(args[1],e.GetType().Name,Encoding.UTF8);return 1;}
            }
            if((args.Length==2 || args.Length==3) && args[0]=="--render-preview") {
                if(args.Length==3 && (args[2]=="transfer" || args[2]=="transfer-progress")) {
                    VietnamFileWindow.Preview(args[1],args[2]=="transfer-progress");return 0;
                }
                using(var form=new MainForm(false,true,false)) {
                    if(args.Length==3 && args[2]=="login"){form.RenderLoginSettings(args[1]);return 0;}
                    if(args.Length==3 && args[2]=="close"){form.RenderCloseDialog(args[1]);return 0;}
                    form.Show(); if(args.Length==3)form.PreviewState(args[2]); Application.DoEvents();
                    using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(args[1],System.Drawing.Imaging.ImageFormat.Png); }
                    form.Hide();
                }
                return 0;
            }
            if(args.Length==2 && args[0]=="--ui-self-test") {
                try {using(var form=new MainForm(false,true,false)){bool ok=VietnamFileWindow.CheckUi() && form.CheckMainUi();File.WriteAllText(args[1],ok?"passed":"failed");return ok?0:1;}}
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
    internal class ActionButton : Button {
        ToolTip tip;
        string reason;
        internal bool ActionAvailable { get { return String.IsNullOrEmpty(reason); } }

        internal void SetBlocked(string explanation, ToolTip tooltip) {
            if(reason==explanation && tip==tooltip)return;
            reason=explanation;tip=tooltip;
            Cursor=ActionAvailable?Cursors.Hand:Cursors.No;
            AccessibleDescription=reason;
            tip.SetToolTip(this,reason ?? "");
            Invalidate();
        }

        protected override void OnClick(EventArgs e) {
            if(ActionAvailable)base.OnClick(e);
            else if(Visible)tip.Show(reason,this,Width/2,Height,3000);
        }

        protected override void OnPaint(PaintEventArgs e) {
            if(ActionAvailable){base.OnPaint(e);return;}
            bool border=FlatAppearance.BorderSize>0;
            Color fill=border?Color.FromArgb(246,248,249):BackColor;
            Color text=Color.FromArgb(123,138,146);
            using(var brush=new SolidBrush(fill)) e.Graphics.FillRectangle(brush,ClientRectangle);
            if(border)using(var pen=new Pen(FlatAppearance.BorderColor))e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1);
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,text,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            if(Focused && ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-4,-4),text,fill);
        }
    }
    internal sealed class SoftButton : ActionButton {
        bool hover;
        internal SoftButton(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnPaint(PaintEventArgs e){
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            var r=new Rectangle(0,0,Width-1,Height-1);var p=new GraphicsPath();int d=18;
            p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();
            Color fill=ActionAvailable?BackColor:Color.FromArgb(223,230,234);
            if(ActionAvailable && hover)fill=ControlPaint.Light(fill,0.08F);
            using(p)using(var b=new SolidBrush(fill))using(var pen=new Pen(FlatAppearance.BorderColor)){e.Graphics.FillPath(b,p);if(FlatAppearance.BorderSize>0)e.Graphics.DrawPath(pen,p);}
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,ActionAvailable?ForeColor:Color.FromArgb(129,144,153),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            if(Focused && ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(5,5,Width-10,Height-10),ForeColor,fill);
        }
    }
    internal sealed class MainForm : Form {
        readonly Color ink = Color.FromArgb(28, 45, 57), muted = Color.FromArgb(99, 116, 128), accent = Color.FromArgb(13, 125, 113);
        readonly bool demo, autoStart;
        readonly TextBox targets = new TextBox(), manualDns = new TextBox(), log = new TextBox();
        readonly CheckBox dnsMode = new CheckBox();
        readonly Label stateTitle = new Label();
        readonly ActionButton start = new SoftButton();
        readonly Button restore = new Button(), vpn = new Button();
        bool armed, openedVpn; System.Windows.Forms.Timer reconnectTimer;
        readonly NotifyIcon tray = new NotifyIcon();
        Process worker; bool running, stopping, quitWhenStopped, allowClose, hadError;
        bool manualDisconnect, engineCompleted, cleanupSucceeded;
        string finishedReason;
        string lastError = "";
        readonly ActionButton settingsButton = new ActionButton(), updateButton = new ActionButton();
        readonly ToolTip updateTip=new ToolTip();
        System.Windows.Forms.Timer updateTimer;
        CancellationTokenSource updateCancel;
        UpdateResult availableUpdate;

        readonly LogoMark heroMark = new LogoMark();
        Icon notificationIcon;
        bool closePromptOpen;
        Func<DialogResult> closePrompt;
        TextBox visibleLog;
        bool recoveryNeeded;
        LoginProfile loginProfile=new LoginProfile();
        CancellationTokenSource loginCancel;bool loginAttempted,resumeCycle;
        readonly VietnamWebSession vietnamWeb = new VietnamWebSession();
        readonly ActionButton webButton = new ActionButton(), defaultSiteButton = new ActionButton(),
            sendFilesButton = new ActionButton(), sshButton = new ActionButton();
        readonly Label webStatus = new Label();
        const string WebWaitingReason="请先连接越南 VPN，等待分流就绪。";
        string settingsPreview;bool settingsTest,settingsTestPassed;
        public MainForm(bool auto, bool preview, bool recover,string handoff=null) {
            autoStart=auto; demo=preview;
            Text="Global Protector"; ClientSize=new Size(400,VietnamWebSession.Bundled?392:344);
            FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false;
            StartPosition=FormStartPosition.CenterScreen; BackColor=Color.White; ForeColor=ink;
            Font=new Font("Microsoft YaHei UI",9F); AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
            Icon=AppBrand.CreateIcon(32);
            Controls.Add(LabelAt("连接 GlobalProtect 时保留普通网络访问",24,17,352,30,9,false));
            heroMark.SetBounds(160,59,80,80); Controls.Add(heroMark);
            stateTitle.SetBounds(24,148,352,38); stateTitle.TextAlign=ContentAlignment.MiddleCenter;
            stateTitle.Font=new Font("Microsoft YaHei UI",18,FontStyle.Bold); Controls.Add(stateTitle);
            start.SetBounds(24,213,352,44); Style(start,true);start.Font=new Font("Microsoft YaHei UI",10,FontStyle.Bold);
            start.AccessibleName="启动或停止";
            start.Click+=delegate {if(manualDisconnect) OpenVpn(); else if(recoveryNeeded) StartWorker("Restore"); else if(running || armed) StopWorker(false); else StartWorker("Start");};
            Controls.Add(start); AcceptButton=start;
            int footer=VietnamWebSession.Bundled?348:300;
            Controls.Add(new Panel {Bounds=new Rectangle(24,footer-13,352,1),BackColor=Color.FromArgb(233,237,240)});
            settingsButton.Text="登录设置";StyleLink(settingsButton);settingsButton.SetBounds(18,footer,88,27);settingsButton.Click+=delegate {ShowSettings();};
            var history=new Button {Text="日志"};StyleLink(history);history.SetBounds(104,footer,57,27);history.Click+=delegate {ShowHistory();};
            Controls.Add(settingsButton);Controls.Add(history);
            if (VietnamWebSession.Bundled) {
                webButton.Text="打开越南网页";StyleLink(webButton);webButton.SetBounds(163,footer,96,27);
                webButton.Click+=delegate {StartVietnamWeb(true);};Controls.Add(webButton);
                defaultSiteButton.Text="打开默认站点";Style(defaultSiteButton,false);defaultSiteButton.SetBounds(24,292,112,33);
                defaultSiteButton.Click+=delegate {StartVietnamWeb(true,"DefaultSite");};Controls.Add(defaultSiteButton);
                sendFilesButton.Text=VietnamFileWindow.WindowTitle;Style(sendFilesButton,false);sendFilesButton.SetBounds(144,292,112,33);
                sendFilesButton.Click+=delegate {OpenVietnamFileWindow();};Controls.Add(sendFilesButton);
                sshButton.Text="打开现场 SSH";Style(sshButton,false);sshButton.SetBounds(264,292,112,33);
                sshButton.Click+=delegate {StartVietnamWeb(true,"Ssh");};Controls.Add(sshButton);
                SetVietnamActions(WebWaitingReason);
                webStatus.SetBounds(24,260,352,24);
                webStatus.ForeColor=muted;webStatus.TextAlign=ContentAlignment.MiddleCenter;
                webStatus.AutoEllipsis=true;Controls.Add(webStatus);
            }
            updateButton.Text="检查更新";StyleLink(updateButton);updateButton.SetBounds(262,footer,120,27);updateButton.Click+=delegate {if(availableUpdate!=null)OpenUpdate();else CheckForUpdates(true);};
            updateTip.SetToolTip(updateButton,"当前版本 v"+UpdateChecker.VersionText+" · 点击检查更新");Controls.Add(updateButton);
            targets.Text=Program.DefaultTarget; dnsMode.Checked=true;
            if(!demo) {LoadSettings();LoadLogin(handoff);}

            var menu=new ContextMenuStrip();
            menu.Items.Add("显示助手",null,delegate {Show();WindowState=FormWindowState.Normal;Activate();});
            menu.Items.Add("检查更新",null,delegate {CheckForUpdates(true);});
            if (VietnamWebSession.CanSaveInstaller)
                menu.Items.Add("保存远端中转安装包…",null,delegate {SaveVietnamInstaller();});
            menu.Items.Add("停止",null,delegate {StopWorker(false);});
            menu.Items.Add("退出并恢复",null,delegate {StopWorker(true);});
            notificationIcon=AppBrand.CreateIcon(16);tray.Icon=notificationIcon; tray.Text="Global Protector · 就绪"; tray.ContextMenuStrip=menu;
            tray.DoubleClick+=delegate {Show();WindowState=FormWindowState.Normal;Activate();};
            FormClosing+=HandleWindowClosing;
            FormClosed+=delegate {CancelLogin();CancelUpdates();vietnamWeb.Stop();if(updateTimer!=null)updateTimer.Dispose();if(reconnectTimer!=null)reconnectTimer.Dispose();updateTip.Dispose();loginProfile.Password="";tray.Dispose();notificationIcon.Dispose();Icon.Dispose();};
            SetStatus("准备就绪");
            Shown+=delegate {
                if(!demo){updateTimer=new System.Windows.Forms.Timer{Interval=1500};updateTimer.Tick+=delegate {updateTimer.Stop();CheckForUpdates(false);};updateTimer.Start();}
                if(!demo && (autoStart || recover)) BeginInvoke(new Action(delegate {StartWorker(recover?"Restore":"Start");}));
            };
        }
        void HandleWindowClosing(object sender,FormClosingEventArgs e) {
            if(allowClose)return;
            if(e.CloseReason!=CloseReason.UserClosing) {
                armed=false;CancelLogin();
                if(running && !stopping)try{Directory.CreateDirectory(Program.Root);File.WriteAllText(Path.Combine(Program.Root,"stop.request"),DateTime.Now.ToString("o"));}catch{}
                return;
            }
            e.Cancel=true;if(closePromptOpen)return;
            DialogResult choice;closePromptOpen=true;
            try{if(closePrompt!=null)choice=closePrompt();else using(var dialog=new CloseChoiceForm())choice=dialog.ShowDialog(this);}finally{closePromptOpen=false;}
            if(choice==DialogResult.Yes){Hide();if(!demo)tray.Visible=true;}
            else if(choice==DialogResult.No)BeginInvoke(new Action(delegate {StopWorker(true);}));
        }
        void CancelUpdates(){var previous=updateCancel;updateCancel=null;if(previous!=null){try{previous.Cancel();}catch(ObjectDisposedException){}}}
        void CheckForUpdates(bool manual) {
            if(demo || updateCancel!=null)return;
            if(updateTimer!=null)updateTimer.Stop();
            var cancellation=new CancellationTokenSource();updateCancel=cancellation;
            updateButton.SetBlocked("正在检查更新，请稍候。",updateTip);updateButton.Text="检查中…";
            System.Threading.Tasks.Task.Factory.StartNew(()=>UpdateChecker.Check(cancellation.Token)).ContinueWith(task=>{
                var result=task.IsFaulted?new UpdateResult{Status=UpdateStatus.Unavailable}:task.Result;bool cancelled=cancellation.IsCancellationRequested;
                Dispatch(delegate {if(updateCancel==cancellation && !cancelled){updateCancel=null;ApplyUpdateResult(result,manual);}});
                cancellation.Dispose();
            });
        }
        void ApplyUpdateResult(UpdateResult result,bool manual) {
            updateButton.SetBlocked(null,updateTip);
            if(result.Status==UpdateStatus.Available) {
                availableUpdate=result;updateButton.Text="发现新版本";updateButton.ForeColor=accent;
                updateTip.SetToolTip(updateButton,"v"+result.LatestVersion+" · 点击打开下载页面");
                if(!demo && tray.Visible)tray.ShowBalloonTip(5000,"发现新版本 v"+result.LatestVersion,"点击助手中的“发现新版本”查看更新。",ToolTipIcon.Info);
                if(manual)OpenUpdate();
            } else {
                availableUpdate=null;updateButton.Text="检查更新";updateButton.ForeColor=muted;
                updateTip.SetToolTip(updateButton,"当前版本 v"+UpdateChecker.VersionText+(result.Status==UpdateStatus.UpToDate?" · 已是最新版本":" · 暂时无法检查更新"));
                if(manual)MessageBox.Show(this,result.Status==UpdateStatus.UpToDate?"当前已是最新版本（v"+UpdateChecker.VersionText+"）。":"暂时无法检查更新，请稍后重试。","检查更新",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
        }
        void OpenUpdate() {
            if(availableUpdate==null)return;
            try{Process.Start(new ProcessStartInfo(availableUpdate.PageUrl){UseShellExecute=true});}
            catch{MessageBox.Show(this,"无法打开浏览器，请在 GitHub 仓库的 Releases 页面下载。","检查更新",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        }
        void StyleLink(Button button) {
            button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderSize=0;
            button.BackColor=BackColor;button.ForeColor=muted;button.Cursor=Cursors.Hand;
            button.Size=new Size(94,26);button.Margin=new Padding(0,0,6,0);button.TabStop=true;
        }
        void RefreshActions() {
            if(manualDisconnect) start.Text="打开 GlobalProtect";
            else if(stopping) start.Text=stateTitle.Text=="正在断开 VPN"?"正在断开":"恢复中";
            else if(recoveryNeeded) start.Text="恢复";
            else start.Text=running || armed?"停止":"启动";
            start.SetBlocked(stopping && !manualDisconnect?"网络恢复正在进行，请稍候。":null,updateTip);
            settingsButton.SetBlocked(recoveryNeeded?"请先恢复网络，再修改登录设置。":
                running || armed?"请先停止当前连接，再修改登录设置。":null,updateTip);
        }
        bool EnsureLoginForStart() {
            return LoginRules.IsComplete(loginProfile) || ShowSettings(true);
        }
        bool ShowSettings(bool forStart=false) {
            if(running || armed || recoveryNeeded)return false;
            using(var dialog=new Form {Text=forStart?"填写 VPN 登录信息":"设置",ClientSize=new Size(486,460),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,StartPosition=FormStartPosition.CenterParent,Font=Font,BackColor=Color.White,Icon=Icon})
            using(var hints=new ToolTip()) {
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
                EventHandler updateSave=delegate {save.SetBlocked(LoginRules.IsComplete(readLogin())?null:"请填写 VPN 门户、账号和密码。",hints);};
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
                    bool incomplete=!save.ActionAvailable && save.Cursor==Cursors.No;
                    portal.Text="vpn.example.com";username.Text="test-user";password.Text="test-password";bool complete=save.ActionAvailable;
                    password.Clear();bool missingPassword=!save.ActionAvailable;
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
            else if(state=="update")ApplyUpdateResult(new UpdateResult{Status=UpdateStatus.Available,LatestVersion="1.1.0",PageUrl=UpdateChecker.RepositoryUrl+"/releases/tag/v1.1.0"},false);
        }
        internal bool CheckMainUi() {
            if(!demo) throw new InvalidOperationException("Read-only UI test required.");
            bool noInputs=!Descendants(this).Any(c=>c is TextBox || c is CheckBox);
            bool idle=start.Text=="启动" && start.ActionAvailable && targets.Text==Program.DefaultTarget &&
                (!VietnamWebSession.Bundled || (!sendFilesButton.ActionAvailable && sendFilesButton.Cursor==Cursors.No));
            PreviewState("waiting");bool wait=start.Text=="停止" && start.ActionAvailable && !settingsButton.ActionAvailable && settingsButton.Cursor==Cursors.No;
            PreviewState("ready");bool ready=start.Text=="停止" && stateTitle.Text=="已连接";
            OnEvent("{\"Event\":\"LOG_WARNING\",\"Data\":{\"Reason\":\"IOException\"}}");
            bool logWarningSafe=running && armed && !hadError && !recoveryNeeded && stateTitle.Text=="已连接";
            OnEvent("{\"Event\":\"DNS_DEGRADED\",\"Data\":\"等待 DNS\"}");
            bool dnsWaitSafe=running && armed && !hadError && stateTitle.Text=="DNS 待恢复" && start.Text=="停止";
            OnEvent("{\"Event\":\"DNS_RECOVERED\",\"Data\":\"DNS 已恢复\"}");
            dnsWaitSafe=dnsWaitSafe && running && armed && stateTitle.Text=="已连接";
            OnEvent("{\"Event\":\"VPN_DISCONNECTING\",\"Data\":{\"Reason\":\"Stopped\"}}");
            bool disconnectSafe=running && stopping && !start.ActionAvailable && start.Cursor==Cursors.No;
            OnEvent("{\"Event\":\"VPN_DISCONNECT_MANUAL\",\"Data\":\"手动断开\"}");
            disconnectSafe=disconnectSafe && running && stopping && start.ActionAvailable && start.Text=="打开 GlobalProtect";
            OnEvent("{\"Event\":\"VPN_DISCONNECTED\",\"Data\":{\"Reason\":\"Stopped\"}}");
            disconnectSafe=disconnectSafe && !manualDisconnect && stateTitle.Text=="恢复中";
            stopping=false;
            PreviewState("error");bool recovery=start.Text=="恢复" && start.ActionAvailable;
            running=false;armed=false;recoveryNeeded=false;settingsTest=true;bool missingBlocked=!EnsureLoginForStart();settingsTest=false;
            loginProfile=new LoginProfile{Portal="vpn.example.com",Username="test-user",Password="test-password"};bool completeAllowed=EnsureLoginForStart();loginProfile=new LoginProfile();
            bool purpose=Descendants(this).Any(x=>x.Text.Contains("GlobalProtect") && x.Text.Contains("普通网络访问"));
            bool closeFlow=CheckCloseFlow();
            return noInputs && idle && wait && ready && logWarningSafe && dnsWaitSafe && disconnectSafe && recovery && purpose && settingsTestPassed && missingBlocked && completeAllowed && closeFlow;
        }
        bool CheckCloseFlow() {
            ShowInTaskbar=false;Show();Application.DoEvents();running=true;armed=true;int prompts=0;
            closePrompt=delegate {prompts++;return DialogResult.Cancel;};Close();
            bool cancel=Visible && !IsDisposed && running && armed;
            closePrompt=delegate {prompts++;return DialogResult.Yes;};Close();
            bool minimize=!Visible && !IsDisposed && running && armed && !stopping;
            Show();closePrompt=delegate {prompts++;return DialogResult.No;};Close();Application.DoEvents();
            return cancel && minimize && IsDisposed && !armed && quitWhenStopped && prompts==3;
        }
        internal void RenderCloseDialog(string path){if(!demo)throw new InvalidOperationException();using(var dialog=new CloseChoiceForm()){dialog.Show();Application.DoEvents();using(var image=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(path,System.Drawing.Imaging.ImageFormat.Png);}dialog.Hide();}}
        internal void RenderLoginSettings(string path){if(!demo)throw new InvalidOperationException();settingsPreview=path;ShowSettings(true);settingsPreview=null;}
        static IEnumerable<Control> Descendants(Control parent) {foreach(Control c in parent.Controls){yield return c;foreach(var d in Descendants(c)) yield return d;}}
        Label LabelAt(string text, int x, int y, int w, int h, float size, bool bold) { return new Label { Text = text, Bounds = new Rectangle(x,y,w,h), Font = new Font("Microsoft YaHei UI",size,bold?FontStyle.Bold:FontStyle.Regular), ForeColor = bold?ink:muted }; }
        void Style(Button b, bool primary) { b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = primary?accent:Color.FromArgb(214,224,228); b.FlatAppearance.BorderSize = 1; b.BackColor = primary?accent:Color.White; b.ForeColor = primary?Color.White:ink; b.Cursor = Cursors.Hand; b.Font = new Font(Font,FontStyle.Bold); }
        void SetStatus(string tag) {

            string status="未启动";
            if(tag=="分流正常")status="已连接";
            else if(tag=="DNS 待恢复")status="DNS 待恢复";
            else if(tag=="等待连接" || tag=="等待重连")status="等待 VPN";
            else if(tag=="检查中" || tag=="验证中")status="连接中";
            else if(tag=="恢复中")status="恢复中";
            else if(tag=="正在断开 VPN" || tag=="请手动断开 VPN")status=tag;
            else if(tag=="自动登录")status="登录中";
            else if(tag=="手动确认")status="等待确认";
            else if(tag=="登录失败")status="登录失败";
            else if(tag=="需要恢复")status="需要恢复";
            else if(tag=="分流失败" || tag=="已停止" && hadError && stateTitle.Text=="分流失败")status="分流失败";
            else if(tag=="需要查看" || tag=="未开始" || tag=="已停止" && hadError)status="连接失败";
            stateTitle.Text=status;
            bool warning=status=="需要恢复" || status=="连接失败" || status=="登录失败" || status=="分流失败" || status=="DNS 待恢复" || status=="请手动断开 VPN";
            stateTitle.ForeColor=warning?Color.FromArgb(184,102,35):ink;
            tray.Text="Global Protector · "+status;
            RefreshActions();
        }
        void Append(string message) {
            log.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+message+Environment.NewLine);
            if(visibleLog!=null && !visibleLog.IsDisposed)visibleLog.Text=log.Text;
        }
        void LoadSettings() { try { string path=Path.Combine(Program.Root,"settings.json"); if(File.Exists(path)) { var d=Program.Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(path)); targets.Text=d.ContainsKey("targets") && !String.IsNullOrWhiteSpace(d["targets"])?d["targets"]:Program.DefaultTarget; manualDns.Text=d.ContainsKey("dns")?d["dns"]:""; dnsMode.Checked=String.IsNullOrEmpty(manualDns.Text); } } catch { targets.Text=Program.DefaultTarget; Append("设置未读取，已使用预设目标。"); } }
        void SaveSettings(string[] list,string dns) { Directory.CreateDirectory(Program.Root); File.WriteAllText(Path.Combine(Program.Root,"settings.json"),Program.Json.Serialize(new Dictionary<string,string>{{"targets",String.Join(Environment.NewLine,list)},{"dns",dns}}),Encoding.UTF8); }
        string ExtractEngine() { Directory.CreateDirectory(Program.Root); string path=Path.Combine(Program.Root,"engine-"+UpdateChecker.VersionText+".ps1"); using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Engine.ps1")) using(var reader=new StreamReader(stream,Encoding.UTF8)) File.WriteAllText(path,reader.ReadToEnd(),new UTF8Encoding(true)); return path; }
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
                manualDisconnect=false;engineCompleted=false;cleanupSucceeded=false;finishedReason=null;
                hadError=false; lastError=""; stopping=mode=="Restore"; armed=mode=="Start"; if(!resume) openedVpn=false;
                string path=ExtractEngine();
                if(mode=="Start") { string stopPath=Path.Combine(Program.Root,"stop.request"); if(File.Exists(stopPath)) File.Delete(stopPath); }
                var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"));
                info.Arguments="-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""+path+"\" -Mode "+mode+" -DataDirectory \""+Program.Root+"\"";
                using(var owner=Process.GetCurrentProcess()) info.Arguments+=" -ControllerPath \""+Application.ExecutablePath+"\" -OwnerPid "+owner.Id;
                if(mode=="Start") {
                    info.Arguments+=" -Targets \""+String.Join(",",list)+"\""+(dns.Length>0?" -PreferredDns "+dns:"");
                    if(list.Contains(Program.DefaultTarget)) info.Arguments+=" -RdpDiagnosticTarget "+Program.DefaultTarget;
                }
                info.UseShellExecute=false; info.CreateNoWindow=true; info.RedirectStandardOutput=true; info.RedirectStandardError=true; info.StandardOutputEncoding=Encoding.UTF8; info.StandardErrorEncoding=Encoding.UTF8;
                worker=new Process {StartInfo=info,EnableRaisingEvents=true};
                worker.OutputDataReceived += delegate(object s,DataReceivedEventArgs e) { if(e.Data!=null) Dispatch(delegate { OnEvent(e.Data); }); };
                worker.ErrorDataReceived += delegate(object s,DataReceivedEventArgs e) { if(!String.IsNullOrWhiteSpace(e.Data)) Dispatch(delegate { Append(e.Data); hadError=true; lastError=e.Data; }); };
                var observedWorker=worker; worker.Exited += delegate {
                    observedWorker.WaitForExit();
                    Dispatch(delegate { if(worker==observedWorker) WorkerEnded(mode); observedWorker.Dispose(); });
                };
                running=true; restore.Enabled=true; targets.Enabled=false; dnsMode.Enabled=false; manualDns.Enabled=false;
                SetStatus(mode=="Restore"?"恢复中":"检查中"); worker.Start(); worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
            } catch(Exception e) { if(loginSession!=null){try{File.Delete(CredentialVault.HandoffPath(loginSession));}catch{}} running=false; armed=false; restore.Enabled=false; targets.Enabled=true; dnsMode.Enabled=true; manualDns.Enabled=!dnsMode.Checked; SetStatus("未开始"); Append(e.Message); }
        }
        void Dispatch(Action action) { try { if(!IsDisposed && IsHandleCreated) BeginInvoke(action); } catch(InvalidOperationException) {} }
        void StartVietnamWeb(bool retry=false,string mode="Automatic") {
            if (demo || !VietnamWebSession.Bundled || !running || !armed || stopping || hadError) return;
            if (!VietnamWebSession.AppliesTo(loginProfile.Portal, ClientIdentity.ActivePortal(), targets.Text)) return;
            SetVietnamActions(null);
            if (retry) SetVietnamWebStatus(mode=="Ssh"?"正在检查现场 SSH…":"正在检查网页中转…");
            vietnamWeb.Start(loginProfile.Portal, targets.Text, retry, mode, message => {
                Append(message);
                SetVietnamWebStatus(message);
            }, Dispatch);
        }
        void SetVietnamWebStatus(string message) {
            webStatus.Text=message;
            updateTip.SetToolTip(webStatus,message);
        }
        void SetVietnamActions(string reason) {
            if(!VietnamWebSession.Bundled)return;
            foreach(var button in new[]{webButton,defaultSiteButton,sendFilesButton,sshButton})button.SetBlocked(reason,updateTip);
        }
        void StopVietnamWeb() { SetVietnamActions(WebWaitingReason);SetVietnamWebStatus("");vietnamWeb.Stop(); }
        async void OpenVietnamFileWindow() {
            if(demo || !sendFilesButton.ActionAvailable)return;
            try { await vietnamWeb.OpenFileWindow(); }
            catch(Exception error) { Append(error.Message);SetVietnamWebStatus(error.Message); }
        }
        void SaveVietnamInstaller() {
            using(var dialog=new SaveFileDialog {FileName=VietnamWebSession.RemoteInstaller,Filter="安装程序 (*.exe)|*.exe"}) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try { VietnamWebSession.SaveRemoteInstaller(dialog.FileName); }
                catch(Exception error) { MessageBox.Show(this,error.Message,"无法保存安装包",MessageBoxButtons.OK,MessageBoxIcon.Error); }
            }
        }
        void OnEvent(string line) {
            try {
                var evt=Program.Json.Deserialize<Dictionary<string,object>>(line); string name=Convert.ToString(evt["Event"]); object data=evt.ContainsKey("Data")?evt["Data"]:null;
                if(name=="WAITING_FOR_VPN") { if(stopping)return; SetStatus("等待连接"); Append("等待 VPN 连接。"); if(!openedVpn) { openedVpn=true; OpenVpn(); } if(!loginAttempted){loginAttempted=true;BeginLogin();} }
                else if(name=="VPN_DETECTED") { CancelLogin();SetStatus("验证中");Append(Convert.ToString(data)); }
                else if(name=="APPLIED_TEMPORARY_SPLIT") { SetStatus("验证中"); Append("临时分流已应用，正在验证。"); }
                else if(name=="READY") { CancelLogin();SetStatus("分流正常"); Append("路由、公网和 DNS 验证通过。"); tray.Visible=true; StartVietnamWeb(); }
                else if(name=="ERROR" || name=="ROLLBACK_FAILED") { CancelLogin();StopVietnamWeb();hadError=true; var d=data as Dictionary<string,object>; lastError=d!=null && d.ContainsKey("Message")?Convert.ToString(d["Message"]):Convert.ToString(data); Append(lastError); SetStatus(name=="ERROR"?"分流失败":"需要恢复"); }
                else if(name=="RESTORED") { CancelLogin();Append("临时改动已恢复。"); if(!hadError)SetStatus("已恢复"); }
                else if(name=="VPN_LOST") { CancelLogin();StopVietnamWeb();Append(Convert.ToString(data));SetStatus("恢复中"); }
                else if(name=="VPN_DISCONNECTING") { CancelLogin();StopVietnamWeb();stopping=true;SetStatus("正在断开 VPN"); }
                else if(name=="VPN_DISCONNECT_MANUAL") {
                    CancelLogin();stopping=true;manualDisconnect=true;
                    Append(Convert.ToString(data));SetStatus("请手动断开 VPN");
                    if(!demo) { Show(); WindowState=FormWindowState.Normal; }
                }
                else if(name=="VPN_DISCONNECTED") { manualDisconnect=false;Append("GlobalProtect 已断开。");SetStatus("恢复中"); }
                else if(name=="TOOL_COMPLETE") {
                    var result=data as Dictionary<string,object>;
                    if(result!=null) {
                        finishedReason=Convert.ToString(result["Reason"]);
                        cleanupSucceeded=Convert.ToBoolean(result["CleanupSucceeded"]);
                        engineCompleted=true;
                    }
                }
                else if(name=="HEALTH") { }
                else if(name=="LOG_WARNING") { Append("日志文件暂时无法写入，助手继续运行。"); }
                else if(name=="DNS_FALLBACK") { Append("VPN DNS 不可用，已切换到原网络的 DNS。"); }
                else if(name=="DNS_DEGRADED") { SetStatus("DNS 待恢复"); Append(Convert.ToString(data)); StartVietnamWeb(); }
                else if(name=="DNS_RECOVERED") { SetStatus("分流正常"); Append(Convert.ToString(data)); }
                else if(name=="VPN_TARGET_PROBE") {
                    var result=data as Dictionary<string,object>;
                    if(result!=null && result.ContainsKey("Reachable"))
                        Append(Convert.ToBoolean(result["Reachable"])?"VPN 内网远程桌面端口可达。":"VPN 内网远程桌面端口无响应。请检查隧道或目标服务器。");
                }
                else if(name=="VPN_DEFAULT_RETURNED" || name=="VPN_DEFAULT_REPAIRED") { Append(Convert.ToString(data)); }
                else if(name=="DNS_AFTER_APPLY") { var d=data as Dictionary<string,object>; if(d!=null && d.ContainsKey("NameServer")) Append("VPN DNS："+Convert.ToString(d["NameServer"])); }
            } catch { Append(line); }
        }
        string Friendly(string error) {
            if(error.IndexOf("Disconnect GlobalProtect",StringComparison.OrdinalIgnoreCase)>=0) return "请先断开 GlobalProtect，再开启助手。";
            if(error.IndexOf("earlier session",StringComparison.OrdinalIgnoreCase)>=0) return "上次有未恢复的改动，请点击“停止”后重试。";
            if(error.IndexOf("default route",StringComparison.OrdinalIgnoreCase)>=0) return "VPN 的路由策略发生变化，助手已停止，请查看日志。";
            return error.Length>180?error.Substring(0,180)+"…":error;
        }
        void WorkerEnded(string mode) {
            CancelLogin();StopVietnamWeb();running=false;
            manualDisconnect=false;
            if(!engineCompleted && mode=="Start") {
                armed=false;stopping=false;
                Append("分流引擎意外退出，正在断开 VPN 并恢复网络。");
                StartWorker("Restore");
                return;
            }
            bool pending=HasPendingRecovery() || !engineCompleted || !cleanupSucceeded;
            recoveryNeeded=pending;
            if(pending) { armed=false; stopping=false; quitWhenStopped=false; hadError=true; Show(); SetStatus("需要恢复"); }
            if(finishedReason=="VpnLost" && Lifecycle.ShouldRearm(armed,stopping,hadError,pending)) {
                SetStatus("等待重连");
                Append("已恢复，等待重连。");
                reconnectTimer=new System.Windows.Forms.Timer {Interval=1500};
                reconnectTimer.Tick += delegate { reconnectTimer.Stop(); reconnectTimer.Dispose(); reconnectTimer=null; if(armed && !stopping) StartWorker("Start",true); };
                reconnectTimer.Start(); return;
            }
            armed=false; stopping=false; restore.Enabled=true; targets.Enabled=true; dnsMode.Enabled=true; manualDns.Enabled=!dnsMode.Checked;
            if(!hadError) SetStatus("已停止");
            else if(!pending) SetStatus("已停止");
            if(quitWhenStopped && !pending) { allowClose=true; Close(); }
        }
        bool HasPendingRecovery() {
            try {string path=Path.Combine(Program.Root,"state.json");if(!File.Exists(path))return false;var saved=Program.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));return saved==null || !saved.ContainsKey("Stage") || Convert.ToString(saved["Stage"])!="restored";}
            catch{return true;}
        }
        void StopWorker(bool quit) {
            bool wasArmed=armed;
            CancelLogin();StopVietnamWeb();quitWhenStopped=quit; armed=false; if(reconnectTimer!=null) {reconnectTimer.Stop(); reconnectTimer.Dispose(); reconnectTimer=null;}
            if(demo) { allowClose=true; if(quit) Close(); return; }
            if(running) { if(stopping) return; stopping=true; Directory.CreateDirectory(Program.Root); File.WriteAllText(Path.Combine(Program.Root,"stop.request"),DateTime.Now.ToString("o")); SetStatus("正在断开 VPN"); }
            else if(wasArmed || recoveryNeeded || HasPendingRecovery()) { StartWorker("Restore"); }
            else if(quit) { allowClose=true; Close(); }
        }
        void OpenLogs() { Directory.CreateDirectory(Program.Root); Process.Start("explorer.exe",Program.Root); }
        void OpenVpn() {
            try {Process.Start(ClientIdentity.ClientPath());}
            catch{Append("无法打开 GlobalProtect，请确认官方客户端已安装。");}
        }
    }
}
