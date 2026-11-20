using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GlobalProtectorWiFi {
    internal sealed class VietnamFileWindow : Form {
        internal const string WindowTitle = "传到远程桌面";
        const string Busy = "正在上传，可点「暂停」停止传输。";
        const string WaitingForSave = "等待远程桌面确认保存";
        const string Reconnecting = "上传连接中断，正在自动重连…";
        readonly Color accent = Color.FromArgb(13,125,113), ink = Color.FromArgb(28,45,57), muted = Color.FromArgb(99,116,128);
        readonly ToolTip tips = new ToolTip();
        readonly ListBox queue = new ListBox { HorizontalScrollbar=true, IntegralHeight=false, SelectionMode=SelectionMode.MultiExtended };
        readonly ActionButton browse = new ActionButton(), remove = new ActionButton(), pause = new ActionButton(), send = new SoftButton();
        readonly Label status = new Label(), detail = new Label();
        readonly TransferProgressBar progress = new TransferProgressBar { Visible=false };
        readonly System.Windows.Forms.Timer connectionTimer = new System.Windows.Forms.Timer { Interval=1000 };
        readonly Func<bool> checkConnection;
        readonly Func<CancellationToken,Task<string>> reconnect;
        readonly int initialRetryDelay;
        CancellationTokenSource activeUpload;
        bool busy, retryPending;

        sealed class TransferProgressBar : Control {
            int percent;
            internal TransferProgressBar() {
                AccessibleRole=AccessibleRole.ProgressBar;
                SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint,true);
            }
            internal int Value {
                get { return percent; }
                set {
                    percent=Math.Max(0,Math.Min(100,value));
                    AccessibleDescription=percent+"%";
                    Invalidate();
                }
            }
            protected override void OnPaint(PaintEventArgs e) {
                using(var track=new SolidBrush(Color.FromArgb(231,237,240)))e.Graphics.FillRectangle(track,ClientRectangle);
                using(var fill=new SolidBrush(Color.FromArgb(13,125,113)))e.Graphics.FillRectangle(fill,0,0,Width*percent/100,Height);
            }
        }

        VietnamFileWindow(Func<bool> checkConnection, Func<CancellationToken,Task<string>> reconnect = null, int initialRetryDelay = 2000) {
            this.checkConnection=checkConnection;
            this.reconnect=reconnect ?? VietnamWebSession.ReconnectFileTransfer;
            this.initialRetryDelay=initialRetryDelay;
            Text=WindowTitle;ClientSize=new Size(498,370);StartPosition=FormStartPosition.CenterScreen;
            FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;BackColor=Color.White;ForeColor=ink;
            Font=new Font("Microsoft YaHei UI",9F);AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            Icon=AppBrand.CreateIcon(32);

            var title=new Label { Text=WindowTitle,Bounds=new Rectangle(22,15,452,30),Font=new Font(Font.FontFamily,16,FontStyle.Bold) };
            var destination=new Label { Text=Program.DefaultTarget+@" · 桌面\Vietnam Inbox",Bounds=new Rectangle(22,49,452,24),ForeColor=muted };
            var dropHint=new Label { Text="将文件拖到下方，或点「选择文件」",Bounds=new Rectangle(23,86,452,23),ForeColor=muted };
            queue.SetBounds(23,110,452,106);queue.AccessibleName="待上传的文件";
            queue.SelectedIndexChanged+=delegate { RefreshButtons(); };
            RegisterDrop(dropHint);RegisterDrop(queue);RegisterDrop(this);

            Style(browse,"选择文件",23,226,94,false);browse.Click+=delegate { ChooseFiles(); };
            Style(remove,"移除所选",126,226,94,false);remove.Click+=delegate {
                foreach (string item in queue.SelectedItems.Cast<string>().ToArray()) queue.Items.Remove(item);
                RefreshButtons();
            };
            Style(pause,"暂停",229,226,111,false);pause.Click+=delegate { PauseUpload(); };
            Style(send,"上传",349,226,126,true);
            send.Click+=async delegate { await Upload(VietnamWebSession.SendFile); };
            AcceptButton=send;
            status.SetBounds(23,271,452,22);status.AutoEllipsis=true;
            detail.SetBounds(23,306,452,22);detail.ForeColor=muted;detail.AutoEllipsis=true;
            progress.SetBounds(23,335,452,11);progress.AccessibleName="远程桌面文件接收进度";
            Controls.AddRange(new Control[]{title,destination,dropHint,queue,browse,remove,pause,send,status,detail,progress});
            RefreshButtons();
            connectionTimer.Tick+=delegate {if(!busy && queue.Items.Count>0)RefreshButtons();};
            Shown+=delegate {connectionTimer.Start();};
        }

        void Style(ActionButton button,string text,int x,int y,int width,bool primary) {
            button.Text=text;button.SetBounds(x,y,width,33);button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderSize=1;
            button.FlatAppearance.BorderColor=primary?accent:Color.FromArgb(211,222,228);
            button.BackColor=primary?accent:Color.White;button.ForeColor=primary?Color.White:ink;
            button.Font=new Font(Font,FontStyle.Bold);button.Cursor=Cursors.Hand;
        }

        void RegisterDrop(Control control) {
            control.AllowDrop=true;
            DragEventHandler indicate=(sender,e)=>e.Effect=CanDrop(e.Data)?DragDropEffects.Copy:DragDropEffects.None;
            control.DragEnter+=indicate;control.DragOver+=indicate;
            control.DragDrop+=(sender,e)=>{ if(CanDrop(e.Data)) AddFiles((string[])e.Data.GetData(DataFormats.FileDrop)); };
        }

        bool CanDrop(IDataObject data) { return !busy && data!=null && data.GetDataPresent(DataFormats.FileDrop); }

        void ChooseFiles() {
            using(var dialog=new OpenFileDialog {Title="选择要上传的文件",Multiselect=true,Filter="所有文件 (*.*)|*.*"})
                if(dialog.ShowDialog(this)==DialogResult.OK)AddFiles(dialog.FileNames);
        }

        void AddFiles(IEnumerable<string> paths) {
            bool skipped=false;
            var names=new HashSet<string>(queue.Items.Cast<string>(),StringComparer.OrdinalIgnoreCase);
            foreach(string path in paths) {
                if(!File.Exists(path)) { skipped=true;continue; }
                if(names.Add(path))queue.Items.Add(path);
            }
            status.Text=skipped?"已跳过不可访问的路径或文件夹。":"";
            detail.Text="";progress.Visible=false;
            RefreshButtons();
        }

        void RefreshButtons() {
            browse.SetBlocked(busy?Busy:null,tips);
            remove.SetBlocked(busy?Busy:queue.SelectedItems.Count==0?"请先选中要移除的文件。":null,tips);
            send.SetBlocked(busy?Busy:queue.Items.Count==0?"请先拖入或选择要上传的文件。":
                checkConnection()?null:"请保持远程桌面连接，等待文件通道就绪。",tips);
            bool pausing=activeUpload!=null && activeUpload.IsCancellationRequested;
            pause.SetBlocked(pausing?"正在暂停上传。":activeUpload==null?"上传开始后可以暂停。":null,tips);
            pause.Text=pausing?"正在暂停…":"暂停";
            if(queue.Items.Count==0)retryPending=false;
            send.Text=busy?"上传中…":retryPending?"继续上传":queue.Items.Count==0?"上传":"上传 ("+queue.Items.Count+")";
        }

        void PauseUpload() {
            if(activeUpload==null || activeUpload.IsCancellationRequested)return;
            activeUpload.Cancel();
            status.Text="正在暂停上传…";
            RefreshButtons();
        }

        sealed class UploadProgress {
            internal int Index,Count;
            internal string Path;
            internal long Received,Length;
        }

        void ShowProgress(UploadProgress item) {
            if(item.Received<0) {
                status.Text="旧版远端中转 · 等待保存确认 · "+Path.GetFileName(item.Path);
                detail.Text="升级远端中转后可查看 Inbox 的实际接收进度。";
                progress.Visible=false;
                return;
            }
            bool awaitingSave=item.Received==item.Length;
            int percent=awaitingSave?99:(int)(100*item.Received/item.Length);
            status.Text=(awaitingSave?WaitingForSave:"远程桌面正在接收")+" · "+Path.GetFileName(item.Path);
            detail.Text=awaitingSave
                ? String.Format("第 {0}/{1} 个 · Inbox 已接收 {2}",item.Index,item.Count,FormatBytes(item.Length))
                : String.Format("第 {0}/{1} 个 · Inbox 已接收 {2} / {3} · {4}%",item.Index,item.Count,FormatBytes(item.Received),FormatBytes(item.Length),percent);
            progress.Visible=true;
            progress.Value=percent;
        }

        static string FormatBytes(long bytes) {
            if(bytes<1024)return bytes+" B";
            if(bytes<1024*1024)return (bytes/1024d).ToString("0.0")+" KiB";
            return (bytes/(1024d*1024)).ToString("0.0")+" MiB";
        }

        async Task<string> SendWithReconnect(Func<string,Action<long,long>,CancellationToken,string> sendFile,
                string file,int index,int count,CancellationToken cancellation) {
            int delay=initialRetryDelay;
            while(true) {
                cancellation.ThrowIfCancellationRequested();
                bool attemptActive=true;
                IProgress<UploadProgress> reporter=new Progress<UploadProgress>(item=>{
                    if(attemptActive && !cancellation.IsCancellationRequested)ShowProgress(item);
                });
                try {
                    return await Task.Run(()=>sendFile(file,(received,length)=>reporter.Report(new UploadProgress {
                        Index=index,Count=count,Path=file,Received=received,Length=length
                    }),cancellation),cancellation);
                } catch(VietnamTransfer.ConnectionInterrupted) { }
                finally { attemptActive=false; }

                cancellation.ThrowIfCancellationRequested();
                status.Text=Reconnecting;
                do {
                    await Task.Delay(delay,cancellation);
                    delay=Math.Min(15000,delay*2);
                    if(checkConnection())break;
                    string message=await reconnect(cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if(!String.IsNullOrWhiteSpace(message))status.Text=message;
                } while(!checkConnection());
            }
        }

        async Task Upload(Func<string,Action<long,long>,CancellationToken,string> sendFile) {
            if(busy || queue.Items.Count==0)return;
            string[] batch=queue.Items.Cast<string>().ToArray();
            int saved=0;
            string savedName=null;
            var cancellation=new CancellationTokenSource();
            activeUpload=cancellation;
            busy=true;
            if(!retryPending){progress.Value=0;progress.Visible=false;}
            status.Text="正在连接文件通道…";detail.Text="";RefreshButtons();
            Exception failure=null;
            try {
                for(int i=0;i<batch.Length;i++) {
                    cancellation.Token.ThrowIfCancellationRequested();
                    string file=batch[i];
                    savedName=await SendWithReconnect(sendFile,file,i+1,batch.Length,cancellation.Token);
                    queue.Items.Remove(file);
                    saved++;
                }
            } catch(Exception error) { failure=error; }
            activeUpload=null;
            bool paused=cancellation.IsCancellationRequested && failure is OperationCanceledException;
            cancellation.Dispose();
            busy=false;
            retryPending=failure!=null;
            if(failure==null) {
                progress.Visible=true;
                progress.Value=100;
                status.Text="远程桌面已保存 · "+savedName;
                detail.Text=String.Format("远程桌面已确认 {0}/{1} 个文件 · 100%",saved,batch.Length);
            }
            else {
                status.Text=paused?"上传已暂停。":failure is TimeoutException?"文件通道未就绪。请保持远程桌面连接。":failure.GetBaseException().Message;
                detail.Text=String.Format("远程桌面已确认 {0}/{1} 个文件 · 可点「继续上传」",saved,batch.Length);
            }
            tips.SetToolTip(detail,saved==0?"":@"最后保存到桌面\Vietnam Inbox\"+savedName);
            tips.SetToolTip(status,status.Text);RefreshButtons();
        }

        protected override void OnFormClosing(FormClosingEventArgs e) {
            if(busy && e.CloseReason==CloseReason.UserClosing) { e.Cancel=true;status.Text=Busy; }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing) {
            if(disposing){connectionTimer.Dispose();tips.Dispose();if(Icon!=null)Icon.Dispose();}
            base.Dispose(disposing);
        }

        internal static int Run() {
            using(var mutex=new Mutex(false,@"Local\GlobalProtectorWiFi-Transfer-"+WindowsIdentity.GetCurrent().User.Value)) {
                bool owned;
                try { owned=mutex.WaitOne(0); } catch(AbandonedMutexException) { owned=true; }
                if(!owned){MessageBox.Show("传文件窗口已经打开。",WindowTitle);return 0;}
                try { using(var window=new VietnamFileWindow(VietnamTransfer.IsAvailable))Application.Run(window);return 0; }
                finally { mutex.ReleaseMutex(); }
            }
        }

        internal static bool CheckUi() {
            bool connected=false;
            using(var window=new VietnamFileWindow(()=>connected)) {
                int clicks=0;
                window.send.Click+=delegate {clicks++;};
                window.send.PerformClick();
                var files=new DataObject(DataFormats.FileDrop,new[]{typeof(VietnamFileWindow).Assembly.Location});
                bool empty=clicks==0 && !window.send.ActionAvailable && window.send.Cursor==Cursors.No && window.queue.AllowDrop && window.CanDrop(files);
                window.AddFiles((string[])files.GetData(DataFormats.FileDrop));
                window.AddFiles((string[])files.GetData(DataFormats.FileDrop));
                bool waiting=!window.send.ActionAvailable && window.send.AccessibleDescription.Contains("远程桌面");
                connected=true;window.RefreshButtons();
                bool queued=waiting && window.send.ActionAvailable && window.queue.Items.Count==1;
                window.busy=true;window.RefreshButtons();
                window.ShowProgress(new UploadProgress {Index=1,Count=2,Path="test.log",Received=-1,Length=1000});
                bool legacy=window.status.Text.Contains("旧版远端中转") && !window.progress.Visible;
                window.ShowProgress(new UploadProgress {Index=1,Count=2,Path="test.log",Received=400,Length=1000});
                bool progressShown=window.progress.Value==40 && window.detail.Text.Contains("40%") &&
                    window.detail.Text.Contains("Inbox 已接收") && !window.send.ActionAvailable && !window.CanDrop(files);
                window.ShowProgress(new UploadProgress {Index=1,Count=2,Path="test.log",Received=1000,Length=1000});
                if(!(empty && queued && legacy && progressShown && window.progress.Value==99 &&
                     window.status.Text.Contains(WaitingForSave) && window.detail.Text.Contains("Inbox 已接收")))return false;
            }
            return CheckUploadQueue() && CheckAutomaticReconnect();
        }

        static bool WaitFor(Func<bool> condition) {
            var timer=System.Diagnostics.Stopwatch.StartNew();
            while(!condition() && timer.ElapsedMilliseconds<5000) {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            return condition();
        }

        static bool CheckUploadQueue() {
            using(var window=new VietnamFileWindow(()=>true))
            using(var confirmFirst=new ManualResetEventSlim())
            using(var secondStarted=new ManualResetEventSlim())
            using(var failSecond=new ManualResetEventSlim()) {
                window.queue.Items.AddRange(new object[]{"first.bin","second.bin","third.bin"});
                Task upload=window.Upload((file,report,cancellation)=>{
                    if(file=="first.bin") {
                        report(100,100);
                        if(!confirmFirst.Wait(5000))throw new TimeoutException("Test save confirmation");
                        return file;
                    }
                    secondStarted.Set();
                    failSecond.Wait(5000);
                    throw new IOException("Test transfer failed");
                });
                bool awaitingSave=WaitFor(()=>window.progress.Value==99) && window.queue.Items.Count==3;
                confirmFirst.Set();
                bool removedDuringBatch=WaitFor(()=>secondStarted.IsSet) &&
                    window.queue.Items.Cast<string>().SequenceEqual(new[]{"second.bin","third.bin"});
                failSecond.Set();
                bool pendingAfterFailure=WaitFor(()=>upload.IsCompleted) && window.detail.Text.Contains("1/3") &&
                    window.queue.Items.Cast<string>().SequenceEqual(new[]{"second.bin","third.bin"});
                if(!(awaitingSave && removedDuringBatch && pendingAfterFailure && window.send.Text=="继续上传"))return false;
                Task paused=window.Upload((file,report,cancellation)=>{
                    report(35,100);
                    if(!cancellation.WaitHandle.WaitOne(5000))throw new TimeoutException("Test pause");
                    cancellation.ThrowIfCancellationRequested();
                    return file;
                });
                if(!WaitFor(()=>window.progress.Value==35) || !window.pause.ActionAvailable)return false;
                window.PauseUpload();
                if(!WaitFor(()=>paused.IsCompleted) || window.status.Text!="上传已暂停。" || window.pause.ActionAvailable ||
                    window.send.Text!="继续上传" || window.queue.Items.Count!=2 || window.progress.Value!=35)return false;
                var retried=new List<string>();
                Task retry=window.Upload((file,report,cancellation)=>{retried.Add(file);return file=="third.bin"?"third (1).bin":file;});
                return WaitFor(()=>retry.IsCompleted) && retried.SequenceEqual(new[]{"second.bin","third.bin"}) &&
                    window.queue.Items.Count==0 && window.progress.Value==100 && !window.send.ActionAvailable &&
                    window.status.Text=="远程桌面已保存 · third (1).bin";
            }
        }

        static bool CheckAutomaticReconnect() {
            bool connected=true;
            int reconnects=0, sends=0;
            using(var drop=new ManualResetEventSlim())
            using(var window=new VietnamFileWindow(()=>connected,token=>{
                connected=++reconnects==2;
                return Task.FromResult("等待模拟连接");
            },20)) {
                window.queue.Items.Add("retry.bin");
                Task upload=window.Upload((file,report,token)=>{
                    if(Interlocked.Increment(ref sends)==1) {
                        report(35,100);
                        if(!drop.Wait(5000))throw new TimeoutException("Test disconnect");
                        throw new VietnamTransfer.ConnectionInterrupted("Test disconnect");
                    }
                    return file;
                });
                bool confirmed=WaitFor(()=>window.progress.Value==35);
                connected=false;drop.Set();
                if(!confirmed || !WaitFor(()=>reconnects==1) || window.progress.Value!=35 || window.queue.Items.Count!=1 ||
                   !WaitFor(()=>upload.IsCompleted) || sends!=2 || reconnects!=2 || window.queue.Items.Count!=0 || window.progress.Value!=100) return false;
            }
            bool reconnecting=false;
            using(var window=new VietnamFileWindow(()=>false,async token=>{
                reconnecting=true;
                await Task.Delay(Timeout.Infinite,token);
                return "";
            },20)) {
                window.queue.Items.Add("paused.bin");
                Task upload=window.Upload((file,report,token)=>{throw new VietnamTransfer.ConnectionInterrupted("Test disconnect");});
                if(!WaitFor(()=>reconnecting) || !window.pause.ActionAvailable)return false;
                window.PauseUpload();
                return WaitFor(()=>upload.IsCompleted) && window.status.Text=="上传已暂停。" && window.queue.Items.Count==1;
            }
        }

        internal static void Preview(string path,bool sending) {
            using(var window=new VietnamFileWindow(()=>true)) {
                if(sending) {
                    window.queue.Items.AddRange(new object[]{"C:\\Logs\\现场日志.zip","C:\\Logs\\部署记录.txt"});
                    window.busy=true;window.RefreshButtons();window.progress.Visible=true;
                    window.ShowProgress(new UploadProgress {Index=1,Count=2,Path="现场日志.zip",Received=7340032,Length=12582912});
                }
                window.Show();Application.DoEvents();
                using(var bitmap=new Bitmap(window.Width,window.Height)) { window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png); }
                window.Hide();
            }
        }
    }
}
