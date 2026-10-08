using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using OleMsg = Microsoft.VisualStudio.OLE.Interop.MSG;

namespace UnrealSense.Extension.GoTo
{
    /// <summary>
    /// Gives a modeless WPF window first claim on keyboard messages. Visual Studio's message loop translates keys
    /// such as Backspace, Delete, Home/End, arrows and Ctrl+A into commands for the active document before WPF ever
    /// sees them, so without this they would edit the file under the popup instead of its text box. Registering an
    /// <see cref="IOleComponent"/> lets us dispatch those messages straight to the window, as a modal dialog would.
    /// </summary>
    internal sealed class KeyboardInputFilter : IOleComponent, IDisposable
    {
        const int WM_KEYFIRST = 0x0100, WM_KEYLAST = 0x0109;

        readonly IOleComponentManager manager;
        readonly IntPtr hwnd;
        uint componentId;

        KeyboardInputFilter(IOleComponentManager manager, IntPtr hwnd)
        {
            this.manager = manager;
            this.hwnd = hwnd;
        }

        public static KeyboardInputFilter Attach(Window window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (!(ServiceProvider.GlobalProvider.GetService(typeof(SOleComponentManager)) is IOleComponentManager manager)) return null;

            var filter = new KeyboardInputFilter(manager, hwnd);
            var info = new OLECRINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(OLECRINFO)),
                grfcrf = (uint)(_OLECRF.olecrfPreTranslateKeys | _OLECRF.olecrfPreTranslateAll),
                grfcadvf = 0,
            };
            if (manager.FRegisterComponent(filter, new[] { info }, out filter.componentId) == 0) return null;
            window.Activated += (s, e) => filter.Activate();
            window.Closed += (s, e) => filter.Dispose();
            return filter;
        }

        void Activate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (componentId != 0) manager.FOnComponentActivate(componentId);
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (componentId == 0) return;
            manager.FRevokeComponent(componentId);
            componentId = 0;
        }

        public int FPreTranslateMessage(OleMsg[] pMsg)
        {
            if (pMsg == null || pMsg.Length == 0) return 0;
            var m = pMsg[0];
            if (m.message < WM_KEYFIRST || m.message > WM_KEYLAST) return 0;
            if (m.hwnd != hwnd && !IsChild(hwnd, m.hwnd)) return 0;

            // Same thing WPF's own dispatcher loop does: let WPF pre-process (input bindings), then translate and dispatch.
            var wpf = new System.Windows.Interop.MSG
            {
                hwnd = m.hwnd,
                message = (int)m.message,
                wParam = m.wParam,
                lParam = m.lParam,
                time = (int)m.time,
                pt_x = m.pt.x,
                pt_y = m.pt.y,
            };
            if (!ComponentDispatcher.RaiseThreadMessage(ref wpf))
            {
                var native = new NativeMsg { hwnd = m.hwnd, message = m.message, wParam = m.wParam, lParam = m.lParam, time = m.time, x = m.pt.x, y = m.pt.y };
                TranslateMessage(ref native);
                DispatchMessage(ref native);
            }
            return 1;
        }

        public int FReserved1(uint dwReserved, uint message, IntPtr wParam, IntPtr lParam) => 1;
        public void OnEnterState(uint uStateID, int fEnter) { }
        public void OnAppActivate(int fActive, uint dwOtherThreadID) { }
        public void OnLoseActivation() { }
        public void OnActivationChange(IOleComponent pic, int fSameComponent, OLECRINFO[] pcrinfo, int fHostIsActivating, OLECHOSTINFO[] pchostinfo, uint dwReserved) { }
        public int FDoIdle(uint grfidlef) => 0;
        public int FContinueMessageLoop(uint uReason, IntPtr pvLoopData, OleMsg[] pMsgPeeked) => 1;
        public int FQueryTerminate(int fPromptUser) => 1;
        public void Terminate() { }
        public IntPtr HwndGetWindow(uint dwWhich, uint dwReserved) => hwnd;

        [StructLayout(LayoutKind.Sequential)]
        struct NativeMsg
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x;
            public int y;
        }

        [DllImport("user32.dll")]
        static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref NativeMsg msg);

        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessage(ref NativeMsg msg);
    }
}
