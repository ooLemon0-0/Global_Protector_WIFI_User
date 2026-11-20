using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal enum Message : byte { Ready, Open, Bytes, Finish, Abort, OpenDefaultWeb, Upload, UploadBytes, UploadFinish, UploadResult, UploadCancel, OpenSsh, UploadProgress, UploadResume, UploadOffset }

internal sealed class ChannelStream : Stream {
    readonly IntPtr handle;
    readonly bool writing;
    [StructLayout(LayoutKind.Sequential)] struct Operation { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateEvent(IntPtr security, bool manualReset, bool initial, IntPtr name);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] static extern unsafe bool ReadFile(IntPtr handle, byte* buffer, uint length, IntPtr transferred, Operation* operation);
    [DllImport("kernel32.dll", SetLastError=true)] static extern unsafe bool WriteFile(IntPtr handle, byte* buffer, uint length, IntPtr transferred, Operation* operation);
    [DllImport("kernel32.dll", SetLastError=true)] static extern unsafe bool GetOverlappedResult(IntPtr handle, Operation* operation, out uint bytes, bool wait);
    [DllImport("kernel32.dll", SetLastError=true)] static extern unsafe bool CancelIoEx(IntPtr handle, Operation* operation);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    internal ChannelStream(IntPtr handle, bool writing) { this.handle = handle; this.writing = writing; }
    public override bool CanRead { get { return !writing; } }
    public override bool CanWrite { get { return writing; } }
    public override bool CanSeek { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
    public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) { return Transfer(buffer, offset, count); }
    public override void Write(byte[] buffer, int offset, int count) { if (Transfer(buffer, offset, count) != count) throw new IOException("Short RDP channel write."); }

    unsafe int Transfer(byte[] buffer, int offset, int count) {
        IntPtr signal = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
        if (signal == IntPtr.Zero) throw new IOException("Unable to allocate an RDP channel I/O event.");
        try {
            var operation = new Operation { Event = signal };
            fixed (byte* start = &buffer[offset]) {
                bool started = writing ? WriteFile(handle, start, (uint)count, IntPtr.Zero, &operation)
                                       : ReadFile(handle, start, (uint)count, IntPtr.Zero, &operation);
                int error = Marshal.GetLastWin32Error();
                if (!started && error != 997) throw new IOException("RDP channel I/O error " + error);
                if (writing && !started && WaitForSingleObject(signal, 10000) != 0) CancelIoEx(handle, &operation);
                uint transferred;
                if (!GetOverlappedResult(handle, &operation, out transferred, true)) throw new IOException("RDP channel I/O error " + Marshal.GetLastWin32Error());
                return (int)transferred;
            }
        } finally { CloseHandle(signal); }
    }
}

internal sealed class Tunnel : IDisposable {
    [System.Diagnostics.Conditional("TESTTRACE")]
    void Trace(string stage) { Console.WriteLine((isLocal ? "LOCAL " : "REMOTE ") + stage); }
    const int MaxFrame = 1200;
    const int MaxConnections = 32;
    const int CapabilityId = Int32.MaxValue;
    const int UploadWindow = 1024 * 1024;
    const int UploadIdleTimeout = 60000;
    static readonly byte[] Capability = Encoding.ASCII.GetBytes("VNWeb/2");
    static readonly byte[] SshCapability = Encoding.ASCII.GetBytes("VNWeb/3");
    static readonly byte[] UploadProgressCapability = Encoding.ASCII.GetBytes("VNWeb/4");
    static readonly byte[] UploadResumeCapability = Encoding.ASCII.GetBytes("VNWeb/5");
    readonly Stream input, output;
    readonly int prefixSize;
    readonly bool isLocal;
    readonly IPAddress target;
    readonly int targetPort, localPort;
    readonly object writeLock = new object();
    readonly object filePipeLock = new object();
    readonly ConcurrentDictionary<int, Link> links = new ConcurrentDictionary<int, Link>();
    readonly TcpListener listener;
    readonly int defaultWebPort, sshPort;
    readonly string inbox;
    readonly string transferPipeName;
    readonly TaskCompletionSource<bool> uploadProgress = new TaskCompletionSource<bool>();
    readonly TaskCompletionSource<bool> uploadResume = new TaskCompletionSource<bool>();
    readonly Stopwatch progressClock = new Stopwatch();
    TcpListener defaultWeb, ssh;
    NamedPipeServerStream filePipe;
    volatile PendingUpload pendingUpload;
    VietnamTransfer.Receiver receiver;
    int receivingId;
    long lastAcknowledged;
    bool extended, sshEnabled;
    int nextId, closed;

    internal Tunnel(Stream input, Stream output, int prefixSize, bool isLocal, IPAddress target, int targetPort, int localPort,
                    int defaultWebPort = 0, string inbox = null, int sshPort = 0, string transferPipeName = VietnamTransfer.Pipe) {
        this.input = input; this.output = output; this.prefixSize = prefixSize; this.isLocal = isLocal;
        this.target = target; this.targetPort = targetPort; this.localPort = localPort;
        this.defaultWebPort = defaultWebPort; this.inbox = inbox; this.sshPort = sshPort;
        this.transferPipeName = transferPipeName;
        if (isLocal) listener = new TcpListener(IPAddress.Loopback, localPort);
    }

    internal void Run() {
        if (!isLocal) Send(Message.Ready, 0);
        var packet = new byte[1608];
        bool greeted = !isLocal;
        while (Volatile.Read(ref closed) == 0) {
            int count = input.Read(packet, 0, packet.Length);
            if (count == 0) break;
            int frameLength = count - prefixSize;
            if (frameLength < 5 || frameLength > MaxFrame) throw new IOException("Invalid relay frame length.");
            if (prefixSize != 0 && (BitConverter.ToInt32(packet, 0) != frameLength || (packet[4] & 3) != 3))
                throw new IOException("Invalid RDP channel packet.");
            var type = (Message)packet[prefixSize];
            int id = BitConverter.ToInt32(packet, prefixSize + 1);
            if (type != Message.Bytes) Trace("received " + type + " " + id);
            if (!greeted) {
                if (type != Message.Ready || id != 0 || frameLength != 5) throw new IOException("Invalid remote relay handshake.");
                listener.Server.ExclusiveAddressUse = true;
                listener.Start(32);
                Console.WriteLine("Listening on http://127.0.0.1:{0}/", localPort);
                Task.Run(() => Accept(listener, Message.Open));
                if (defaultWebPort != 0) Send(Message.Bytes, CapabilityId, Capability, Capability.Length);
                else if (sshPort != 0) Send(Message.Bytes, CapabilityId, SshCapability, SshCapability.Length);
                greeted = true;
                continue;
            }
            if (id <= 0 || type < Message.Open || type > Message.UploadOffset)
                throw new IOException("Invalid relay frame.");
            var bytes = new byte[frameLength - 5];
            Buffer.BlockCopy(packet, prefixSize + 5, bytes, 0, bytes.Length);
            if (type == Message.Bytes && id == CapabilityId) {
                string version = Encoding.ASCII.GetString(bytes);
                if (!extended && defaultWebPort != 0 && version == "VNWeb/2") {
                    EnableExtensions();
                    if (isLocal) {
                        var next = sshPort != 0 ? SshCapability : UploadProgressCapability;
                        Send(Message.Bytes, CapabilityId, next, next.Length);
                    }
                }
                if (!sshEnabled && sshPort != 0 && version == "VNWeb/3") {
                    EnableSsh();
                    if (isLocal && extended) Send(Message.Bytes, CapabilityId, UploadProgressCapability, UploadProgressCapability.Length);
                }
                if (extended && version == "VNWeb/4" && uploadProgress.TrySetResult(true)) {
                    var next = isLocal ? UploadResumeCapability : UploadProgressCapability;
                    Send(Message.Bytes, CapabilityId, next, next.Length);
                }
                if (extended && version == "VNWeb/5" && uploadProgress.Task.IsCompleted && uploadResume.TrySetResult(true) && !isLocal)
                    Send(Message.Bytes, CapabilityId, UploadResumeCapability, UploadResumeCapability.Length);
                continue;
            }
            if ((type >= Message.Upload && type <= Message.UploadCancel) || type >= Message.UploadProgress) {
                if (!extended) throw new IOException("Remote relay does not support file transfer.");
                ProcessUpload(type, id, bytes);
                continue;
            }
            if (type == Message.Bytes ? bytes.Length == 0 : bytes.Length != 0)
                throw new IOException("Invalid relay frame length.");
            Link link;
            if (type == Message.Open || type == Message.OpenDefaultWeb || type == Message.OpenSsh) {
                if (isLocal || links.ContainsKey(id)) throw new IOException("Unexpected relay connection request.");
                if (type == Message.OpenDefaultWeb && !extended) throw new IOException("Default web endpoint was not negotiated.");
                if (type == Message.OpenSsh && !sshEnabled) throw new IOException("SSH endpoint was not negotiated.");
                if (links.Count >= MaxConnections) { Send(Message.Abort, id); continue; }
                var client = new TcpClient(AddressFamily.InterNetwork);
                link = new Link(this, id, client);
                links[id] = link;
                int port = type == Message.OpenSsh ? sshPort : type == Message.OpenDefaultWeb ? defaultWebPort : targetPort;
                Task.Run(() => ConnectRemote(link, client, port));
            } else if (links.TryGetValue(id, out link)) {
                if (type == Message.Abort) { link.Close(false); continue; }
                if (!link.Enqueue(bytes)) link.Close(true);
            }
        }
    }

    void EnableExtensions() {
        extended = true;
        // Legacy relays ignore bytes addressed to this reserved, unassigned connection ID.
        if (!isLocal) { Send(Message.Bytes, CapabilityId, Capability, Capability.Length); return; }
        defaultWeb = new TcpListener(IPAddress.Loopback, defaultWebPort);
        try {
            defaultWeb.Server.ExclusiveAddressUse = true;
            defaultWeb.Start(32);
            Console.WriteLine("Listening on http://127.0.0.1:{0}/", defaultWebPort);
            Task.Run(() => Accept(defaultWeb, Message.OpenDefaultWeb));
        } catch (SocketException error) { Console.Error.WriteLine("Default web port unavailable: " + error.Message); }
        Task.Factory.StartNew(ServeUploads, TaskCreationOptions.LongRunning);
    }

    void EnableSsh() {
        sshEnabled = true;
        if (!isLocal) { Send(Message.Bytes, CapabilityId, SshCapability, SshCapability.Length); return; }
        ssh = new TcpListener(IPAddress.Loopback, sshPort);
        try {
            ssh.Server.ExclusiveAddressUse = true;
            ssh.Start(8);
            Console.WriteLine("Listening for SSH on 127.0.0.1:{0}", sshPort);
            Task.Run(() => Accept(ssh, Message.OpenSsh));
        } catch (SocketException error) { Console.Error.WriteLine("Local SSH port unavailable: " + error.Message); }
    }

    sealed class PendingUpload {
        internal readonly int Id;
        readonly long total;
        readonly object gate = new object();
        long acknowledged, offset = -1;
        string result;
        bool disconnected;
        internal PendingUpload(int id, long total) { Id = id; this.total = total; }
        internal void SetOffset(long value) {
            lock (gate) {
                if (offset >= 0 || value < 0 || value > total) throw new IOException("Invalid remote upload offset.");
                offset = acknowledged = value;
                Monitor.PulseAll(gate);
            }
        }
        internal long WaitForOffset() {
            lock (gate) {
                if (offset < 0 && result == null && !disconnected && !Monitor.Wait(gate, UploadIdleTimeout))
                    throw new VietnamTransfer.ConnectionInterrupted("远程桌面未在 60 秒内返回续传进度。");
                if (offset >= 0) return offset;
                if (disconnected) throw new VietnamTransfer.ConnectionInterrupted("远程桌面已断开。");
                throw new IOException(result != null && result[0] == '-' ? result.Substring(1) : "无法读取远程桌面的续传进度。");
            }
        }
        internal void ReceiveProgress(long received) {
            lock (gate) {
                if (received < acknowledged || received > total)
                    throw new IOException("Invalid remote file progress.");
                if (received == acknowledged) return;
                acknowledged = received;
                Monitor.PulseAll(gate);
            }
        }
        internal void Finish(string value) {
            lock (gate) {
                if (result == null) result = value;
                Monitor.PulseAll(gate);
            }
        }
        internal void Disconnect() {
            lock (gate) { disconnected = true; Monitor.PulseAll(gate); }
        }
        internal string Report(BinaryWriter reply, bool trackProgress, ref long reported, bool wait) {
            long received;
            string currentResult;
            lock (gate) {
                if (wait && result == null && !disconnected && (!trackProgress || acknowledged == reported) &&
                    !Monitor.Wait(gate, UploadIdleTimeout))
                    throw new VietnamTransfer.ConnectionInterrupted("远程桌面超过 60 秒没有返回新的接收进度或保存确认。");
                if (result == null && disconnected) throw new VietnamTransfer.ConnectionInterrupted("远程桌面已断开。");
                received = acknowledged;
                currentResult = result;
            }
            if (trackProgress && received > reported) {
                reply.Write((byte)VietnamTransfer.Reply.Progress);
                reply.Write(received);
                reported = received;
            }
            return currentResult;
        }
    }

    void ServeUploads() {
        var buffer = new byte[MaxFrame - 5];
        // Give the capability handshake a chance before exposing the pipe to the UI.
        uploadResume.Task.Wait(1500);
        while (Volatile.Read(ref closed) == 0) {
            NamedPipeServerStream pipe;
            lock (filePipeLock) {
                if (Volatile.Read(ref closed) != 0) return;
                try { filePipe = pipe = WebRelay.CreateLocalPipe(transferPipeName, PipeTransmissionMode.Byte); }
                catch (IOException error) { Console.Error.WriteLine("File transfer pipe unavailable: " + error.Message); return; }
            }
            try {
                pipe.WaitForConnection();
                var reader = new BinaryReader(pipe);
                int count = reader.ReadInt32();
                if (count < 9 + VietnamTransfer.IdentitySize || count > VietnamTransfer.MaxHeaderSize)
                    throw new IOException("Invalid file header.");
                byte[] header = reader.ReadBytes(count);
                if (header.Length != count) throw new IOException("Incomplete file header.");
                long remaining = BitConverter.ToInt64(header, 0);
                if (remaining < 0 || remaining > VietnamTransfer.MaxSize) throw new IOException("File too large.");
                bool resumable = uploadResume.Task.IsCompleted;
                bool trackProgress = resumable || uploadProgress.Task.IsCompleted;
                var reply = new BinaryWriter(pipe);
                var upload = new PendingUpload(Interlocked.Increment(ref nextId), remaining);
                pendingUpload = upload;
                try {
                    SendUploadFrame(resumable ? Message.UploadResume : Message.Upload, upload.Id, header,
                         resumable ? count : count - VietnamTransfer.IdentitySize);
                    long offset = resumable ? upload.WaitForOffset() : 0;
                    reply.Write((byte)VietnamTransfer.Reply.Offset);
                    reply.Write(resumable ? offset : -1L);
                    reply.Write((byte)(trackProgress ? VietnamTransfer.Reply.Progress : VietnamTransfer.Reply.Legacy));
                    if (trackProgress) reply.Write(offset);
                    remaining -= offset;
                    long sent = offset, reported = offset;
                    string result = upload.Report(reply, trackProgress, ref reported, false);
                    while (remaining > 0 && result == null) {
                        while (trackProgress && sent - reported >= UploadWindow && result == null)
                            result = upload.Report(reply, true, ref reported, true);
                        if (result != null) break;
                        count = pipe.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                        if (count == 0) throw new IOException("The sender disconnected.");
                        remaining -= count;
                        SendUploadFrame(Message.UploadBytes, upload.Id, buffer, count);
                        sent += count;
                        result = upload.Report(reply, trackProgress, ref reported, false);
                    }
                    if (result == null) SendUploadFrame(Message.UploadFinish, upload.Id);
                    while (result == null) result = upload.Report(reply, trackProgress, ref reported, true);
                    reply.Write((byte)(result[0] == '+' ? VietnamTransfer.Reply.Saved : VietnamTransfer.Reply.Failed));
                    reply.Write(result.Substring(1));
                } catch (IOException error) {
                    if (Volatile.Read(ref closed) == 0) {
                        try { Send(Message.UploadCancel, upload.Id); } catch (IOException) { }
                    }
                    bool interrupted = error is VietnamTransfer.ConnectionInterrupted;
                    reply.Write((byte)(resumable && interrupted ? VietnamTransfer.Reply.Interrupted : VietnamTransfer.Reply.Failed));
                    reply.Write(interrupted && !resumable ? "旧版远端中转无法确认上传结果。请检查 Inbox 或升级远端中转。" : error.Message);
                } finally { pendingUpload = null; }
                pipe.WaitForPipeDrain();
            } catch (IOException) { } catch (ObjectDisposedException) { break; }
            finally { lock (filePipeLock) { pipe.Dispose(); filePipe = null; } }
        }
    }

    void SendUploadFrame(Message type, int id, byte[] bytes = null, int length = 0) {
        try { Send(type, id, bytes, length); }
        catch (IOException error) { throw new VietnamTransfer.ConnectionInterrupted("远程桌面文件通道已断开。", error); }
    }

    void ProcessUpload(Message type, int id, byte[] bytes) {
        if (isLocal) {
            var upload = pendingUpload;
            if (type == Message.UploadOffset && uploadResume.Task.IsCompleted && bytes.Length == sizeof(long)) {
                if (upload != null && upload.Id == id) upload.SetOffset(BitConverter.ToInt64(bytes, 0));
            } else if (type == Message.UploadProgress && uploadProgress.Task.IsCompleted && bytes.Length == sizeof(long)) {
                if (upload != null && upload.Id == id) upload.ReceiveProgress(BitConverter.ToInt64(bytes, 0));
            } else if (type == Message.UploadResult && bytes.Length >= 2 && (bytes[0] == '+' || bytes[0] == '-')) {
                if (upload != null && upload.Id == id) upload.Finish(Encoding.UTF8.GetString(bytes));
            } else throw new IOException("Invalid file transfer reply.");
            return;
        }
        try {
            if (type == Message.Upload || type == Message.UploadResume) {
                if (inbox == null || receiver != null) throw new IOException("Remote file transfer is unavailable or busy.");
                bool resumable = type == Message.UploadResume;
                if (resumable && !uploadResume.Task.IsCompleted) throw new IOException("Upload resume was not negotiated.");
                receiver = new VietnamTransfer.Receiver(inbox, bytes, resumable);
                receivingId = id;
                lastAcknowledged = receiver.Received;
                progressClock.Restart();
                if (resumable) Send(Message.UploadOffset, id, BitConverter.GetBytes(lastAcknowledged), sizeof(long));
                if (receiver.SavedName != null) { ReplyToUpload(id, "+" + receiver.SavedName); CloseReceiver(); }
            } else if (receiver != null && receivingId == id) {
                if (type == Message.UploadBytes) {
                    receiver.Write(bytes);
                    long received = receiver.Received;
                    if (uploadProgress.Task.IsCompleted &&
                        (received == receiver.Size || received - lastAcknowledged >= 128*1024 || progressClock.ElapsedMilliseconds >= 250)) {
                        receiver.Flush();
                        Send(Message.UploadProgress, id, BitConverter.GetBytes(received), sizeof(long));
                        lastAcknowledged = received;
                        progressClock.Restart();
                    }
                }
                else if (type == Message.UploadFinish) { ReplyToUpload(id, "+" + receiver.Commit()); CloseReceiver(); }
                else if (type == Message.UploadCancel) CloseReceiver();
            }
        } catch (Exception error) {
            if (receivingId == id) CloseReceiver();
            ReplyToUpload(id, "-" + error.GetBaseException().Message.Substring(0, Math.Min(200, error.GetBaseException().Message.Length)));
        }
    }

    void ReplyToUpload(int id, string text) {
        byte[] reply = Encoding.UTF8.GetBytes(text);
        Send(Message.UploadResult, id, reply, reply.Length);
    }

    void CloseReceiver() { if (receiver != null) { receiver.Dispose(); receiver = null; } receivingId = 0; }

    void Accept(TcpListener endpoint, Message type) {
        try {
            while (Volatile.Read(ref closed) == 0) {
                var client = endpoint.AcceptTcpClient();
                Trace("accepted local browser connection");
                if (links.Count >= MaxConnections) { client.Close(); continue; }
                int id = Interlocked.Increment(ref nextId);
                var link = new Link(this, id, client);
                links[id] = link;
                try { Send(type, id); link.Start(); }
                catch { link.Close(false); break; }
            }
        } catch (SocketException) { } catch (ObjectDisposedException) { }
    }

    void ConnectRemote(Link link, TcpClient client, int port) {
        try {
            var connection = client.ConnectAsync(target, port);
            if (!connection.Wait(5000) || !client.Connected) throw new IOException("The web server did not accept the connection.");
            link.Start();
            Trace("connected upstream");
        } catch { link.Close(true); }
    }

    void Send(Message type, int id, byte[] bytes = null, int length = 0) {
        if (type != Message.Bytes) Trace("sending " + type + " " + id);
        var frame = new byte[5 + length];
        frame[0] = (byte)type;
        Buffer.BlockCopy(BitConverter.GetBytes(id), 0, frame, 1, 4);
        if (length > 0) Buffer.BlockCopy(bytes, 0, frame, 5, length);
        lock (writeLock) {
            if (Volatile.Read(ref closed) != 0) throw new IOException("Relay stopped.");
            output.Write(frame, 0, frame.Length);
        }
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        if (listener != null) listener.Stop();
        if (defaultWeb != null) defaultWeb.Stop();
        if (ssh != null) ssh.Stop();
        var upload = pendingUpload;
        if (upload != null) upload.Disconnect();
        lock (filePipeLock) { if (filePipe != null) filePipe.Dispose(); }
        CloseReceiver();
        foreach (var link in links.Values) link.Close(false);
    }

    sealed class Link {
        readonly Tunnel tunnel;
        readonly int id;
        readonly TcpClient client;
        readonly BlockingCollection<byte[]> queue = new BlockingCollection<byte[]>(256);
        int stopped, finished;

        internal Link(Tunnel tunnel, int id, TcpClient client) { this.tunnel = tunnel; this.id = id; this.client = client; }
        internal bool Enqueue(byte[] bytes) {
            try { return Volatile.Read(ref stopped) != 0 || queue.TryAdd(bytes, 5000); }
            catch (InvalidOperationException) { return true; }
        }
        internal void Start() {
            tunnel.Trace("starting stream " + id);
            if (Volatile.Read(ref stopped) != 0) return;
            client.NoDelay = true;
            // Both loops block on I/O. Give each its own thread to avoid starving the relay reader.
            Task.Factory.StartNew(ReadSocket, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(WriteSocket, TaskCreationOptions.LongRunning);
        }
        void ReadSocket() {
            try {
                var buffer = new byte[MaxFrame - 5];
                var stream = client.GetStream();
                tunnel.Trace("socket reader ready " + id);
                int count;
                int total = 0;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) { if(total == 0)tunnel.Trace("first socket bytes " + id + " " + count); total+=count; tunnel.Send(Message.Bytes, id, buffer, count); }
                tunnel.Send(Message.Finish, id);
                Finish();
            } catch { Close(true); }
        }
        void WriteSocket() {
            try {
                var stream = client.GetStream();
                tunnel.Trace("socket writer ready " + id);
                bool first = true;
                foreach (var bytes in queue.GetConsumingEnumerable()) {
                    if (bytes.Length == 0) { client.Client.Shutdown(SocketShutdown.Send); Finish(); return; }
                    if(first){tunnel.Trace("first pipe bytes " + id + " " + bytes.Length);first=false;}stream.Write(bytes, 0, bytes.Length);
                }
            } catch { Close(true); }
        }
        void Finish() { if (Interlocked.Increment(ref finished) == 2) Close(false); }
        internal void Close(bool notify) {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            Link removed;
            tunnel.links.TryRemove(id, out removed);
            queue.CompleteAdding();
            client.Close();
            if (notify) try { tunnel.Send(Message.Abort, id); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
    }
}

internal static class WebRelay {
    static void RemoteStatus(string stage, string detail = "") {
        try {
            string source = Assembly.GetExecutingAssembly().Location;
            if (source.StartsWith(@"\\tsclient\C\Users\Mock\VNWeb\", StringComparison.OrdinalIgnoreCase)) {
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(source), "remote-status.txt"),
                    DateTime.Now.ToString("o") + " " + stage + " " + detail.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void CopyToRemoteDesktop() {
        string source = Assembly.GetExecutingAssembly().Location;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string destination = Path.Combine(desktop, "VietnamWebRelay.exe");
        try {
            if (!String.Equals(source, destination, StringComparison.OrdinalIgnoreCase) && !File.Exists(destination)) File.Copy(source, destination);
            RemoteStatus("DESKTOP_COPY_READY", "VietnamWebRelay.exe");
        } catch (IOException) { RemoteStatus("DESKTOP_COPY_SKIPPED"); }
        catch (UnauthorizedAccessException) { RemoteStatus("DESKTOP_COPY_SKIPPED"); }
    }

    [DllImport("wtsapi32.dll", CharSet=CharSet.Ansi, ExactSpelling=true, SetLastError=true)]
    static extern IntPtr WTSVirtualChannelOpenEx(uint session, string name, uint flags);
    [DllImport("wtsapi32.dll", SetLastError=true)]
    static extern bool WTSVirtualChannelQuery(IntPtr channel, int query, out IntPtr data, out uint bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr data);
    [DllImport("wtsapi32.dll")] static extern bool WTSVirtualChannelClose(IntPtr channel);
    [DllImport("wtsapi32.dll", EntryPoint="WTSQuerySessionInformationW", ExactSpelling=true)]
    static extern bool QuerySession(IntPtr server, uint session, int info, out IntPtr data, out uint bytes);

    const int WtsConnectState = 8, WtsClientProtocolType = 16;
    const int WtsActive = 0, RdpProtocol = 2;
    static bool IsConnectedRdpSession() {
        int protocol, state;
        return TrySessionValue(WtsClientProtocolType, sizeof(short), out protocol) && protocol == RdpProtocol &&
            TrySessionValue(WtsConnectState, sizeof(int), out state) && state == WtsActive;
    }

    static bool TrySessionValue(int info, int size, out int value) {
        value = -1;
        IntPtr data;
        uint bytes;
        if (!QuerySession(IntPtr.Zero, uint.MaxValue, info, out data, out bytes)) return false;
        try {
            if (data == IntPtr.Zero || bytes < size) return false;
            value = size == sizeof(short) ? Marshal.ReadInt16(data) : Marshal.ReadInt32(data);
            return true;
        } finally { WTSFreeMemory(data); }
    }

    static NamedPipeServerStream LocalPipe() { return CreateLocalPipe(Session.Pipe, PipeTransmissionMode.Message); }

    internal static NamedPipeServerStream CreateLocalPipe(string name, PipeTransmissionMode mode) {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        using (var identity = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, mode, PipeOptions.Asynchronous, 65536, 65536, security);
    }

    internal static void RunLocal(int port, Func<NamedPipeServerStream> openPipe, Func<bool> keepRunning, int defaultWebPort = 0, int sshPort = 0) {
        while (keepRunning()) {
            using (var pipe = openPipe()) {
                Console.WriteLine("Waiting for the remote relay in the RDP session...");
                pipe.WaitForConnection();
                try {
                    using (var tunnel = new Tunnel(pipe, pipe, 0, true, null, 0, port, defaultWebPort, sshPort: sshPort)) tunnel.Run();
                } catch (IOException error) {
                    Console.WriteLine("Relay channel ended: " + error.Message);
                } catch (SocketException error) {
                    Console.Error.WriteLine("The local web port could not be opened: " + error.Message);
                }
            }
        }
    }

    static void CheckWeb(IPAddress target, int targetPort) {
        using (var probe = new TcpClient()) {
            if (!probe.ConnectAsync(target, targetPort).Wait(5000) || !probe.Connected) throw new IOException("Cannot reach 10.214.1.18:3000 from this desktop.");
        }
    }

    static void RunRemote(IPAddress target, int targetPort, bool background, Action<string> status) {
        if (!background) { CheckWeb(target, targetPort); status("WEB_REACHABLE"); }
        IntPtr channel = WTSVirtualChannelOpenEx(uint.MaxValue, Session.Channel, 1);
        if (channel == IntPtr.Zero) throw new IOException("Cannot open the relay channel. Reconnect with the local relay set up. Windows error " + Marshal.GetLastWin32Error());
        try {
            IntPtr data;
            uint size;
            if (!WTSVirtualChannelQuery(channel, 1, out data, out size)) throw new IOException("Cannot get the RDP channel handle. Windows error " + Marshal.GetLastWin32Error());
            IntPtr file;
            try {
                if (size != IntPtr.Size) throw new IOException("Unexpected RDP channel handle.");
                file = Marshal.ReadIntPtr(data);
            } finally { WTSFreeMemory(data); }
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string inbox = String.IsNullOrEmpty(desktop) ? null : Path.Combine(desktop, "Vietnam Inbox");
            using (var reader = new ChannelStream(file, false))
            using (var writer = new ChannelStream(file, true))
            using (var tunnel = new Tunnel(reader, writer, 8, false, target, targetPort, 0, 80, inbox, 22)) {
                status("RDP_CHANNEL_OPEN");
                tunnel.Run();
            }
        } finally { WTSVirtualChannelClose(channel); }
    }

    static int RunBackgroundRemote() {
        bool first;
        using (var guard = new Mutex(true, @"Local\VietnamWebRelay-" + Session.Channel, out first)) {
            if (!first) return 0;
            string targetLog = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "relay.log");
            string lastStatus = null;
            Action<string> status = message => {
                if (message == lastStatus) return;
                lastStatus = message;
                try {
                    if (File.Exists(targetLog) && new FileInfo(targetLog).Length >= 262144) File.WriteAllText(targetLog, "");
                    File.AppendAllText(targetLog, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
                } catch (IOException) { } catch (UnauthorizedAccessException) { }
            };
            while (true) {
                if (IsConnectedRdpSession()) {
                    try {
                        RunRemote(IPAddress.Parse("10.214.1.18"), 3000, true, status);
                        status("RDP_CHANNEL_CLOSED");
                    } catch (Exception error) {
                        status("WAITING_FOR_CHANNEL " + error.GetBaseException().Message);
                    }
                } else {
                    status("WAITING_FOR_RDP");
                }
                Thread.Sleep(5000);
            }
        }
    }

    static int Main(string[] args) {
        try {
#if LOCAL
            RunLocal(23180, LocalPipe, () => true, 23181, 23222);
#elif REMOTE_BACKGROUND
            return RunBackgroundRemote();
#else
            RemoteStatus("STARTED");
            CopyToRemoteDesktop();
            RunRemote(IPAddress.Parse("10.214.1.18"), 3000, false, stage => {
                RemoteStatus(stage);
                if (stage == "RDP_CHANNEL_OPEN") Console.WriteLine("Forwarding the internal web service. Leave this window open.");
            });
#endif
#if !REMOTE_BACKGROUND
            Console.WriteLine("The relay has ended.");
            return 0;
#endif
        }
#if REMOTE_BACKGROUND
        catch (Exception) { return 1; }
#else
        catch (Exception error) {
            RemoteStatus("FAILED", error.GetBaseException().Message);
            Console.Error.WriteLine(error.GetBaseException().Message);
            if (!Console.IsInputRedirected) { Console.WriteLine("Press Enter to close."); Console.ReadLine(); }
            return 1;
        }
#endif
    }
}
