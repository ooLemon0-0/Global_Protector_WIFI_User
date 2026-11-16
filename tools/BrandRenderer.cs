using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class BrandRenderer {
    static GraphicsPath Tile() {
        var p=new GraphicsPath();p.AddArc(8,8,120,120,180,90);p.AddArc(128,8,120,120,270,90);p.AddArc(128,128,120,120,0,90);p.AddArc(8,128,120,120,90,90);p.CloseFigure();return p;
    }
    static Bitmap Render(int size) {
        using(var large=new Bitmap(size*4,size*4,PixelFormat.Format32bppArgb)) {
            using(var g=Graphics.FromImage(large)) {
                g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.ScaleTransform(large.Width/256F,large.Height/256F);
                using(var tile=Tile())using(var fill=new LinearGradientBrush(new Point(32,8),new Point(216,248),Color.FromArgb(24,178,163),Color.FromArgb(6,91,101)))using(var border=new Pen(Color.FromArgb(40,255,255,255),2)) {g.FillPath(fill,tile);g.DrawPath(border,tile);}
                using(var shield=new GraphicsPath()) {
                    shield.AddBezier(128,48,146,61,168,69,187,73);shield.AddLine(187,73,187,121);
                    shield.AddBezier(187,121,187,162,166,192,128,211);shield.AddBezier(128,211,90,192,69,162,69,121);
                    shield.AddLine(69,121,69,73);shield.AddBezier(69,73,88,69,110,61,128,48);shield.CloseFigure();
                    using(var fill=new SolidBrush(Color.FromArgb(250,255,253)))g.FillPath(fill,shield);
                }
                using(var pen=new Pen(Color.FromArgb(9,115,116),size<=24?16:13)) {
                    pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;
                    g.DrawBezier(pen,96,112,114,95,142,95,160,112);
                    if(size>24){pen.Width=12;g.DrawBezier(pen,109,132,120,122,136,122,147,132);}
                }
                float center=size<=24?143:154;float radius=size<=24?10:8;
                using(var dot=new SolidBrush(Color.FromArgb(9,115,116)))g.FillEllipse(dot,128-radius,center-radius,radius*2,radius*2);
            }
            var result=new Bitmap(size,size,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(result)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.DrawImage(large,new Rectangle(0,0,size,size),0,0,large.Width,large.Height,GraphicsUnit.Pixel);}
            return result;
        }
    }
    static byte[] IconFrame(Bitmap bitmap) {
        using(var stream=new MemoryStream()) {
            if(bitmap.Width>=128){bitmap.Save(stream,ImageFormat.Png);return stream.ToArray();}
            int size=bitmap.Width,maskStride=((size+31)/32)*4;
            using(var w=new BinaryWriter(stream)) {
                w.Write(40);w.Write(size);w.Write(size*2);w.Write((short)1);w.Write((short)32);w.Write(0);w.Write(size*size*4+maskStride*size);w.Write(0);w.Write(0);w.Write(0);w.Write(0);
                for(int y=size-1;y>=0;y--)for(int x=0;x<size;x++){Color c=bitmap.GetPixel(x,y);w.Write(c.B);w.Write(c.G);w.Write(c.R);w.Write(c.A);}
                for(int y=size-1;y>=0;y--){var row=new byte[maskStride];for(int x=0;x<size;x++)if(bitmap.GetPixel(x,y).A<128)row[x/8]|=(byte)(0x80>>(x%8));w.Write(row);}
                return stream.ToArray();
            }
        }
    }
    static int Main(string[] args) {
        string assets=Path.GetFullPath(args[0]);Directory.CreateDirectory(assets);
        using(var logo=Render(256))logo.Save(Path.Combine(assets,"logo.png"),ImageFormat.Png);
        int[] sizes={16,20,24,32,40,48,64,128,256};var frames=new List<byte[]>();
        foreach(int size in sizes)using(var frame=Render(size))frames.Add(IconFrame(frame));
        using(var stream=File.Create(Path.Combine(assets,"app.ico")))using(var w=new BinaryWriter(stream)) {
            w.Write((short)0);w.Write((short)1);w.Write((short)sizes.Length);int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++){w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)0);w.Write((byte)0);w.Write((short)1);w.Write((short)32);w.Write(frames[i].Length);w.Write(offset);offset+=frames[i].Length;}
            foreach(byte[] frame in frames)w.Write(frame);
        }
        Console.WriteLine("Generated logo.png and a nine-size Windows app.ico.");return 0;
    }
}
