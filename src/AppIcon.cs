// Ícone do MultiLink: bloco grafite (o fundo da interface) com três links que convergem num só.
// O mesmo desenho gera o ícone da bandeja (verde ou vermelho) e o .ico do executável (build.ps1).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace MultiLink
{
    public static class AppIcon
    {
        public static readonly Color On = Color.FromArgb(0x4c, 0xc3, 0x8a);    // --on da interface
        public static readonly Color Alert = Color.FromArgb(0xe5, 0x53, 0x4b); // --critical

        static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

        // Desenha numa grade de 64 unidades; em tamanhos pequenos os traços engrossam para continuar legíveis.
        public static Bitmap Render(int size, Color accent)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 64f, size / 64f);
                bool small = size <= 24;

                // bloco: grafite com luz vinda de cima
                using (var tile = small ? Rounded(new RectangleF(0, 0, 64, 64), 13) : Rounded(new RectangleF(2, 2, 60, 60), 15))
                {
                    using (var fill = new LinearGradientBrush(new PointF(0, 2), new PointF(0, 62),
                        Color.FromArgb(0x26, 0x2d, 0x36), Color.FromArgb(0x13, 0x17, 0x1c)))
                        g.FillPath(fill, tile);
                    if (!small)
                        using (var edge = new Pen(Color.FromArgb(40, 255, 255, 255), 1.5f))
                            g.DrawPath(edge, tile);
                }

                // nos tamanhos da bandeja: desenho maior, nós mais afastados e linhas retas (curvas viram borrão)
                float stroke = small ? 7f : 4.5f, rIn = small ? 6.5f : 4.5f, rOut = small ? 10f : 7.5f;
                float left = small ? 15 : 18;
                var hub = new PointF(small ? 47 : 44, 32);
                float[] ys = small ? new float[] { 14, 32, 50 } : new float[] { 17, 32, 47 };

                // três links entrando no mesmo ponto
                using (var pen = new Pen(accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    foreach (var y in ys)
                    {
                        if (small || y == 32) g.DrawLine(pen, left, y, hub.X, hub.Y);
                        else g.DrawBezier(pen, left, y, 31, y, 31, hub.Y, hub.X, hub.Y);
                    }

                using (var b = new SolidBrush(accent))
                {
                    foreach (var y in ys) g.FillEllipse(b, left - rIn, y - rIn, rIn * 2, rIn * 2);
                    g.FillEllipse(b, hub.X - rOut, hub.Y - rOut, rOut * 2, rOut * 2);
                }
                // miolo escuro no nó de saída: dá leitura de "conector" nos tamanhos maiores
                if (!small)
                    using (var hole = new SolidBrush(Color.FromArgb(0x13, 0x17, 0x1c)))
                        g.FillEllipse(hole, hub.X - 3f, hub.Y - 3f, 6f, 6f);
            }
            return bmp;
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // .ico com todos os tamanhos (cada um em PNG), para o Windows escolher o certo em cada DPI
        public static byte[] IcoBytes(Color accent)
        {
            var images = new byte[Sizes.Length][];
            for (int i = 0; i < Sizes.Length; i++)
                using (var bmp = Render(Sizes[i], accent))
                using (var png = new MemoryStream())
                {
                    bmp.Save(png, ImageFormat.Png);
                    images[i] = png.ToArray();
                }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)Sizes.Length);
                int offset = 6 + 16 * Sizes.Length;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    int s = Sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Length); w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static void WriteIco(string path) { File.WriteAllBytes(path, IcoBytes(On)); }
    }
}
