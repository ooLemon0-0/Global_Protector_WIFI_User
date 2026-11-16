using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace GlobalProtectorWiFi {
    internal static class AppBrand {
        static readonly Bitmap logo=LoadLogo();
        static Bitmap LoadLogo(){using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Logo.png"))using(var image=new Bitmap(stream))return new Bitmap(image);}
        internal static Image Logo {get{return logo;}}
        internal static Icon CreateIcon(int size){using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("App.ico"))return new Icon(stream,size,size);}
    }
    internal sealed class LogoMark : Control {
        internal LogoMark(){SetStyle(ControlStyles.SupportsTransparentBackColor|ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);BackColor=Color.Transparent;}
        protected override void OnPaint(PaintEventArgs e){e.Graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;e.Graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;e.Graphics.DrawImage(AppBrand.Logo,ClientRectangle);}
    }
    internal sealed class CloseChoiceForm : Form {
        internal CloseChoiceForm() {
            Text="关闭窗口";ClientSize=new Size(420,230);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=false;
            StartPosition=FormStartPosition.CenterParent;BackColor=Color.White;ForeColor=Color.FromArgb(28,45,57);Font=new Font("Microsoft YaHei UI",9F);
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;Icon=AppBrand.CreateIcon(32);
            Controls.Add(new LogoMark{Bounds=new Rectangle(24,22,44,44)});
            Controls.Add(new Label{Text="关闭 Global Protector",Bounds=new Rectangle(82,26,314,35),Font=new Font(Font.FontFamily,14,FontStyle.Bold)});
            Controls.Add(new Label{Text="最小化后，助手会继续运行。\r\n退出会停止助手并恢复临时网络设置。",Bounds=new Rectangle(24,85,372,48),ForeColor=Color.FromArgb(99,116,128)});
            var minimize=new SoftButton{Text="最小化到托盘",Bounds=new Rectangle(24,162,218,42),DialogResult=DialogResult.Yes};
            var exit=new SoftButton{Text="退出",Bounds=new Rectangle(254,162,142,42),DialogResult=DialogResult.No};
            foreach(var button in new[]{minimize,exit}){button.FlatStyle=FlatStyle.Flat;button.Cursor=Cursors.Hand;button.FlatAppearance.BorderSize=1;button.Font=new Font(Font,FontStyle.Bold);}
            minimize.BackColor=Color.FromArgb(13,125,113);minimize.ForeColor=Color.White;minimize.FlatAppearance.BorderColor=minimize.BackColor;
            exit.BackColor=Color.White;exit.ForeColor=ForeColor;exit.FlatAppearance.BorderColor=Color.FromArgb(214,224,228);
            Controls.Add(minimize);Controls.Add(exit);AcceptButton=minimize;
        }
        protected override bool ProcessDialogKey(Keys keyData){if(keyData==Keys.Escape){DialogResult=DialogResult.Cancel;return true;}return base.ProcessDialogKey(keyData);}
        protected override void Dispose(bool disposing){var icon=Icon;base.Dispose(disposing);if(disposing && icon!=null)icon.Dispose();}
    }
}
