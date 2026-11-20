using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal static class Vietnam2222Trace {
    const int Seconds = 90;
#if LOCAL_EGRESS
    const string Share = @"C:\Users\Mock";
    const string Prefix = "VN2222-LocalTrace-";
    const string ExeName = "VN2222-LocalTrace.exe";
    const string ReportName = "vietnam-2222-local-trace.txt";
#elif INSTALLED_REMOTE_TRACE
    static readonly string Share = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VietnamWebRelay");
    const string Prefix = "VN2222-Trace-";
    const string ExeName = "VN2222-Trace.exe";
    const string ReportName = "vietnam-2222-trace.txt";
#else
    const string Share = @"\\tsclient\C\Users\Mock";
    const string Prefix = "VN2222-Trace-";
    const string ExeName = "VN2222-Trace.exe";
    const string ReportName = "vietnam-2222-trace.txt";
#endif
    static string Client = "10.192.29.122";
    const string Server = "10.213.134.31";
    static readonly string Folder = AppDomain.CurrentDomain.BaseDirectory;

    [STAThread]
    static int Main(string[] args) {
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
        string id = Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar));
        try {
#if !LOCAL_EGRESS
            if (!String.Equals(Environment.MachineName, "RHF-VPN", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请在目标远端电脑 RHF-VPN 运行。");
#endif
            if (args.Length == 0) return LaunchLocalCopy();
            if (!Regex.IsMatch(id, "^" + Prefix + "[a-f0-9]{32}$") || !IsAdmin())
                throw new InvalidOperationException("此测试需要以管理员权限启动。");
            if (args[0] == "--watch" && args.Length == 2) return Watch(id, Int32.Parse(args[1]));
            if (args.Length != 1 || args[0] != "--run") return 2;
#if !LOCAL_EGRESS
            if (!SystemInformation.TerminalServerSession) return 2;
#endif
            RunTrace(id);
            return 0;
        } catch (Exception e) {
            Report("ERROR", id, e.GetBaseException().Message);
            if (args.Length == 0 || args[0] != "--watch")
                MessageBox.Show(e.GetBaseException().Message, "越南端口抓包");
            return 1;
        }
    }

    static bool IsAdmin() {
        using (var identity = WindowsIdentity.GetCurrent())
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static int LaunchLocalCopy() {
        string directory = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, ExeName);
        File.Copy(Assembly.GetExecutingAssembly().Location, copy);
        using (var child = Process.Start(new ProcessStartInfo(copy, "--run") { UseShellExecute = true, Verb = "runas" })) { }
        return 0;
    }

    static void RunTrace(string id) {
#if INSTALLED_REMOTE_TRACE
        var peers = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
            .Where(endpoint => endpoint.State == TcpState.Established && endpoint.LocalEndPoint.Address.ToString() == Server &&
                endpoint.LocalEndPoint.Port == 3389 && endpoint.RemoteEndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(endpoint => endpoint.RemoteEndPoint.Address.ToString()).Distinct().ToArray();
        if (peers.Length != 1) throw new InvalidOperationException("请先连接当前远程桌面。只能在确定唯一 RDP 来源地址时启动抓包。");
        Client = peers[0];
#endif
        var expected = IPAddress.Parse(Client);
#if LOCAL_EGRESS
        var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(adapter =>
            adapter.OperationalStatus == OperationalStatus.Up && adapter.Description.IndexOf("PANGP", StringComparison.OrdinalIgnoreCase) >= 0 &&
            adapter.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(expected))).ToArray();
        if (adapters.Length != 1) throw new InvalidOperationException("找不到当前 VPN 地址对应的 GlobalProtect 虚拟网卡。抓包没有启动。");
#else
        if (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Any(endpoint =>
            endpoint.State == TcpState.Established && endpoint.LocalEndPoint.Address.ToString() == Server &&
            endpoint.LocalEndPoint.Port == 3389 && endpoint.RemoteEndPoint.Address.Equals(expected)))
            throw new InvalidOperationException("没找到来自本机 VPN 地址的现有 RDP 连接。抓包没有启动。");
#endif

        var status = Call("status");
        var filters = Call("filter list");
        if (!IsIdle(status) || !IsEmpty(filters))
            throw new InvalidOperationException("不能确认 Windows 抓包工具空闲，保留了现有设置。" + status.Text + " " + filters.Text);
        string token = id.Substring(id.Length - 12);
        using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\" + id))
        using (var parent = Process.GetCurrentProcess())
        using (var watcher = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--watch " + parent.Id) {
            UseShellExecute = false, CreateNoWindow = true
        })) {
            if (!ready.WaitOne(4000)) throw new InvalidOperationException("抓包回收程序没有启动。");
            try {
                AddFilter("vn2222_" + token, 2222);
                AddFilter("vn3389_" + token, 3389);
                string component = "nics";
#if LOCAL_EGRESS
                component = "all";
#endif
                Require(Call("start --capture --comp " + component + " --pkt-size 64 --file-size 2 --log-mode circular --file-name \"" + Path.Combine(Folder, "capture.etl") + "\""));
                File.WriteAllText(Path.Combine(Folder, "started"), id);
                var elapsed = Stopwatch.StartNew();
#if LOCAL_EGRESS
                Report("READY", id, "CLIENT=" + Client + " COMPONENT=" + component);
#else
                Report("READY", id, "CLIENT=" + Client);
#endif
                WaitForProbe(id, elapsed);
#if LOCAL_EGRESS
                var drops = Call("counters --type drop --include-hidden --drop-reason");
                Report("DROP_COUNTERS", id, drops.Text);
#endif
            } finally { Cleanup(id); }
        }
        string etl = Path.Combine(Folder, "capture.etl"), pcap = Path.Combine(Folder, "capture.pcapng");
        try {
            Require(Call("etl2pcap \"" + etl + "\" --out \"" + pcap + "\""));
            Counts counts;
            using (var stream = File.OpenRead(pcap)) counts = ReadCapture(stream);
            Report("RESULT", id, "SYN2222=" + counts.In2222 + " SYNACK2222=" + counts.Out2222 +
                " SYN3389=" + counts.In3389 + " SYNACK3389=" + counts.Out3389 + " IPV4=" + counts.Ipv4);
#if INSTALLED_REMOTE_TRACE
            MessageBox.Show("2222 握手请求：" + counts.In2222 + "\r\n3389 握手请求：" + counts.In3389 +
                "\r\n\r\n日志：" + Path.Combine(Share, ReportName), "越南端口抓包");
#endif
#if LOCAL_EGRESS
            var decoded = Path.Combine(Folder, "path.txt");
            var conversion = Call("etl2txt \"" + etl + "\" --out \"" + decoded + "\" --brief");
            if (conversion.Code == 0) Report("PACKET_PATH", id, decoded);
            else Report("PACKET_PATH_UNAVAILABLE", id, conversion.Text);
            Report("WFP", id, "OUTBOUND_BLOCK_2222=" + ReadOutboundBlocks());
#endif
        } finally { File.Delete(pcap); File.Delete(etl); }
    }

    static void WaitForProbe(string id, Stopwatch elapsed) {
        string stop = Path.Combine(Share, id + ".done");
        using (var form = new Form { Text = "越南端口抓包", ClientSize = new Size(430, 120),
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, StartPosition = FormStartPosition.CenterScreen })
        using (var label = new Label { Bounds = new Rectangle(20, 20, 390, 75) })
        using (var timer = new System.Windows.Forms.Timer { Interval = 1100 }) {
            form.Controls.Add(label);
            Action update = () => {
                int remaining = Math.Max(0, Seconds - (int)elapsed.Elapsed.TotalSeconds);
                label.Text = "已开始抓包，等待本机测试。\r\n" + remaining + " 秒后自动结束。";
                if (remaining == 0 || File.Exists(stop)) form.Close();
            };
            form.Shown += delegate { update(); if (!form.IsDisposed) timer.Start(); };
            timer.Tick += delegate { update(); };
            Application.Run(form);
        }
    }

    static int Watch(string id, int pid) {
        using (var ready = EventWaitHandle.OpenExisting(@"Local\" + id))
        using (var parent = Process.GetProcessById(pid)) {
            if (parent.Handle == IntPtr.Zero) return 2;
            ready.Set();
            parent.WaitForExit((Seconds + 45) * 1000);
        }
        Cleanup(id);
        return 0;
    }

    static void AddFilter(string name, int port) {
        Require(Call("filter add " + name + " -d IPv4 -t TCP SYN -i " + Client + " " + Server + " -p " + port));
        // Keep the exact state we installed. PktMon can only remove all filters.
        var installed = Call("filter list");
        Require(installed);
        File.WriteAllText(Path.Combine(Folder, "installed"), installed.Text.Trim());
    }

    static void Cleanup(string id) {
        string running = Path.Combine(Folder, "started");
        if (File.Exists(running)) {
            Require(Call("stop"));
            File.Delete(running);
            Report("STOPPED", id, "");
        }
        string snapshot = Path.Combine(Folder, "installed");
        if (!File.Exists(snapshot)) return;
        var filters = Call("filter list");
        if (filters.Code == 0 && String.Equals(filters.Text.Trim(), File.ReadAllText(snapshot), StringComparison.Ordinal)) {
            Require(Call("filter remove"));
            File.Delete(snapshot);
            Report("FILTERS_CLEANED", id, "");
        } else {
            Report("FILTERS_CHANGED", id, "现有抓包筛选器已变化，保留该设置。");
            File.Delete(snapshot);
        }
    }

    sealed class Command { internal int Code; internal string Text; }

    static Command Call(string arguments) {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pktmon.exe");
        using (var child = Process.Start(new ProcessStartInfo(exe, arguments) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
#if LOCAL_EGRESS
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
#endif
        })) {
            var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(12000)) { child.Kill(); throw new IOException("Windows 抓包工具命令超时。"); }
            child.WaitForExit();
            return new Command { Code = child.ExitCode, Text = (output.Result + " " + errors.Result).Trim() };
        }
    }

    static void Require(Command c) {
        if (c.Code != 0) throw new IOException("Windows 抓包工具返回错误 " + c.Code + "。" + c.Text);
    }

    static bool DriverNotLoaded(Command c) {
        return c.Code == 2 && Regex.IsMatch(c.Text, "cannot find the file specified|找不到指定的文件", RegexOptions.IgnoreCase);
    }

    static bool IsIdle(Command c) {
        return DriverNotLoaded(c) || c.Code == 0 && Regex.IsMatch(c.Text,
            @"\bnot running\b|\bstopped\b|未在运行|未运行|没有运行|已停止|未启动", RegexOptions.IgnoreCase);
    }

    static bool IsEmpty(Command c) {
        return DriverNotLoaded(c) || c.Code == 0 && (String.IsNullOrWhiteSpace(c.Text) || Regex.IsMatch(c.Text,
            @"\bno (?:active |packet )?filters\b|^\s*none\s*$|(?:没有|未配置|未设置|无)(?:任何|活动的?|可用的?)?(?:数据包|包)?筛选器|筛选器列表为空|^\s*(?:数据包筛选器[:：]\s*)?无[。.]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline));
    }

    static void Report(string stage, string id, string details) {
        string line = DateTimeOffset.Now.ToString("o") + " " + stage + " " + id + " " + details.Replace('\r', ' ').Replace('\n', ' ').Trim();
        try { File.AppendAllText(Path.Combine(Share, ReportName), line.TrimEnd() + Environment.NewLine, Encoding.UTF8); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
#if INSTALLED_REMOTE_TRACE
        try {
            const string redirected = @"\\tsclient\C\Users\Mock";
            if (Directory.Exists(redirected)) File.AppendAllText(Path.Combine(redirected, ReportName), line.TrimEnd() + Environment.NewLine, Encoding.UTF8);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
#endif
    }

#if LOCAL_EGRESS
    static string ReadOutboundBlocks() {
        try {
            var query = new EventLogQuery("Security", PathType.LogName,
                "*[System[(EventID=5152 or EventID=5157) and TimeCreated[timediff(@SystemTime)<=7200000]]] " +
                "and *[EventData[Data[@Name='DestAddress']='" + Server + "' and Data[@Name='DestPort']='2222']]");
            using (var reader = new EventLogReader(query))
            using (var record = reader.ReadEvent()) return record == null ? "NoMatchingAuditEvent" : "Recorded";
        } catch (Exception) { return "Unavailable"; }
    }
#endif

    sealed class Counts { internal int In2222, Out2222, In3389, Out3389, Ipv4; }

    static Counts ReadCapture(Stream stream) {
        var types = new List<ushort>(); var counts = new Counts();
        using (var reader = new BinaryReader(stream, Encoding.ASCII, true)) {
            while (stream.Position + 12 <= stream.Length) {
                uint type = reader.ReadUInt32(); int size = reader.ReadInt32();
                if (size < 12 || size > 1048576 || size - 8 > stream.Length - stream.Position)
                    throw new IOException("抓包文件格式或长度无效。");
                byte[] payload = reader.ReadBytes(size - 12);
                if (reader.ReadInt32() != size) throw new IOException("抓包记录不完整。");
                if (type == 0x0a0d0d0a) {
                    if (payload.Length < 4 || BitConverter.ToUInt32(payload, 0) != 0x1a2b3c4d) throw new IOException("无法解析抓包字节顺序。");
                    types.Clear();
                } else if (type == 1 && payload.Length >= 8) {
                    types.Add(BitConverter.ToUInt16(payload, 0));
                } else if (type == 6 && payload.Length >= 20) {
                    uint iface = BitConverter.ToUInt32(payload, 0); uint cap = BitConverter.ToUInt32(payload, 12);
                    if (iface < types.Count && cap <= payload.Length - 20) ReadPacket(payload, 20, (int)cap, types[(int)iface], counts);
                }
            }
        }
        return counts;
    }

    static int NetworkShort(byte[] b, int at) { return (b[at] << 8) | b[at + 1]; }

    static void ReadPacket(byte[] b, int at, int length, ushort link, Counts counts) {
        int end = at + length;
        if (link == 1) {
            if (length < 14) return;
            int etherType = NetworkShort(b, at + 12); at += 14;
            while ((etherType == 0x8100 || etherType == 0x88a8) && at + 4 <= end) { etherType = NetworkShort(b, at + 2); at += 4; }
            if (etherType != 0x0800) return;
        } else if (link != 101 && link != 228) return;
        if (at + 20 > end || b[at] >> 4 != 4 || b[at + 9] != 6 || (NetworkShort(b, at + 6) & 0x1fff) != 0) return;
        int header = (b[at] & 15) * 4; int tcp = at + header;
        if (header < 20 || tcp + 14 > end || (b[tcp + 13] & 2) == 0) return;
        counts.Ipv4++;
        string from = String.Join(".", b.Skip(at + 12).Take(4)); string to = String.Join(".", b.Skip(at + 16).Take(4));
        if (from == Client && to == Server && (b[tcp + 13] & 16) == 0) {
            int port = NetworkShort(b, tcp + 2); if (port == 2222) counts.In2222++; if (port == 3389) counts.In3389++;
        } else if (from == Server && to == Client && (b[tcp + 13] & 16) != 0) {
            int port = NetworkShort(b, tcp); if (port == 2222) counts.Out2222++; if (port == 3389) counts.Out3389++;
        }
    }

    static int SelfTest() {
        if (!IsIdle(new Command { Code=2, Text="系统找不到指定的文件。" }) ||
            !IsIdle(new Command { Code=0, Text="数据包监视器没有运行。" }) ||
            !IsEmpty(new Command { Code=0, Text="No packet filters are configured." }) ||
            !IsEmpty(new Command { Code=0, Text="数据包筛选器:      无" }) ||
            IsIdle(new Command { Code=0, Text="Status: Running" }) || IsEmpty(new Command {Code=0,Text="Other capture TCP 443"})) return 1;
        byte[] frame = new byte[62]; frame[12]=0x08; frame[13]=0x00; frame[14]=0x45; frame[23]=6;
        IPAddress.Parse(Client).GetAddressBytes().CopyTo(frame, 26); IPAddress.Parse(Server).GetAddressBytes().CopyTo(frame, 30);
        frame[36]=0x08; frame[37]=0xae; frame[47]=2;
        var counts = new Counts(); ReadPacket(frame, 0, frame.Length, 1, counts);
        return counts.In2222 == 1 && counts.In3389 == 0 && counts.Out2222 == 0 ? 0 : 2;
    }
}
