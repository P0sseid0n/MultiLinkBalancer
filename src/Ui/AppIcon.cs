// Ícone do MultiLink: bloco grafite (o fundo da interface) com três links que convergem num só.
// O mesmo desenho gera o ícone da bandeja (verde ou vermelho) e o .ico do executável (build.ps1).
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace MultiLink.Ui
{
    public static class AppIcon
    {
        public static readonly Color On = Color.FromArgb(0x4c, 0xc3, 0x8a);    // --on da interface
        public static readonly Color Alert = Color.FromArgb(0xe5, 0x53, 0x4b); // --critical
        static readonly Color TileTop = Color.FromArgb(0x26, 0x2d, 0x36), TileBottom = Color.FromArgb(0x13, 0x17, 0x1c);

        static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

        // Desenha numa grade de 64 unidades; em tamanhos pequenos os traços engrossam para continuar legíveis.
        public static Bitmap Render(int size, Color accent)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform(size / 64f, size / 64f);
                bool small = size <= 24;
                DrawTile(graphics, small);
                DrawLinks(graphics, small, accent);
            }
            return bitmap;
        }

        // Bloco grafite com luz vinda de cima.
        static void DrawTile(Graphics graphics, bool small)
        {
            using (var tile = small ? Rounded(new RectangleF(0, 0, 64, 64), 13) : Rounded(new RectangleF(2, 2, 60, 60), 15))
            {
                using (var fill = new LinearGradientBrush(new PointF(0, 2), new PointF(0, 62), TileTop, TileBottom))
                    graphics.FillPath(fill, tile);
                if (!small)
                    using (var edge = new Pen(Color.FromArgb(40, 255, 255, 255), 1.5f))
                        graphics.DrawPath(edge, tile);
            }
        }

        // Três links entrando no mesmo ponto. Nos tamanhos da bandeja: desenho maior, nós mais
        // afastados e linhas retas (curvas viram borrão).
        static void DrawLinks(Graphics graphics, bool small, Color accent)
        {
            float stroke = small ? 7f : 4.5f, inputRadius = small ? 6.5f : 4.5f, hubRadius = small ? 10f : 7.5f;
            float left = small ? 15 : 18;
            var hub = new PointF(small ? 47 : 44, 32);
            float[] inputsY = small ? new float[] { 14, 32, 50 } : new float[] { 17, 32, 47 };

            using (var pen = new Pen(accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                foreach (var y in inputsY)
                {
                    if (small || y == 32) graphics.DrawLine(pen, left, y, hub.X, hub.Y);
                    else graphics.DrawBezier(pen, left, y, 31, y, 31, hub.Y, hub.X, hub.Y);
                }

            using (var brush = new SolidBrush(accent))
            {
                foreach (var y in inputsY) graphics.FillEllipse(brush, left - inputRadius, y - inputRadius, inputRadius * 2, inputRadius * 2);
                graphics.FillEllipse(brush, hub.X - hubRadius, hub.Y - hubRadius, hubRadius * 2, hubRadius * 2);
            }
            // miolo escuro no nó de saída: dá leitura de "conector" nos tamanhos maiores
            if (!small)
                using (var hole = new SolidBrush(TileBottom))
                    graphics.FillEllipse(hole, hub.X - 3f, hub.Y - 3f, 6f, 6f);
        }

        static GraphicsPath Rounded(RectangleF rectangle, float radius)
        {
            float diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        // .ico com todos os tamanhos (cada um em PNG), para o Windows escolher o certo em cada DPI.
        public static byte[] IcoBytes(Color accent)
        {
            var images = new byte[IcoSizes.Length][];
            for (int i = 0; i < IcoSizes.Length; i++)
                using (var bitmap = Render(IcoSizes[i], accent))
                using (var png = new MemoryStream())
                {
                    bitmap.Save(png, ImageFormat.Png);
                    images[i] = png.ToArray();
                }
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output))
            {
                // ICONDIR: reservado, tipo 1 (ícone), quantidade
                writer.Write((short)0); writer.Write((short)1); writer.Write((short)IcoSizes.Length);
                int offset = 6 + 16 * IcoSizes.Length;
                for (int i = 0; i < IcoSizes.Length; i++)
                {
                    // ICONDIRENTRY: largura e altura (0 = 256), cores, reservado, planos, bits por pixel, tamanho, posição
                    byte side = (byte)(IcoSizes[i] >= 256 ? 0 : IcoSizes[i]);
                    writer.Write(side); writer.Write(side);
                    writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((short)1); writer.Write((short)32);
                    writer.Write(images[i].Length); writer.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var image in images) writer.Write(image);
                writer.Flush();
                return output.ToArray();
            }
        }

        public static void WriteIco(string path) { File.WriteAllBytes(path, IcoBytes(On)); }
    }
}
