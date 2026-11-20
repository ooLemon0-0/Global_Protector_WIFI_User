using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Forms;

internal static class RemoteSetup {
    const string Product = "越南网页中转";
    const string WorkerName = "VietnamWebRelayBackground.exe";
    const string ShortcutName = "Vietnam Web Relay.lnk";
    const string TraceName = "VN2222-Trace.exe";
    const string TraceShortcut = "Vietnam Port Trace.lnk";
    const string RelayShortcut = "Vietnam Web Relay.lnk";
    const string InstallerFolder = @"C:\VNWeb";
    const string InstallerName = "VN-Web-Setup-v5.exe";

    [STAThread]
    static int Main(string[] args) {
        try {
            if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
            if (!SystemInformation.TerminalServerSession || !String.Equals(Environment.MachineName, "RHF-VPN", StringComparison.OrdinalIgnoreCase)) {
                MessageBox.Show("请在 RHF-VPN 的远程桌面内运行此安装程序。", Product, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }

            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VietnamWebRelay");
            string worker = Path.Combine(directory, WorkerName);
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (String.IsNullOrEmpty(startup) || !Directory.Exists(startup)) throw new IOException("找不到当前用户的启动文件夹。");
            string shortcut = Path.Combine(startup, ShortcutName);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!Directory.Exists(desktop)) throw new IOException("找不到当前用户的桌面文件夹。");
            string trace = Path.Combine(directory, TraceName);
            string startLink = Path.Combine(desktop, RelayShortcut);
            string traceLink = Path.Combine(desktop, TraceShortcut);
            VerifyShortcut(shortcut, worker);
            VerifyShortcut(startLink, worker);
            VerifyShortcut(traceLink, trace);
            if (args.Length == 1 && args[0] == "--disable") {
                File.Delete(shortcut);
                StopWorker(worker);
                File.Delete(startLink);
                File.Delete(traceLink);
                MessageBox.Show("后台启动已停用。", Product);
                return 0;
            }
            if (args.Length != 0) return 2;
            if (!File.Exists(shortcut) && MessageBox.Show("为当前远程桌面用户启用网页中转和桌面诊断快捷方式？", Product,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 0;
            Directory.CreateDirectory(directory);
            InstallResource(WorkerName, worker, () => StopWorker(worker));
            InstallResource(TraceName, trace, null);
            using (Process.Start(new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true })) { }
            InstallShortcuts(startup, desktop, worker, trace);
            string savedInstaller;
            try { savedInstaller = "安装包已保存在 " + SaveInstallerCopy(InstallerFolder); }
            catch (IOException error) { savedInstaller = "安装包无法保存到 " + InstallerFolder + "。" + error.Message; }
            catch (UnauthorizedAccessException) { savedInstaller = "当前用户没有写入 " + InstallerFolder + " 的权限。"; }
            MessageBox.Show("后台中转已启用，支持网站、文件接收和现场 SSH。请保持远程桌面连接。\r\n\r\n" + savedInstaller +
                "\r\n需要抓包时，双击远端桌面的 Vietnam Port Trace。", Product);
            return 0;
        } catch (Exception error) {
            MessageBox.Show(error.GetBaseException().Message, Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static string SaveInstallerCopy(string directory) {
        string source = Assembly.GetExecutingAssembly().Location;
        string destination = Path.Combine(directory, InstallerName);
        if (String.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return destination;
        Directory.CreateDirectory(directory);
        if (File.Exists(destination) && File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(destination))) return destination;
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".new");
        try {
            File.Copy(source, temporary);
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            return destination;
        } finally { File.Delete(temporary); }
    }

    static void InstallResource(string name, string path, Action beforeReplace) {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try {
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) {
                if (source == null) throw new IOException("安装包缺少文件 " + name);
                using (var target = File.Create(temporary)) source.CopyTo(target);
            }
            if (File.Exists(path)) {
                if (File.ReadAllBytes(temporary).SequenceEqual(File.ReadAllBytes(path))) return;
                if (beforeReplace != null) beforeReplace();
                File.Replace(temporary, path, null);
            }
            else File.Move(temporary, path);
        } finally { File.Delete(temporary); }
    }

    static void VerifyShortcut(string path, string target) {
        if (File.Exists(path) && !String.Equals(ReadShortcut(path), target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("已有同名的其他快捷方式 " + Path.GetFileName(path));
    }

    static void InstallShortcuts(string startup, string desktop, string worker, string trace) {
        SaveShortcut(Path.Combine(startup, ShortcutName), worker, "Start the Vietnam web relay");
        SaveShortcut(Path.Combine(desktop, RelayShortcut), worker, "Start the Vietnam web relay");
        SaveShortcut(Path.Combine(desktop, TraceShortcut), trace, "Capture Vietnam TCP handshakes on demand");
    }

    static ShellLink CreateShellLink() {
        return (ShellLink)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
    }

    static void SaveShortcut(string path, string target, string description) {
        var link = CreateShellLink();
        try {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target));
            link.SetDescription(description);
            ((IPersistFile)link).Save(Path.GetFullPath(path), false);
        } catch (COMException error) {
            throw new IOException("无法保存快捷方式 " + path + "\r\n" + error.Message);
        } finally { Marshal.FinalReleaseComObject(link); }
    }

    static string ReadShortcut(string path) {
        var link = CreateShellLink();
        try {
            ((IPersistFile)link).Load(Path.GetFullPath(path), 0);
            var target = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            return target.ToString();
        } finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ShellLink {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr item);
        void SetIDList(IntPtr item);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out ushort key);
        void SetHotkey(ushort key);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    static void StopWorker(string path) {
        int session;
        using (var current = Process.GetCurrentProcess()) session = current.SessionId;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WorkerName))) {
            using (process) {
                if (process.SessionId != session || process.HasExited) continue;
                if (!String.Equals(process.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill();
                if (!process.WaitForExit(5000)) throw new IOException("后台中转未能退出。");
            }
        }
    }

    static int SelfTest() {
        string directory = Path.Combine(Path.GetTempPath(), "VietnamWebRelayTest-" + Guid.NewGuid().ToString("N"));
        string desktop = Path.Combine(directory, "桌面");
        string startup = Path.Combine(directory, "启动");
        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(startup);
        string worker = Path.Combine(directory, WorkerName);
        string trace = Path.Combine(directory, TraceName);
        string startupLink = Path.Combine(startup, ShortcutName);
        string relayLink = Path.Combine(desktop, RelayShortcut);
        string traceLink = Path.Combine(desktop, TraceShortcut);
        string savedInstaller = Path.Combine(desktop, InstallerName);
        try {
            InstallResource(WorkerName, worker, null);
            InstallResource(WorkerName, worker, () => { throw new InvalidOperationException("Identical worker was replaced."); });
            InstallResource(TraceName, trace, null);
            // Reproduce the previous install reaching startup registration before desktop shortcut creation.
            SaveShortcut(startupLink, worker, "Existing startup entry");
            InstallShortcuts(startup, desktop, worker, trace);
            InstallShortcuts(startup, desktop, worker, trace);
            SaveInstallerCopy(desktop);
            SaveInstallerCopy(desktop);
            return String.Equals(ReadShortcut(startupLink), worker, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(ReadShortcut(relayLink), worker, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(ReadShortcut(traceLink), trace, StringComparison.OrdinalIgnoreCase) &&
                File.ReadAllBytes(savedInstaller).SequenceEqual(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location)) ? 0 : 1;
        } finally {
            File.Delete(startupLink);
            File.Delete(relayLink);
            File.Delete(traceLink);
            File.Delete(savedInstaller);
            File.Delete(worker);
            File.Delete(trace);
            Directory.Delete(startup);
            Directory.Delete(desktop);
            Directory.Delete(directory);
        }
    }
}
