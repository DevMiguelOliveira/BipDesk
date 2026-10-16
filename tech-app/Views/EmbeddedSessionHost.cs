using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BipDesk.Tech.Views
{
    public class EmbeddedSessionHost : HwndHost
    {
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_EX_APPWINDOW = 0x00040000;

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint WM_CLOSE = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetWindowText(IntPtr hWnd, string lpString);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        private readonly IntPtr _childHwnd;
        private bool _isDisposed;

        public IntPtr ChildHandle => _childHwnd;

        public EmbeddedSessionHost(IntPtr childHwnd, string title = "BipDesk - Sessão Remota")
        {
            _childHwnd = childHwnd;
            Focusable = true;

            MouseDown += (s, e) =>
            {
                Focus();
                if (_childHwnd != IntPtr.Zero)
                {
                    SetFocus(_childHwnd);
                }
            };

            GotFocus += (s, e) =>
            {
                if (_childHwnd != IntPtr.Zero)
                {
                    SetFocus(_childHwnd);
                }
            };

            try
            {
                SetWindowText(_childHwnd, title);
            }
            catch { }
        }


        public void CloseSessionWindow()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                if (_childHwnd != IntPtr.Zero)
                {
                    try
                    {
                        PostMessage(_childHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch { }
                }
            }
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            if (_childHwnd != IntPtr.Zero)
            {
                // Esconde brevemente para desregistrar da barra de tarefas de forma imperceptível
                ShowWindow(_childHwnd, 0 /* SW_HIDE */);

                // Transforma a janela RustDesk em controle filho integrado (WS_CHILD) e remove bordas de popup e botões de janela nativos
                var style = GetWindowLong(_childHwnd, GWL_STYLE);
                style = (style & ~WS_POPUP & ~WS_CAPTION & ~WS_THICKFRAME & ~WS_MINIMIZEBOX & ~WS_MAXIMIZEBOX) | WS_CHILD | 0x00010000 /* WS_TABSTOP */;
                SetWindowLong(_childHwnd, GWL_STYLE, style);

                // Remove do alternador de janelas e barra de tarefas do Windows
                var exStyle = GetWindowLong(_childHwnd, GWL_EXSTYLE);
                exStyle &= ~WS_EX_APPWINDOW;
                SetWindowLong(_childHwnd, GWL_EXSTYLE, exStyle);

                // Acopla a janela na árvore visual do BipDesk Admin
                SetParent(_childHwnd, hwndParent.Handle);

                // Ajusta dimensões iniciais
                var width = ActualWidth > 0 ? (int)ActualWidth : 1200;
                var height = ActualHeight > 0 ? (int)ActualHeight : 700;

                SetWindowPos(_childHwnd, IntPtr.Zero, 0, 0, width, height,
                    SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                ShowWindow(_childHwnd, 5 /* SW_SHOW */);
                SetFocus(_childHwnd);
            }

            return new HandleRef(this, _childHwnd);
        }

        protected override bool TabIntoCore(System.Windows.Input.TraversalRequest request)
        {
            if (_childHwnd != IntPtr.Zero)
            {
                SetFocus(_childHwnd);
                return true;
            }
            return base.TabIntoCore(request);
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SETFOCUS = 0x0007;
            const int WM_MOUSEACTIVATE = 0x0021;
            if (msg == WM_MOUSEACTIVATE || msg == WM_SETFOCUS)
            {
                if (_childHwnd != IntPtr.Zero)
                {
                    SetFocus(_childHwnd);
                }
            }
            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }


        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            // Apenas encerra o processo remoto se a sessão foi explicitamente fechada
            if (_isDisposed && _childHwnd != IntPtr.Zero)
            {
                try
                {
                    PostMessage(_childHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }
            }
            else if (!_isDisposed && _childHwnd != IntPtr.Zero)
            {
                // Desacopla temporariamente da hierarquia para prevenir que o Windows destrua a janela durante re-layout ou troca de abas
                try
                {
                    SetParent(_childHwnd, IntPtr.Zero);
                    ShowWindow(_childHwnd, 0 /* SW_HIDE */);
                }
                catch { }
            }
        }

        protected override void Dispose(bool disposing)
        {
            CloseSessionWindow();
            base.Dispose(disposing);
        }

        protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (_childHwnd != IntPtr.Zero && sizeInfo.NewSize.Width > 0 && sizeInfo.NewSize.Height > 0)
            {
                SetWindowPos(_childHwnd, IntPtr.Zero, 0, 0, (int)sizeInfo.NewSize.Width, (int)sizeInfo.NewSize.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
    }
}
