using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

internal static class VietnamTransfer {
    internal const string Pipe = "VietnamWeb.FileTransfer.v3";
    internal const long MaxSize = 1024L * 1024 * 1024;
    internal const int IdentitySize = 48;
    internal const int MaxHeaderSize = 776 + IdentitySize;
    internal enum Reply : byte { Progress, Legacy, Saved, Failed, Offset, Interrupted }
    static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal sealed class ConnectionInterrupted : IOException {
        internal ConnectionInterrupted(string message, Exception inner = null) : base(message, inner) { }
    }

    static bool IsPipeDisconnect(IOException error) {
        switch (unchecked((uint)error.HResult)) {
            case 0x8007006d: // ERROR_BROKEN_PIPE
            case 0x800700e8: // ERROR_NO_DATA
            case 0x800700e9: // ERROR_PIPE_NOT_CONNECTED
                return true;
            default:
                return error is EndOfStreamException;
        }
    }

    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool WaitNamedPipe(string name, uint timeoutMilliseconds);

    internal static bool IsAvailable() { return WaitNamedPipe(@"\\.\pipe\"+Pipe,1); }

    internal static byte[] Header(string name, long size) {
        byte[] text = Utf8.GetBytes(name);
        if (text.Length == 0 || text.Length > 768 || name == "." || name == ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.TrimEnd(' ', '.') != name ||
            Regex.IsMatch(name.Split('.')[0].TrimEnd(), @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase) ||
            size < 0 || size > MaxSize)
            throw new IOException("Invalid file name or size (maximum 1 GiB).");
        var header = new byte[8 + text.Length];
        Buffer.BlockCopy(BitConverter.GetBytes(size), 0, header, 0, 8);
        Buffer.BlockCopy(text, 0, header, 8, text.Length);
        return header;
    }

    static byte[] FileHash(Stream source, CancellationToken cancellation) {
        using (var sha = SHA256.Create()) {
            var buffer = new byte[256 * 1024];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0) {
                cancellation.ThrowIfCancellationRequested();
                sha.TransformBlock(buffer, 0, count, null, 0);
            }
            sha.TransformFinalBlock(buffer, 0, 0);
            return sha.Hash;
        }
    }

    static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }

    static string ResumeIdPath(string directory, string path, byte[] hash) {
        if (directory == null) return null;
        Directory.CreateDirectory(directory);
        using (var sha = SHA256.Create()) {
            string identity = Path.GetFullPath(path).ToUpperInvariant() + "|" + Hex(hash);
            return Path.Combine(directory, Hex(sha.ComputeHash(Utf8.GetBytes(identity))) + ".id");
        }
    }

    static Guid LoadResumeId(string path) {
        Guid id;
        if (path != null && File.Exists(path) && Guid.TryParseExact(File.ReadAllText(path), "N", out id)) return id;
        id = Guid.NewGuid();
        if (path != null) File.WriteAllText(path, id.ToString("N"));
        return id;
    }

    internal static string Send(string path, Func<int,bool> trustedServer, string pipeName = Pipe, Action<long,long> progress = null,
                                CancellationToken cancellation = default(CancellationToken), string resumeDirectory = null,
                                int replyTimeout = 75000) {
        using (var source = File.OpenRead(path))
        using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
        using (var idle = new CancellationTokenSource())
        using (idle.Token.Register(pipe.Dispose))
        using (cancellation.Register(pipe.Dispose)) {
          bool resumable = true;
          try {
            cancellation.ThrowIfCancellationRequested();
            long length = source.Length;
            byte[] metadata = Header(Path.GetFileName(path), length);
            try { pipe.Connect(3000); }
            catch (TimeoutException error) { throw new ConnectionInterrupted("文件通道暂不可用。", error); }
            uint processId;
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out processId) || !trustedServer((int)processId))
                throw new IOException("无法验证文件通道，请重新启动助手中的越南网页中转。");
            byte[] hash = FileHash(source, cancellation);
            string idPath = ResumeIdPath(resumeDirectory, path, hash);
            byte[] header = new byte[metadata.Length + IdentitySize];
            Buffer.BlockCopy(metadata, 0, header, 0, metadata.Length);
            Buffer.BlockCopy(LoadResumeId(idPath).ToByteArray(), 0, header, metadata.Length, 16);
            Buffer.BlockCopy(hash, 0, header, metadata.Length + 16, 32);
            var writer = new BinaryWriter(pipe);
            idle.CancelAfter(replyTimeout);
            writer.Write(header.Length);
            writer.Write(header);
            var reader = new BinaryReader(pipe);
            Reply first = ReadReply(reader, pipe);
            if (first == Reply.Failed) throw new IOException(reader.ReadString());
            if (first == Reply.Interrupted) throw new ConnectionInterrupted(reader.ReadString());
            if (first != Reply.Offset) throw new IOException("文件通道未返回续传偏移。");
            long offset = reader.ReadInt64();
            if (offset < -1 || offset > length) throw new IOException("远程桌面返回了无效的续传偏移。");
            resumable = offset >= 0;
            idle.CancelAfter(resumable ? replyTimeout : Timeout.Infinite);
            offset = Math.Max(0, offset);
            source.Position = offset;
            var response = Task.Run(() => ReadReplies(pipe, length, (received,total) => {
                if (resumable && received >= 0) idle.CancelAfter(replyTimeout);
                if (progress != null) progress(received,total);
            }));
            var buffer = new byte[64 * 1024];
            long sent = offset;
            try {
                while (sent < length && !response.IsCompleted) {
                    int count = source.Read(buffer,0,(int)Math.Min(length-sent,buffer.Length));
                    if (count == 0) throw new IOException("文件在发送时被截断。");
                    pipe.Write(buffer,0,count);
                    sent += count;
                }
            } catch (Exception error) {
                if (response.IsCompleted) response.GetAwaiter().GetResult();
                bool disconnected = !pipe.IsConnected;
                pipe.Dispose();
                try { response.GetAwaiter().GetResult(); }
                catch (IOException) { } catch (ObjectDisposedException) { }
                if (disconnected && error is IOException) throw new EndOfStreamException("File transfer pipe disconnected.",error);
                throw;
            }
            string saved = response.GetAwaiter().GetResult();
            try { if (idPath != null) File.Delete(idPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return saved;
          } catch (Exception error) {
            cancellation.ThrowIfCancellationRequested();
            if (resumable && idle.IsCancellationRequested) throw new ConnectionInterrupted("远程桌面文件通道长时间没有响应。", error);
            var io = error as IOException;
            if (io != null && IsPipeDisconnect(io)) {
                if (resumable) throw new ConnectionInterrupted("远程桌面文件通道已断开。", error);
                throw new IOException("旧版远端中转无法确认上传结果。请检查 Inbox 或升级远端中转。", error);
            }
            throw;
          }
        }
    }

    static Reply ReadReply(BinaryReader reader, NamedPipeClientStream pipe) {
        try { return (Reply)reader.ReadByte(); }
        catch (IOException error) {
            if (!pipe.IsConnected) throw new EndOfStreamException("File transfer pipe disconnected.",error);
            throw;
        }
    }

    static string ReadReplies(NamedPipeClientStream pipe, long length, Action<long,long> progress) {
        var reader = new BinaryReader(pipe);
        long received = -1;
        while (true) {
            switch (ReadReply(reader, pipe)) {
                case Reply.Progress:
                    long next = reader.ReadInt64();
                    if (next < 0 || next < received || next > length)
                        throw new IOException("远程桌面返回了无效的接收进度。");
                    received = next;
                    if (progress != null) progress(received,length);
                    break;
                case Reply.Legacy:
                    if (progress != null) progress(-1,length);
                    break;
                case Reply.Saved:
                    return reader.ReadString();
                case Reply.Failed:
                    throw new IOException(reader.ReadString());
                case Reply.Interrupted:
                    throw new ConnectionInterrupted(reader.ReadString());
                default:
                    throw new IOException("无法识别文件接收回执。");
            }
        }
    }

    internal sealed class Receiver : IDisposable {
        readonly string directory, name, temp, receipt;
        readonly FileStream output;
        readonly long size;
        readonly byte[] expectedHash;
        long received;
        internal long Received { get { return received; } }
        internal long Size { get { return size; } }

        internal string SavedName { get; private set; }

        internal Receiver(string directory, byte[] header, bool resume = false) {
            int metadataLength = header.Length - (resume ? IdentitySize : 0);
            if (metadataLength < 9 || metadataLength > 776) throw new IOException("Invalid transfer header.");
            size = BitConverter.ToInt64(header, 0);
            name = Utf8.GetString(header, 8, metadataLength - 8);
            Header(name, size);
            this.directory = directory;
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The inbox folder is redirected.");
            if (!resume) {
                temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
                output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
                return;
            }
            var parts = Path.Combine(directory, ".vn-upload-parts");
            Directory.CreateDirectory(parts);
            RejectLink(parts);
            File.SetAttributes(parts, File.GetAttributes(parts) | FileAttributes.Hidden);
            string key;
            using (var sha = SHA256.Create()) key = Hex(sha.ComputeHash(header));
            temp = Path.Combine(parts, key + ".partial");
            receipt = Path.Combine(parts, key + ".saved");
            expectedHash = new byte[32];
            Buffer.BlockCopy(header, metadataLength + 16, expectedHash, 0, 32);
            if (!File.Exists(temp) && File.Exists(receipt)) {
                RejectLink(receipt);
                string priorName = File.ReadAllText(receipt, Utf8);
                Header(priorName, size);
                string prior = Path.Combine(directory, priorName);
                if (File.Exists(prior)) {
                    RejectLink(prior);
                    using (var file = File.OpenRead(prior)) {
                        if (file.Length == size && Hex(FileHash(file, CancellationToken.None)) == Hex(expectedHash)) {
                            SavedName = priorName;
                            received = size;
                            return;
                        }
                    }
                }
            }
            if (File.Exists(temp)) RejectLink(temp);
            output = new FileStream(temp, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 64 * 1024);
            if (output.Length > size) output.SetLength(0);
            received = output.Seek(0, SeekOrigin.End);
        }

        static void RejectLink(string path) {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The Inbox or upload state is redirected.");
        }

        internal void Write(byte[] bytes) {
            if (received + bytes.Length > size) throw new IOException("File length exceeds the declared size.");
            output.Write(bytes, 0, bytes.Length);
            received += bytes.Length;
        }

        internal void Flush() { output.Flush(); }

        internal string Commit() {
            if (SavedName != null) return SavedName;
            if (received != size) throw new IOException("Incomplete file.");
            if (expectedHash != null) {
                output.Flush();
                output.Position = 0;
                if (Hex(FileHash(output, CancellationToken.None)) != Hex(expectedHash)) {
                    output.Dispose();
                    File.Delete(temp);
                    throw new IOException("文件校验失败，继续上传时将重新发送。");
                }
            }
            output.Dispose();
            string stem = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            for (int i = 0; i < 1000; i++) {
                string candidate = i == 0 ? name : stem + " (" + i + ")" + extension;
                string destination = Path.Combine(directory, candidate);
                if (File.Exists(destination) || Directory.Exists(destination)) continue;
                if (receipt != null) WriteReceipt(candidate);
                try { File.Move(temp, destination); return candidate; }
                catch (IOException) { if (!File.Exists(destination) && !Directory.Exists(destination)) throw; }
            }
            throw new IOException("No available file name.");
        }

        void WriteReceipt(string candidate) {
            string draft = receipt + "." + Guid.NewGuid().ToString("N");
            try {
                using (var file = new FileStream(draft, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    byte[] bytes = Utf8.GetBytes(candidate);
                    file.Write(bytes, 0, bytes.Length);
                }
                if (File.Exists(receipt)) { RejectLink(receipt); File.Replace(draft, receipt, null); }
                else File.Move(draft, receipt);
            } finally { File.Delete(draft); }
        }

        public void Dispose() {
            if (output == null) return;
            output.Dispose();
            if (receipt != null) return;
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
