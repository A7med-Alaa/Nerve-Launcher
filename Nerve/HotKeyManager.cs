using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Nerve
{
    internal class HotKeyManager
    {
        [DllImport("User32.dll")]
        private static extern bool RegisterHotKey([In] IntPtr hWnd, [In] int id, [In] uint fsModifiers, [In] uint vk);

        [DllImport("User32.dll")]
        private static extern bool UnregisterHotKey([In] IntPtr hWnd, [In] int id);

        private readonly Window _window;
        private readonly int _hotkeyID;

        
        private const int WM_HOTKEY = 0x0312;

        private Action _onPressed;

        public HotKeyManager(Window window, int hotkeyID) {
            _window = window;
            _hotkeyID = hotkeyID;
        }

        public void Register(uint modifiers, uint key, Action onPressed)
        {
            _onPressed = onPressed;
            var helper = new WindowInteropHelper(_window);
            var source = HwndSource.FromHwnd(helper.Handle);
            source.AddHook(hotkeyHook);

            RegisterHotKey(helper.Handle, _hotkeyID, modifiers, key);
        } 

        public void Unregister()
        {
            var helper = new WindowInteropHelper(_window);
            UnregisterHotKey(helper.Handle, _hotkeyID);
        }
        
        private nint hotkeyHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == _hotkeyID) {
                _onPressed?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }
    }
}
