using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// This test uses two local message pipes and a synthetic loopback web server.
// It never opens the real RDP host or the internal web service.
internal static class SmokeTest {
    static void Serve(TcpListener listener, int requests) {
        for (int i = 0; i < requests; i++) {
            var socket = listener.AcceptTcpClient();
            Console.WriteLine("FAKE WEB accepted");
            Task.Run(() => {
                using (socket) {
                    socket.ReceiveTimeout = 5000;
                    var stream = socket.GetStream();
                    var bytes = new byte[4096];
                    var request = new StringBuilder();
                    while (!request.ToString().Contains("\r\n\r\n")) {
                        int count = stream.Read(bytes, 0, bytes.Length);
                        if (count == 0) return;
                        request.Append(Encoding.ASCII.GetString(bytes, 0, count));
                    }
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 262144\r\nConnection: close\r\n\r\n");
                    Console.WriteLine("FAKE WEB request received");
                    stream.Write(header, 0, header.Length);
                    var body = new byte[4096];
                    for (int at = 0; at < body.Length; at++) body[at] = 90;
                    for (int block = 0; block < 64; block++) stream.Write(body, 0, body.Length);
                }
            });
        }
    }

    static void Fetch(int port) {
        var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/");
        request.Proxy = null;
        request.Timeout = 5000;
        request.ReadWriteTimeout = 5000;
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var stream = response.GetResponseStream()) {
            if (response.StatusCode != HttpStatusCode.OK) throw new Exception("HTTP status mismatch.");
            int total = 0;
            var bytes = new byte[4096];
            int count;
            while ((count = stream.Read(bytes, 0, bytes.Length)) > 0) {
                total += count;
                for (int at = 0; at < count; at++) if (bytes[at] != 90) throw new Exception("Relayed byte differs.");
            }
            if (total != 262144) throw new Exception("Incomplete response: " + total);
        }
    }

    static void Run() {
        string name = "WebRelayTest-" + Guid.NewGuid().ToString("N");
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        int targetPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        int localPort = FreePort();
        Task.Run(() => Serve(upstream, 4));
        using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous))
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)) {
            var connect = Task.Run(() => client.Connect(3000));
            server.WaitForConnection();
            connect.Wait();
            client.ReadMode = PipeTransmissionMode.Message;
            using (var local = new Tunnel(server, server, 0, true, null, 0, localPort, FreePort()))
            using (var remote = new Tunnel(client, client, 0, false, IPAddress.Loopback, targetPort, 0)) {
                var localTask = Task.Factory.StartNew(() => { try { local.Run(); } catch (Exception e) { Console.WriteLine("LOCAL LOOP " + e); } }, TaskCreationOptions.LongRunning);
                var remoteTask = Task.Factory.StartNew(() => { try { remote.Run(); } catch (Exception e) { Console.WriteLine("REMOTE LOOP " + e); } }, TaskCreationOptions.LongRunning);
                try {
                    Exception failure = null;
                    for (int attempt = 0; attempt < 20; attempt++) {
                        try { Fetch(localPort); failure = null; break; }
                        catch (WebException error) { failure = error; Thread.Sleep(100); }
                    }
                    if (failure != null) throw failure;
                    var checks = new[] { Task.Run(() => Fetch(localPort)), Task.Run(() => Fetch(localPort)), Task.Run(() => Fetch(localPort)) };
                    if (!Task.WaitAll(checks, 12000)) throw new Exception("Concurrent response timeout.");
                    Console.WriteLine("PASS: a legacy peer ignored negotiation and still delivered four HTTP responses intact.");
                } finally {
                    upstream.Stop();
                    local.Dispose();
                    remote.Dispose();
                    server.Dispose();
                    client.Dispose();
                    Task.WaitAll(new[] {localTask, remoteTask}, 3000);
                }
            }
        }
    }

    static void VerifyReconnect() {
        string name = "WebRelayReconnect-" + Guid.NewGuid().ToString("N");
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        int targetPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start();
        int localPort = ((IPEndPoint)reserve.LocalEndpoint).Port;
        reserve.Stop();
        Task.Run(() => Serve(upstream, 2));
        using (var ready = new AutoResetEvent(false)) {
            int attempts = 0;
            var listener = Task.Factory.StartNew(() => WebRelay.RunLocal(localPort, () => {
                var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                ready.Set();
                return pipe;
            }, () => ++attempts <= 2), TaskCreationOptions.LongRunning);
            try {
                for (int i = 0; i < 2; i++) {
                    if (!ready.WaitOne(4000)) throw new Exception("Local relay did not accept a new RDP session.");
                    using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                        pipe.Connect(3000);
                        pipe.ReadMode = PipeTransmissionMode.Message;
                        using (var remote = new Tunnel(pipe, pipe, 0, false, IPAddress.Loopback, targetPort, 0)) {
                            var work = Task.Factory.StartNew(() => {
                                try { remote.Run(); } catch (IOException) { } catch (ObjectDisposedException) { }
                            }, TaskCreationOptions.LongRunning);
                            try {
                                for (int attempt = 0; ; attempt++) {
                                    try { Fetch(localPort); break; }
                                    catch (WebException) { if (attempt == 19) throw; Thread.Sleep(100); }
                                }
                            } finally {
                                pipe.Dispose();
                                if (!work.Wait(3000)) throw new Exception("Old remote channel did not close.");
                            }
                        }
                    }
                }
                if (!listener.Wait(3000)) throw new Exception("Local relay did not release the last channel.");
                Console.WriteLine("PASS: the same local relay served HTTP across two separate pipe sessions.");
            } finally { upstream.Stop(); }
        }
    }

    static int FreePort() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    sealed class UploadReadGate : Stream {
        readonly Stream input;
        readonly ManualResetEventSlim started = new ManualResetEventSlim(), release = new ManualResetEventSlim();
        bool paused;
        internal UploadReadGate(Stream input) { this.input = input; }
        internal bool WaitForUpload() { return started.Wait(4000); }
        internal void ResumeUpload() { release.Set(); }
        public override int Read(byte[] buffer, int offset, int count) {
            int read = input.Read(buffer, offset, count);
            if (!paused && read > 5 && buffer[offset] == (byte)Message.UploadBytes) {
                paused = true;
                started.Set();
                if (!release.Wait(6000)) throw new IOException("Slow receiver gate timeout.");
            }
            return read;
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long length) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) {
            if (disposing) { started.Dispose(); release.Dispose(); }
            base.Dispose(disposing);
        }
    }

    static void VerifyExtendedRelay() {
        string name = "VietnamTransferTest-" + Guid.NewGuid().ToString("N");
        string testTransferPipe = name + "-upload";
        string directory = Path.Combine(Path.GetTempPath(), name);
        string inbox = Path.Combine(directory, "inbox");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "测试.bin");
        var data = Enumerable.Range(0, 2 * 1024 * 1024 + 123).Select(i => (byte)(i % 79)).ToArray();
        File.WriteAllBytes(file, data);
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        var extraUpstream = new TcpListener(IPAddress.Loopback, 0);
        var sshUpstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start(); extraUpstream.Start(); sshUpstream.Start();
        Task.Run(() => Serve(upstream, 1)); Task.Run(() => Serve(extraUpstream, 1));
        var sshPeer = Task.Run(() => {
            using (var peer = sshUpstream.AcceptTcpClient()) {
                peer.ReceiveTimeout = 3000;
                var stream = peer.GetStream();
                byte[] banner = Encoding.ASCII.GetBytes("SSH-2.0-RelayTest\r\n");
                stream.Write(banner,0,banner.Length);
                if (stream.ReadByte() != 42) throw new IOException("SSH client bytes reached the wrong destination.");
                stream.WriteByte(43);
            }
        });
        int local = FreePort(), extraLocal = FreePort(), sshLocal = FreePort();
        using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous))
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)) {
            var connect = Task.Run(() => client.Connect(3000));
            server.WaitForConnection(); connect.Wait(); client.ReadMode = PipeTransmissionMode.Message;
            using (var slowReceiver = new UploadReadGate(client))
            using (var left = new Tunnel(server, server, 0, true, null, 0, local, extraLocal, sshPort: sshLocal, transferPipeName: testTransferPipe))
            using (var right = new Tunnel(slowReceiver, client, 0, false, IPAddress.Loopback,
                ((IPEndPoint)upstream.LocalEndpoint).Port, 0, ((IPEndPoint)extraUpstream.LocalEndpoint).Port, inbox,
                ((IPEndPoint)sshUpstream.LocalEndpoint).Port)) {
                var serverTask = Task.Factory.StartNew(() => { try { left.Run(); } catch (IOException) { } catch (ObjectDisposedException) { } catch (OperationCanceledException) { } }, TaskCreationOptions.LongRunning);
                var clientTask = Task.Factory.StartNew(() => { try { right.Run(); } catch (IOException) { } catch (ObjectDisposedException) { } catch (OperationCanceledException) { } }, TaskCreationOptions.LongRunning);
                try {
                    int pid;
                    using (var process = System.Diagnostics.Process.GetCurrentProcess()) pid = process.Id;
                    try { VietnamTransfer.Send(file, id => false, testTransferPipe); throw new InvalidOperationException("Unverified pipe accepted."); }
                    catch (IOException) { }
                    var received = new System.Collections.Concurrent.ConcurrentQueue<long>();
                    var upload = Task.Run(() => VietnamTransfer.Send(file, id => id == pid, testTransferPipe, (confirmed,total) => {
                        if (total != data.Length) throw new IOException("Wrong upload size in progress.");
                        received.Enqueue(confirmed);
                    }));
                    try {
                        if (!slowReceiver.WaitForUpload() || !SpinWait.SpinUntil(() => !received.IsEmpty, 3000)) {
                            if (upload.IsFaulted) upload.GetAwaiter().GetResult();
                            throw new IOException("Receiver did not reach the paused transfer.");
                        }
                        Thread.Sleep(200);
                        if (upload.IsCompleted || received.ToArray().Any(n => n != 0))
                            throw new IOException("Progress advanced while the remote Inbox was blocked.");
                    } finally { slowReceiver.ResumeUpload(); }
                    string first = upload.GetAwaiter().GetResult();
                    long[] progress = received.ToArray();
                    if (progress.Length < 3 || progress[0] != 0 || progress.Last() != data.Length ||
                        !progress.SequenceEqual(progress.OrderBy(n => n)) || !progress.Any(n => n > 0 && n < data.Length))
                        throw new IOException("Inbox acknowledgements did not cover the upload in order.");
                    string second = VietnamTransfer.Send(file, id => id == pid, testTransferPipe);
                    if (first != "测试.bin" || second != "测试 (1).bin" ||
                        !File.ReadAllBytes(Path.Combine(inbox, first)).SequenceEqual(data) ||
                        !File.ReadAllBytes(Path.Combine(inbox, second)).SequenceEqual(data)) throw new IOException("Incorrect saved files.");
                    Fetch(local); Fetch(extraLocal);
                    using (var socket = new TcpClient("127.0.0.1",sshLocal)) {
                        socket.ReceiveTimeout = 3000;
                        var stream = socket.GetStream();
                        var banner = new byte[19];
                        int count = 0;
                        while (count < banner.Length) {
                            int read = stream.Read(banner,count,banner.Length-count);
                            if (read == 0) throw new IOException("SSH banner was incomplete.");
                            count += read;
                        }
                        if (Encoding.ASCII.GetString(banner) != "SSH-2.0-RelayTest\r\n") throw new IOException("Wrong SSH banner.");
                        stream.WriteByte(42);
                        if (stream.ReadByte() != 43 || !sshPeer.Wait(3000)) throw new IOException("SSH data did not round trip.");
                    }
                    try { VietnamTransfer.Header("..\\test.txt", 1); throw new InvalidOperationException("Traversal was accepted."); }
                    catch (IOException) { }
                    using (var partial = new VietnamTransfer.Receiver(inbox, VietnamTransfer.Header("incomplete.bin", 5))) {
                        partial.Write(new byte[3]);
                        try { partial.Commit(); throw new InvalidOperationException("Incomplete file was saved."); }
                        catch (IOException) { }
                    }
                    if (Directory.GetFiles(inbox).Length != 2) throw new IOException("Incomplete transfer left a file behind.");
                    Console.WriteLine("PASS: remote Inbox progress stays at zero while the receiver is paused, advances through confirmed bytes and completes after save.");
                    Console.WriteLine("PASS: both web ports, SSH banner and round-trip bytes, Unicode file names, matching data, collision handling and incomplete-file cleanup.");
                } finally {
                    upstream.Stop(); extraUpstream.Stop(); sshUpstream.Stop(); left.Dispose(); right.Dispose(); server.Dispose(); client.Dispose();
                    Task.WaitAll(new[] {serverTask, clientTask}, 3000);
                    if (Directory.Exists(inbox)) {
                        foreach (string item in Directory.GetFiles(inbox)) File.Delete(item);
                        Directory.Delete(inbox, true);
                    }
                    File.Delete(file); Directory.Delete(directory);
                }
            }
        }
    }

    static void WithUploadSession(string inbox, string transferPipe, Action<NamedPipeClientStream> check) {
        string name = transferPipe + "-" + Guid.NewGuid().ToString("N");
        using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous))
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)) {
            var connect = Task.Run(() => client.Connect(3000));
            server.WaitForConnection(); connect.GetAwaiter().GetResult(); client.ReadMode = PipeTransmissionMode.Message;
            using (var local = new Tunnel(server, server, 0, true, null, 0, FreePort(), FreePort(), transferPipeName: transferPipe))
            using (var remote = new Tunnel(client, client, 0, false, IPAddress.Loopback, 0, 0, 1, inbox)) {
                var left = Task.Factory.StartNew(() => RunUploadSession(local), TaskCreationOptions.LongRunning);
                var right = Task.Factory.StartNew(() => RunUploadSession(remote), TaskCreationOptions.LongRunning);
                try { check(client); }
                finally {
                    client.Dispose(); server.Dispose();
                    if (!Task.WaitAll(new[] {left,right}, 3000)) throw new IOException("Upload test session did not close.");
                }
            }
        }
    }

    static void RunUploadSession(Tunnel relay) {
        using (relay) {
            try { relay.Run(); }
            catch (IOException) { } catch (ObjectDisposedException) { } catch (OperationCanceledException) { }
        }
    }

    static void VerifyUploadResume() {
        string name = "VietnamResumeTest-" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), name);
        string inbox = Path.Combine(directory,"inbox"), sender = Path.Combine(directory,"sender");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory,"resume.bin");
        byte[] data = Enumerable.Range(0, 4 * 1024 * 1024 + 19).Select(i => (byte)(i % 93)).ToArray();
        File.WriteAllBytes(file, data);
        int pid;
        using (var process = System.Diagnostics.Process.GetCurrentProcess()) pid = process.Id;
        long pausedAt = 0, disconnectedAt = 0;
        try {
            WithUploadSession(inbox, name, client => {
                using (var cancel = new CancellationTokenSource()) {
                    try {
                        VietnamTransfer.Send(file, id => id == pid, name, (received,total) => {
                            if (received >= 128 * 1024 && pausedAt == 0) { pausedAt = received; cancel.Cancel(); }
                        }, cancel.Token, sender);
                        throw new InvalidOperationException("Pause did not stop the upload.");
                    } catch (OperationCanceledException) { }
                }
                if (pausedAt <= 0 || pausedAt >= data.Length || File.Exists(Path.Combine(inbox,"resume.bin")))
                    throw new IOException("The paused upload did not remain partial.");
                long firstOffset = -1;
                try {
                    VietnamTransfer.Send(file, id => id == pid, name, (received,total) => {
                        if (firstOffset < 0) firstOffset = received;
                        if (received > firstOffset && disconnectedAt == 0) { disconnectedAt = received; client.Dispose(); }
                    }, resumeDirectory: sender);
                    throw new InvalidOperationException("Closing the remote transport did not interrupt the upload.");
                } catch (VietnamTransfer.ConnectionInterrupted) { }
                if (firstOffset < pausedAt || disconnectedAt <= firstOffset || disconnectedAt >= data.Length)
                    throw new IOException("Upload did not resume after pause or did not reach the disconnect point.");
            });
            WithUploadSession(inbox, name, client => {
                string identityPath = Directory.GetFiles(sender,"*.id").Single();
                string identity = File.ReadAllText(identityPath);
                long firstOffset = -1;
                string saved = VietnamTransfer.Send(file, id => id == pid, name, (received,total) => {
                    if (firstOffset < 0) firstOffset = received;
                }, resumeDirectory: sender);
                if (firstOffset < disconnectedAt || firstOffset >= data.Length || File.Exists(identityPath) ||
                    saved != "resume.bin" || !File.ReadAllBytes(Path.Combine(inbox,saved)).SequenceEqual(data))
                    throw new IOException("The reconnect did not resume from the confirmed Inbox offset and save the original data.");
                File.WriteAllText(identityPath,identity);
                if (VietnamTransfer.Send(file, id => id == pid, name, resumeDirectory: sender) != saved ||
                    Directory.GetFiles(inbox).Length != 1)
                    throw new IOException("A retried save confirmation duplicated the finished file.");
            });
            Console.WriteLine("PASS: pause, disconnect, Inbox-offset resume, byte-for-byte completion and duplicate-free save confirmation.");
        } finally { Directory.Delete(directory, true); }
    }

    static int Main() {
        var deadline = new Timer(_ => { Console.Error.WriteLine("Test exceeded 20 seconds."); Environment.Exit(2); }, null, 20000, Timeout.Infinite);
        try { Run(); VerifyReconnect(); VerifyExtendedRelay(); VerifyUploadResume(); VerifySilentUploadPipe(); deadline.Dispose(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void VerifySilentUploadPipe() {
        string name = "VietnamSilentPipe-" + Guid.NewGuid().ToString("N");
        string file = Path.GetTempFileName();
        try {
            using(var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)) {
                var upload = Task.Run(() => {
                    try { VietnamTransfer.Send(file, id => true, name, replyTimeout: 250); return false; }
                    catch (VietnamTransfer.ConnectionInterrupted) { return true; }
                });
                server.WaitForConnection();
                if (!upload.Wait(3000) || !upload.Result) throw new IOException("A silent transfer pipe did not trigger retry.");
            }
            Console.WriteLine("PASS: a transfer pipe that stops responding times out and can be retried.");
        } finally { File.Delete(file); }
    }
}
