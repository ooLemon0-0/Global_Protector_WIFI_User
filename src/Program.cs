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
[assembly: AssemblyVersion("0.1.1.0")]
[assembly: AssemblyFileVersion("0.1.1.0")]
namespace GlobalProtectorWiFi {
    internal static class Program {
        internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlobalProtectorWiFi");
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        internal static bool Admin { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }
        [STAThread] static int Main(string[] args) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length==2 && args[0]=="--render-preview") {
                using(var form=new MainForm(false,true,false)) {
                    form.Show(); Application.DoEvents();
                    using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(args[1],System.Drawing.Imaging.ImageFormat.Png); }
                    form.Hide();
                }
                return 0;
            }
            using (var mutex = new Mutex(false, "Local\\GlobalProtectorWiFi-" + WindowsIdentity.GetCurrent().User.Value)) {
                bool acquired = false;
                try {
                    try { acquired = mutex.WaitOne(3000); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) { MessageBox.Show("助手已在运行，请在任务栏或系统托盘打开。", "Global Protector"); return 1; }
                    Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { MessageBox.Show(e.Exception.Message, "操作未完成"); };
                    Application.Run(new MainForm(args.Contains("--autostart"), args.Contains("--demo"), args.Contains("--recover")));
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
    internal sealed class Card : Panel {
        internal Color Surface = Color.White;
        internal Card() { DoubleBuffered = true; BackColor = Color.Transparent; Padding = new Padding(20); }
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Round(r, 16)) using (var b = new SolidBrush(Surface)) using (var pen = new Pen(Color.FromArgb(226, 232, 236))) { e.Graphics.FillPath(b, p); e.Graphics.DrawPath(pen, p); }
            base.OnPaint(e);
        }
        static GraphicsPath Round(Rectangle r, int d) { var p = new GraphicsPath(); p.AddArc(r.X,r.Y,d,d,180,90); p.AddArc(r.Right-d,r.Y,d,d,270,90); p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); p.AddArc(r.X,r.Bottom-d,d,d,90,90); p.CloseFigure(); return p; }
    }
    internal sealed class MainForm : Form {
        readonly Color ink = Color.FromArgb(28, 45, 57), muted = Color.FromArgb(99, 116, 128), accent = Color.FromArgb(13, 125, 113);
        readonly bool demo, autoStart;
        readonly TextBox targets = new TextBox(), manualDns = new TextBox(), log = new TextBox();
        readonly CheckBox dnsMode = new CheckBox();
        readonly Label stateTitle = new Label(), stateDetail = new Label(), badge = new Label(), checks = new Label();
        readonly Button start = new Button(), restore = new Button(), vpn = new Button();
        readonly CheckBox autoOpen = new CheckBox();
        bool armed, openedVpn; System.Windows.Forms.Timer reconnectTimer;
        readonly NotifyIcon tray = new NotifyIcon();
        Process worker; bool running, stopping, quitWhenStopped, allowClose, hadError;
        string lastError = "";
        public MainForm(bool auto, bool preview, bool recover) {
            autoStart = auto; demo = preview;
            Text = "Global Protector · Wi-Fi 分流助手"; ClientSize = new Size(790, 736); MinimumSize = new Size(806, 775);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(245, 248, 250);
            Font = new Font("Microsoft YaHei UI", 9F); ForeColor = ink; AutoScaleMode = AutoScaleMode.Dpi;
            Icon = CreateIcon();
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 18, 26, 12), ColumnCount = 1, RowCount = 7 };
            foreach (int height in new[] { 75, 141, 199, 55, 118, 62, 28 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(layout);
            var header = new Panel { Dock = DockStyle.Fill };
            var logo = new Label { Text = "G", Font = new Font("Segoe UI", 25, FontStyle.Bold), ForeColor = Color.White, BackColor = accent, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 5, 52, 52) };
            header.Controls.Add(logo);
            header.Controls.Add(LabelAt("Global Protector", 68, 3, 400, 30, 19, true));
            header.Controls.Add(LabelAt("VPN 内网与日常上网，各走其路。", 70, 38, 440, 24, 10, false));
            var version = LabelAt("v0.1.1", 620, 14, 90, 25, 9, false); version.TextAlign = ContentAlignment.MiddleRight; version.Anchor = AnchorStyles.Top | AnchorStyles.Right; header.Controls.Add(version);
            layout.Controls.Add(header, 0, 0);
            var status = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Surface = Color.FromArgb(235, 246, 243) };
            badge.SetBounds(20, 17, 620, 20); badge.Font = new Font(Font, FontStyle.Bold); badge.ForeColor = accent;
            stateTitle.SetBounds(20, 44, 660, 32); stateTitle.Font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold);
            stateDetail.SetBounds(20, 83, 680, 37); stateDetail.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right; stateDetail.ForeColor = muted;
            status.Controls.AddRange(new Control[] { badge, stateTitle, stateDetail }); layout.Controls.Add(status, 0, 1);
            var settings = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
            settings.Controls.Add(LabelAt("VPN 内网目标 IP", 20, 14, 250, 24, 10, true));
            targets.Multiline = true; targets.ScrollBars = ScrollBars.Vertical; targets.SetBounds(20, 44, 415, 66); targets.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right; targets.Font = new Font("Consolas", 11); targets.AccessibleName = "VPN 内网目标 IPv4 地址";
            settings.Controls.Add(targets);
            settings.Controls.Add(LabelAt("填写远程电脑地址；多个 IP 用逗号或换行分隔。", 20, 115, 660, 23, 8.5F, false));
            settings.Controls.Add(LabelAt("DNS", 20, 147, 45, 26, 9, true));
            dnsMode.Text = "自动选择 VPN 可用 DNS"; dnsMode.Checked = true; dnsMode.SetBounds(65, 144, 245, 28); dnsMode.AccessibleName = "DNS 选择方式";
            manualDns.SetBounds(325, 144, 190, 28); manualDns.Font = new Font("Consolas", 10); manualDns.Enabled = false; manualDns.AccessibleName = "指定 VPN DNS 地址";
            dnsMode.CheckedChanged += delegate { manualDns.Enabled = !dnsMode.Checked && !running; };
            settings.Controls.AddRange(new Control[] { dnsMode, manualDns });
            vpn.Text = "打开 GlobalProtect"; vpn.SetBounds(465, 47, 232, 40); vpn.Anchor = AnchorStyles.Top | AnchorStyles.Right; Style(vpn, false); vpn.Click += delegate { OpenVpn(); }; settings.Controls.Add(vpn);
            layout.Controls.Add(settings, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            start.Text = "开启助手"; restore.Text = "停止并恢复";
            foreach (var b in new[] { start, restore }) { b.Size = new Size(230, 41); b.Margin = new Padding(0, 0, 12, 0); Style(b, b == start); actions.Controls.Add(b); }
            start.Click += delegate { StartWorker("Start"); }; restore.Click += delegate { StopWorker(false); };
            restore.Enabled = false; autoOpen.Text="开启后自动打开 VPN"; autoOpen.Checked=true; autoOpen.Size=new Size(230,41); actions.Controls.Add(autoOpen); layout.Controls.Add(actions, 0, 3);
            var logCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10) };
            checks.SetBounds(16, 12, 680, 22); checks.Text = "连接状态与恢复记录"; checks.Font = new Font(Font, FontStyle.Bold); logCard.Controls.Add(checks);
            log.ReadOnly = true; log.Multiline = true; log.ScrollBars = ScrollBars.Vertical; log.BorderStyle = BorderStyle.None; log.BackColor = Color.White; log.ForeColor = muted; log.SetBounds(16, 40, 680, 57); log.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom; log.AccessibleName = "运行记录"; logCard.Controls.Add(log); layout.Controls.Add(logCard, 0, 4);
            var note = LabelAt("普通上网保留原网络，所填 IP 和 VPN DNS 经隧道。\r\nVPN 强制 DNS 策略仍可能影响公司内部域名；最小化后请保留助手运行。", 0, 0, 730, 58, 9, false); note.Dock = DockStyle.Fill; layout.Controls.Add(note, 0, 5);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            var logsLink = new LinkLabel { Text = "查看本机日志", AutoSize = true, LinkColor = accent, Margin = new Padding(0, 0, 24, 0) }; logsLink.LinkClicked += delegate { OpenLogs(); };
            var repoLink = new LinkLabel { Text = "GitHub · 源码与反馈", AutoSize = true, LinkColor = accent }; repoLink.LinkClicked += delegate { Process.Start("https://github.com/ooLemon0-0/Global_Protector_WIFI_User"); };
            footer.Controls.AddRange(new Control[] { logsLink, repoLink }); layout.Controls.Add(footer, 0, 6);
            var loginHelp=new LinkLabel {Text="快速登录说明",AutoSize=true,LinkColor=accent,Margin=new Padding(24,0,0,0)};
            loginHelp.LinkClicked += delegate {MessageBox.Show("助手只负责打开 GlobalProtect。\n\n如需下次免输密码，请由 VPN 管理员启用官方 Save User Credentials 或 SSO。MFA/动态密码仍按公司策略验证。\n\n请在官方客户端登录；助手不保存账号密码。","快速登录",MessageBoxButtons.OK,MessageBoxIcon.Information);};
            footer.Controls.Add(loginHelp);
            // Position from the laid-out parent size, rather than anchoring
            // controls before TableLayoutPanel assigns their initial width.
            version.Anchor=AnchorStyles.Top|AnchorStyles.Left;
            stateDetail.Anchor=AnchorStyles.Top|AnchorStyles.Left;
            targets.Anchor=AnchorStyles.Top|AnchorStyles.Left;
            vpn.Anchor=AnchorStyles.Top|AnchorStyles.Left;
            log.Anchor=AnchorStyles.Top|AnchorStyles.Left;
            header.Resize += delegate { version.Left=Math.Max(0,header.ClientSize.Width-version.Width); };
            status.Resize += delegate { stateDetail.Width=status.ClientSize.Width-40; stateTitle.Width=stateDetail.Width; };
            settings.Resize += delegate { vpn.Left=settings.ClientSize.Width-252; targets.Width=Math.Max(200,vpn.Left-40); };
            logCard.Resize += delegate { log.Width=logCard.ClientSize.Width-32; log.Height=Math.Max(20,logCard.ClientSize.Height-51); };
            Shown += delegate {
                version.Left=Math.Max(0,header.ClientSize.Width-version.Width);
                stateDetail.Width=status.ClientSize.Width-40; stateTitle.Width=stateDetail.Width;
                vpn.Left=settings.ClientSize.Width-252; targets.Width=Math.Max(200,vpn.Left-40);
                log.Width=logCard.ClientSize.Width-32; log.Height=Math.Max(20,logCard.ClientSize.Height-51);
                dnsMode.Refresh();
            };
            var menu = new ContextMenuStrip(); menu.Items.Add("显示助手", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); }); menu.Items.Add("停止并恢复", null, delegate { StopWorker(false); }); menu.Items.Add("退出并恢复", null, delegate { StopWorker(true); });
            tray.Icon = Icon; tray.Text = "Global Protector · 就绪"; tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
            FormClosing += delegate(object s, FormClosingEventArgs e) { if (!allowClose && (running || armed)) { e.Cancel = true; Hide(); tray.Visible = true; tray.ShowBalloonTip(2000, "助手继续运行", "可在托盘中停止分流或退出并恢复。", ToolTipIcon.Info); } };
            FormClosed += delegate { tray.Dispose(); };
            LoadSettings(); SetStatus("就绪", "先开启助手，再连接 VPN", "准备好VPN 内网目标 IP 后点击“开启助手”。分流期间可最小化到系统托盘。");
            Shown += delegate { if (!demo && (autoStart || recover)) BeginInvoke(new Action(delegate { StartWorker(recover?"Restore":"Start"); })); };
        }
        Label LabelAt(string text, int x, int y, int w, int h, float size, bool bold) { return new Label { Text = text, Bounds = new Rectangle(x,y,w,h), Font = new Font("Microsoft YaHei UI",size,bold?FontStyle.Bold:FontStyle.Regular), ForeColor = bold?ink:muted }; }
        void Style(Button b, bool primary) { b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = primary?accent:Color.FromArgb(214,224,228); b.FlatAppearance.BorderSize = 1; b.BackColor = primary?accent:Color.White; b.ForeColor = primary?Color.White:ink; b.Cursor = Cursors.Hand; b.Font = new Font(Font,FontStyle.Bold); }
        static Icon CreateIcon() { using(var b=new Bitmap(32,32)) using(var g=Graphics.FromImage(b)) { g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(Color.Transparent); using(var fill=new SolidBrush(Color.FromArgb(13,125,113))) g.FillEllipse(fill,1,1,30,30); using(var f=new Font("Segoe UI",18,FontStyle.Bold)) g.DrawString("G",f,Brushes.White,3,-1); var h=b.GetHicon(); try { return (Icon)Icon.FromHandle(h).Clone(); } finally { DestroyIcon(h); } } }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        void SetStatus(string tag,string title,string detail) { badge.Text = "●  " + tag; stateTitle.Text = title; stateDetail.Text = detail; tray.Text = ("Global Protector · " + tag).Substring(0,Math.Min(63,("Global Protector · " + tag).Length)); }
        void Append(string message) { log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine); }
        void LoadSettings() { try { string path=Path.Combine(Program.Root,"settings.json"); if(File.Exists(path)) { var d=Program.Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(path)); targets.Text=d.ContainsKey("targets")?d["targets"]:""; manualDns.Text=d.ContainsKey("dns")?d["dns"]:""; dnsMode.Checked=String.IsNullOrEmpty(manualDns.Text); autoOpen.Checked=!d.ContainsKey("autoOpen") || d["autoOpen"]=="true"; } } catch { Append("设置未读取，填写地址后即可开始。"); } }
        void SaveSettings(string[] list,string dns) { Directory.CreateDirectory(Program.Root); File.WriteAllText(Path.Combine(Program.Root,"settings.json"),Program.Json.Serialize(new Dictionary<string,string>{{"targets",String.Join(Environment.NewLine,list)},{"dns",dns},{"autoOpen",autoOpen.Checked?"true":"false"}}),Encoding.UTF8); }
        string ExtractEngine() { Directory.CreateDirectory(Program.Root); string path=Path.Combine(Program.Root,"engine-0.1.1.ps1"); using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Engine.ps1")) using(var reader=new StreamReader(stream,Encoding.UTF8)) File.WriteAllText(path,reader.ReadToEnd(),new UTF8Encoding(true)); return path; }
        void StartWorker(string mode, bool resume = false) {
            try {
                if (demo) { Append("预览模式仅展示界面，不执行网络修改。"); return; }
                if (running) return;
                string[] list=mode=="Start"?InputRules.Parse(targets.Text,false):new string[0]; string dns="";
                if(mode=="Start" && !dnsMode.Checked) { var parsed=InputRules.Parse(manualDns.Text,false); if(parsed.Length!=1) throw new ArgumentException("指定 DNS 只需填写一个 IPv4 地址。"); dns=parsed[0]; }
                if(mode=="Start") SaveSettings(list,dns);
                if(!Program.Admin) {
                    var elevated=new ProcessStartInfo(Application.ExecutablePath,mode=="Start"?"--autostart":"--recover") {UseShellExecute=true,Verb="runas"};
                    Process.Start(elevated); allowClose=true; Close(); return;
                }
                hadError=false; lastError=""; stopping=false; armed=mode=="Start"; if(!resume) openedVpn=false;
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
                SetStatus("检查中","正在检查当前网络","请稍候，确认后将提示你连接 GlobalProtect。"); worker.Start(); worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
            } catch(Exception e) { running=false; armed=false; start.Enabled=true; restore.Enabled=false; targets.Enabled=true; dnsMode.Enabled=true; manualDns.Enabled=!dnsMode.Checked; SetStatus("未开始","操作未完成",e.Message); Append(e.Message); }
        }
        void Dispatch(Action action) { try { if(!IsDisposed && IsHandleCreated) BeginInvoke(action); } catch(InvalidOperationException) {} }
        void OnEvent(string line) {
            try {
                var evt=Program.Json.Deserialize<Dictionary<string,object>>(line); string name=Convert.ToString(evt["Event"]); object data=evt.ContainsKey("Data")?evt["Data"]:null;
                if(name=="WAITING_FOR_VPN") { SetStatus("等待连接","现在可以连接 GlobalProtect","助手已在本机等待，即使连接时断网也会继续执行。"); Append("网络基线已保存，等待 VPN 连接。"); if(autoOpen.Checked && !openedVpn) { openedVpn=true; OpenVpn(); } }
                else if(name=="APPLIED_TEMPORARY_SPLIT") { SetStatus("验证中","分流已应用，正在验证","正在检查目标路由、普通上网和域名解析。"); Append("临时分流已应用，正在验证。"); }
                else if(name=="READY") { SetStatus("分流正常","VPN 与日常上网可以并行","请保留助手运行；断开 VPN 或点击“停止并恢复”会撤销临时改动。"); Append("路由、公网和 DNS 验证通过。"); tray.Visible=true; }
                else if(name=="ERROR" || name=="ROLLBACK_FAILED") { hadError=true; var d=data as Dictionary<string,object>; lastError=d!=null && d.ContainsKey("Message")?Convert.ToString(d["Message"]):Convert.ToString(data); Append(lastError); SetStatus("需要查看","分流未保持运行",Friendly(lastError)); }
                else if(name=="RESTORED") { Append("临时改动已恢复。"); SetStatus("已恢复","已恢复连接前的网络设置",hadError?Friendly(lastError):"下次使用时，先开启助手，再连接 VPN。"); }
                else if(name=="HEALTH") { Append("已完成一次网络健康检查。"); }
                else if(name=="DNS_AFTER_APPLY") { var d=data as Dictionary<string,object>; if(d!=null && d.ContainsKey("NameServer")) Append("VPN DNS："+Convert.ToString(d["NameServer"])); }
            } catch { Append(line); }
        }
        string Friendly(string error) {
            if(error.IndexOf("Disconnect GlobalProtect",StringComparison.OrdinalIgnoreCase)>=0) return "请先断开 GlobalProtect，再开启助手。";
            if(error.IndexOf("earlier session",StringComparison.OrdinalIgnoreCase)>=0) return "上次有未恢复的改动，请点击“停止并恢复”后重试。";
            if(error.IndexOf("default route",StringComparison.OrdinalIgnoreCase)>=0) return "VPN 的路由策略发生变化，助手已停止，请查看日志。";
            return error.Length>180?error.Substring(0,180)+"…":error;
        }
        void WorkerEnded() {
            running=false;
            bool pending=false;
            try { string path=Path.Combine(Program.Root,"state.json"); if(File.Exists(path)) { var saved=Program.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path)); pending=saved.ContainsKey("Stage") && Convert.ToString(saved["Stage"])!="restored"; } } catch { pending=true; }
            if(pending) { armed=false; quitWhenStopped=false; hadError=true; Show(); SetStatus("需要恢复","仍有网络快照等待恢复","请先断开 VPN，再点击“停止并恢复”。本机日志保留了详细原因。"); }
            if(Lifecycle.ShouldRearm(armed,stopping,hadError,pending)) {
                SetStatus("等待重连","VPN 已断开，助手继续待命","网络已恢复。下次连接 VPN 时，助手会重新应用分流，无需重启。");
                Append("本轮已恢复，继续等待下次 VPN 连接。");
                reconnectTimer=new System.Windows.Forms.Timer {Interval=1500};
                reconnectTimer.Tick += delegate { reconnectTimer.Stop(); reconnectTimer.Dispose(); reconnectTimer=null; if(armed && !stopping) StartWorker("Start",true); };
                reconnectTimer.Start(); return;
            }
            armed=false; start.Enabled=true; restore.Enabled=true; targets.Enabled=true; dnsMode.Enabled=true; manualDns.Enabled=!dnsMode.Checked;
            if(!hadError) SetStatus("已停止","助手已停止，本次分流已结束","重新使用时，请先断开 VPN，再开启助手。");
            else if(!pending) SetStatus("已停止","本次操作已结束",Friendly(lastError)+"  可查看本机日志。");
            if(quitWhenStopped && !pending) { allowClose=true; Close(); }
        }
        void StopWorker(bool quit) {
            quitWhenStopped=quit; armed=false; if(reconnectTimer!=null) {reconnectTimer.Stop(); reconnectTimer.Dispose(); reconnectTimer=null;}
            if(demo) { allowClose=true; if(quit) Close(); return; }
            if(running) { if(stopping) return; stopping=true; Directory.CreateDirectory(Program.Root); File.WriteAllText(Path.Combine(Program.Root,"stop.request"),DateTime.Now.ToString("o")); SetStatus("恢复中","正在撤销临时改动","请稍候，恢复记录会保存到本机日志。"); }
            else if(File.Exists(Path.Combine(Program.Root,"state.json"))) { StartWorker("Restore"); }
            else if(quit) { allowClose=true; Close(); }
        }
        void OpenLogs() { Directory.CreateDirectory(Program.Root); Process.Start("explorer.exe",Program.Root); }
        void OpenVpn() {
            try {
                string path=null;
                foreach(var p in Process.GetProcessesByName("PanGPA")) { try { path=p.MainModule.FileName; break; } catch {} }
                if(path==null) using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PanGPS")) { string image=key==null?null:Convert.ToString(key.GetValue("ImagePath")); if(!String.IsNullOrEmpty(image)) { image=Environment.ExpandEnvironmentVariables(image).Trim(); string service=image.StartsWith("\"")?image.Split('"')[1]:image; path=Path.Combine(Path.GetDirectoryName(service),"PanGPA.exe"); } }
                if(path==null || !File.Exists(path)) throw new FileNotFoundException("未找到 GlobalProtect，请从公司提供的安装包安装官方客户端。");
                Process.Start(path);
            } catch(Exception e) { Append(e.Message); }
        }
    }
}
