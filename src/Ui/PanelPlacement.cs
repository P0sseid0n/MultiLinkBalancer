// Onde o painel abre: no canto da área de trabalho junto à barra de tarefas (onde fica a bandeja),
// alinhado ao ponto de onde foi aberto, como os painéis do próprio Windows.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MultiLink.Ui
{
    class PanelPlacement
    {
        Point anchor;
        Rectangle trayOverflow; // menu de ícones ocultos (^) aberto no clique; vazio se o ícone está na barra

        // Distância do painel até a barra de tarefas. Definida depois de criar a janela (depende do DPI);
        // até lá vale 0, porque o Windows já pede a posição enquanto a janela está sendo criada.
        public int Gap { get; set; }

        // Aberto pelo ícone da bandeja: junto ao cursor (e acima do menu de ícones ocultos, se for de lá).
        public void AnchorAtCursor(IntPtr ownWindow)
        {
            anchor = Cursor.Position;
            trayOverflow = OverflowMenuUnder(anchor, ownWindow);
        }

        // Aberto pelo .exe ou pelo atalho: no canto da bandeja da tela principal.
        public void AnchorAtPrimaryTray()
        {
            var workArea = Screen.PrimaryScreen.WorkingArea;
            anchor = new Point(workArea.Right, workArea.Bottom);
            trayOverflow = Rectangle.Empty;
        }

        public Point Position(Size size)
        {
            var screen = Screen.FromPoint(anchor);
            Rectangle workArea = screen.WorkingArea, bounds = screen.Bounds;
            bool taskbarLeft = workArea.Left > bounds.Left, taskbarRight = workArea.Right < bounds.Right, taskbarTop = workArea.Top > bounds.Top;
            int x, y;
            if (taskbarLeft) { x = workArea.Left + Gap; y = anchor.Y - size.Height / 2; }
            else if (taskbarRight) { x = workArea.Right - size.Width - Gap; y = anchor.Y - size.Height / 2; }
            else
            {
                x = anchor.X - size.Width / 2;
                y = taskbarTop ? workArea.Top + Gap : workArea.Bottom - size.Height - Gap;
            }
            if (!trayOverflow.IsEmpty) // abre acima (ou abaixo, com a barra em cima) do menu de ícones ocultos
                y = taskbarTop ? trayOverflow.Bottom + Gap : trayOverflow.Top - size.Height - Gap;
            x = Math.Max(workArea.Left + Gap, Math.Min(x, workArea.Right - size.Width - Gap));
            y = Math.Max(workArea.Top + Gap, Math.Min(y, workArea.Bottom - size.Height - Gap));
            return new Point(x, y);
        }

        // Janela sob o cursor no clique: se não for a própria barra de tarefas, é o menu de ícones ocultos.
        static Rectangle OverflowMenuUnder(Point point, IntPtr ownWindow)
        {
            var window = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = point.X, Y = point.Y }), NativeMethods.GA_ROOT);
            if (window == IntPtr.Zero || window == ownWindow) return Rectangle.Empty;
            var className = new StringBuilder(256);
            NativeMethods.GetClassName(window, className, className.Capacity);
            if (className.ToString().StartsWith("Shell_", StringComparison.Ordinal)) return Rectangle.Empty; // Shell_TrayWnd, Shell_SecondaryTrayWnd
            NativeMethods.RECT frame;
            if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out frame, Marshal.SizeOf(typeof(NativeMethods.RECT))) != 0)
                return Rectangle.Empty;
            var rectangle = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
            return Screen.FromPoint(point).WorkingArea.Contains(rectangle) ? rectangle : Rectangle.Empty;
        }
    }
}
