using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GlobalProtectorWiFi {
    internal sealed class VietnamWebSession {
        const string Portal = "113.160.245.25";
        const string Launcher = "Connect-Vietnam.ps1";
        const string Prefix = "VietnamWeb.";
        static readonly Assembly ApplicationAssembly = typeof(VietnamWebSession).Assembly;
        internal static readonly bool Bundled = ApplicationAssembly.GetManifestResourceInfo(Prefix + Launcher) != null;
        internal const string RemoteInstaller = "VN-Web-Setup-v5.exe";
        internal static readonly bool CanSaveInstaller = ApplicationAssembly.GetManifestResourceInfo(Prefix + RemoteInstaller) != null;
        Process connector;
        bool started;

        internal static bool AppliesTo(string configuredPortal, string observedPortal, string targets) {
            try {
                return LoginRules.PortalKey(configuredPortal) == Portal &&
                    (String.IsNullOrWhiteSpace(observedPortal) || LoginRules.PortalKey(observedPortal) == Portal) &&
                    InputRules.Parse(targets, false).Contains(Program.DefaultTarget);
            } catch (ArgumentException) { return false; }
        }

        internal void Start(string portal, string targets, bool retry, string mode, Action<string> log, Action<Action> dispatch) {
            if (!Bundled || (started && !retry)) return;
            try {
                if (!AppliesTo(portal, ClientIdentity.ActivePortal(), targets)) return;
                Stop();
                started = true;
                if (!String.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "AMD64", StringComparison.OrdinalIgnoreCase)) {
                    log("越南网页中转需要 Windows x64。");
                    return;
                }
                var info = LauncherInfo(PrepareLauncher(), mode);
                var process = new Process { StartInfo = info };
                DataReceivedEventHandler receive = (sender, args) => {
                    if (!String.IsNullOrWhiteSpace(args.Data))
                        dispatch(() => { if (connector == process) log(args.Data); });
                };
                process.OutputDataReceived += receive;
                process.ErrorDataReceived += receive;
                connector = process;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            } catch (Exception error) {
                Stop();
                started = true;
                log("越南网页中转未启动：" + error.Message);
            }
        }

        static string PrepareLauncher() {
            string directory = Path.Combine(Program.Root, "VietnamWeb", UpdateChecker.VersionText);
            Directory.CreateDirectory(directory);
            foreach (string resource in ApplicationAssembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal) && name != Prefix + RemoteInstaller))
                Extract(resource, Path.Combine(directory, resource.Substring(Prefix.Length)));
            return Path.Combine(directory, Launcher);
        }

        static ProcessStartInfo LauncherInfo(string launcher, string mode) {
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            return new ProcessStartInfo(shell, "-NoProfile -NonInteractive -Sta -ExecutionPolicy Bypass -File \"" + launcher + "\" -Mode " + mode) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
        }

        internal Task OpenFileWindow() {
            // Launch via the interactive Explorer token so ordinary Explorer can drop files into the window.
            var info = LauncherInfo(PrepareLauncher(), "FileTransfer");
            info.Arguments += " -TransferApp \"" + ApplicationAssembly.Location + "\"";
            return Task.Run(() => {
                using (var process = Process.Start(info)) {
                    var errors = process.StandardError.ReadToEndAsync();
                    string result = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit();
                    if (process.ExitCode != 0) throw new IOException(String.IsNullOrWhiteSpace(result)?"无法打开传文件窗口。"+errors.Result.Trim():result);
                }
            });
        }

        internal static void SaveRemoteInstaller(string destination) {
            Extract(Prefix + RemoteInstaller, destination);
        }

        internal static Task<string> ReconnectFileTransfer(CancellationToken cancellation) {
            return Task.Run(() => {
                cancellation.ThrowIfCancellationRequested();
                string launcher = PrepareLauncher();
                cancellation.ThrowIfCancellationRequested();
                using (var process = Process.Start(LauncherInfo(launcher, "ResumeTransfer")))
                using (cancellation.Register(() => StopProcess(process))) {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var errors = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(15000)) {
                        StopProcess(process);
                        return "正在等待远程桌面恢复…";
                    }
                    cancellation.ThrowIfCancellationRequested();
                    string message = output.GetAwaiter().GetResult().Trim();
                    return String.IsNullOrEmpty(message) && process.ExitCode != 0 ? errors.GetAwaiter().GetResult().Trim() : message;
                }
            }, cancellation);
        }

        [DllImport("kernel32.dll", SetLastError=true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        static string QueryImagePath(int processId) {
            const uint QueryLimitedInformation = 0x1000;
            IntPtr handle = OpenProcess(QueryLimitedInformation,false,processId);
            if (handle == IntPtr.Zero) return null;
            try {
                var path = new StringBuilder(32768);
                int size = path.Capacity;
                return QueryFullProcessImageName(handle,0,path,ref size)?path.ToString():null;
            } finally { CloseHandle(handle); }
        }

        internal static string SendFile(string path, Action<long,long> progress, CancellationToken cancellation) {
            return VietnamTransfer.Send(path, id => {
                try {
                    using (var process = Process.GetProcessById(id))
                    using (var current = Process.GetCurrentProcess()) {
                        if (process.SessionId != current.SessionId || process.ProcessName != "LocalRelay") return false;
                        string actualPath = QueryImagePath(id);
                        if (String.IsNullOrEmpty(actualPath)) return false;
                        using (var expected = ApplicationAssembly.GetManifestResourceStream(Prefix + "LocalRelay.exe"))
                        using (var actual = File.OpenRead(actualPath))
                        using (var hash = System.Security.Cryptography.SHA256.Create())
                            return hash.ComputeHash(expected).SequenceEqual(hash.ComputeHash(actual));
                    }
                } catch (ArgumentException) { return false; }
                catch (IOException) { return false; }
                catch (System.ComponentModel.Win32Exception) { return false; }
            }, progress: progress, cancellation: cancellation, resumeDirectory: Path.Combine(Program.Root, "TransferResume"));
        }

        internal void Stop() {
            started = false;
            var process = connector;
            connector = null;
            if (process == null) return;
            try { StopProcess(process); } finally { process.Dispose(); }
        }

        static void StopProcess(Process process) {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        static void Extract(string resource, string destination) {
            byte[] bytes;
            using (var source = ApplicationAssembly.GetManifestResourceStream(resource))
            using (var buffer = new MemoryStream()) {
                source.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            if (File.Exists(destination) && File.ReadAllBytes(destination).SequenceEqual(bytes)) return;
            string temp = destination + "." + Guid.NewGuid().ToString("N") + ".new";
            try {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(destination)) File.Replace(temp, destination, null);
                else File.Move(temp, destination);
            } finally { File.Delete(temp); }
        }
    }
}
