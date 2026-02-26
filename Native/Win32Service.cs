using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Butterfly.Native
{
    public static class Win32Service
    {
        #region DWM API (Dark Mode)

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        #endregion

        #region Window Management

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpWindow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public const int GWL_EXSTYLE = -20;

        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_HIDEWINDOW = 0x0080;
        public const uint SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetWindowText(IntPtr hWnd, string lpString);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

        #endregion

        #region Window Messages

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public const uint WM_CHAR = 0x0102;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_LBUTTONDBLCLK = 0x0203;
        public const uint WM_SETTEXT = 0x000C;
        public const uint WM_CLOSE = 0x0010;
        public const uint BM_CLICK = 0x00F5;

        public const int VK_RETURN = 0x0D;
        public const int VK_TAB = 0x09;
        public const int VK_F12 = 0x7B;

        #endregion

        #region Input Control

        [DllImport("user32.dll")]
        public static extern bool BlockInput(bool fBlockIt);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;

        #endregion

        #region DPI Awareness

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        public const int LOGPIXELSX = 88;

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        #endregion

        #region Window Rectangles and Coordinates

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        #endregion

        #region Window Show Commands

        public const int SW_HIDE = 0;
        public const int SW_MINIMIZE = 6;
        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;

        #endregion

        #region Screen Capture and Pixel Detection

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

        #endregion

        #region Window Position Storage

        private struct WindowPosition
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public int ExtendedStyle;
        }

        private static Dictionary<IntPtr, WindowPosition> _savedWindowPositions = new Dictionary<IntPtr, WindowPosition>();

        private const int OFF_SCREEN_X = -32000;
        private const int OFF_SCREEN_Y = -32000;

        private const int LIMBO_THRESHOLD = -30000;

        private const int FALLBACK_X = 100;
        private const int FALLBACK_Y = 100;

        #endregion

        #region Helper Methods

        public static string GetClassName(IntPtr hWnd)
        {
            StringBuilder className = new StringBuilder(256);
            GetClassName(hWnd, className, className.Capacity);
            return className.ToString();
        }

        private static bool IsGhostWindow(IntPtr hWnd, IntPtr mainWindowHandle)
        {
            if (hWnd == mainWindowHandle)
                return false;

            if (!IsWindow(hWnd))
                return false;

            int textLength = GetWindowTextLength(hWnd);
            bool hasNoTitle = textLength == 0;

            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            bool isToolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;

            bool hasInvalidSize = false;
            if (GetWindowRect(hWnd, out RECT rect))
            {
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                hasInvalidSize = width == 0 || height == 0;
            }

            string className = GetClassName(hWnd);
            bool isSuspiciousClass = className.Equals("IME", StringComparison.OrdinalIgnoreCase) ||
                                     className.Contains("MSCTFIME", StringComparison.OrdinalIgnoreCase) ||
                                     className.StartsWith("MSCTF", StringComparison.OrdinalIgnoreCase);

            bool isGhost = (isToolWindow && hasNoTitle) ||
                           hasInvalidSize ||
                           isSuspiciousClass;

            return isGhost;
        }

        public static int ToggleGameWindows(Process process, bool show)
        {
            if (process == null || process.HasExited)
                return 0;

            int windowCount = 0;
            uint processId = (uint)process.Id;
            List<IntPtr> windows = new List<IntPtr>();

            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowProcessId);
                if (windowProcessId == processId)
                {
                    windows.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);

            process.Refresh();
            IntPtr mainWindowHandle = process.MainWindowHandle;

            foreach (IntPtr hWnd in windows)
            {
                if (IsWindow(hWnd))
                {
                    if (show)
                    {
                        if (IsGhostWindow(hWnd, mainWindowHandle))
                        {
                            string className = GetClassName(hWnd);
                            PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            Console.WriteLine($"Ghost window detected and closed: {hWnd} - {className}");
                            continue;
                        }

                        if (GetWindowTextLength(hWnd) > 0)
                        {
                            bool isInLimbo = false;
                            
                            if (GetWindowRect(hWnd, out RECT currentRect))
                            {
                                isInLimbo = currentRect.Left < LIMBO_THRESHOLD || currentRect.Top < LIMBO_THRESHOLD;
                            }
                            
                            if (isInLimbo || _savedWindowPositions.ContainsKey(hWnd))
                            {
                                int restoreX, restoreY;
                                int restoreStyle;
                                
                                if (_savedWindowPositions.ContainsKey(hWnd))
                                {
                                    WindowPosition savedPos = _savedWindowPositions[hWnd];
                                    restoreX = savedPos.X;
                                    restoreY = savedPos.Y;
                                    restoreStyle = savedPos.ExtendedStyle;
                                    
                                    _savedWindowPositions.Remove(hWnd);
                                }
                                else
                                {
                                    int windowWidth = 0;
                                    int windowHeight = 0;
                                    
                                    if (GetWindowRect(hWnd, out RECT fallbackRect))
                                    {
                                        windowWidth = fallbackRect.Right - fallbackRect.Left;
                                        windowHeight = fallbackRect.Bottom - fallbackRect.Top;
                                    }
                                    
                                    int screenWidth = GetSystemMetrics(SM_CXSCREEN);
                                    int screenHeight = GetSystemMetrics(SM_CYSCREEN);
                                    
                                    if (windowWidth > screenWidth)
                                        restoreX = 0;
                                    else
                                        restoreX = (screenWidth - windowWidth) / 2;
                                    
                                    if (windowHeight > screenHeight)
                                        restoreY = 0;
                                    else
                                        restoreY = (screenHeight - windowHeight) / 2;
                                    
                                    restoreStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                                }
                                
                                int newStyle = restoreStyle;
                                newStyle &= ~WS_EX_TOOLWINDOW;
                                newStyle |= WS_EX_APPWINDOW;
                                
                                SetWindowLong(hWnd, GWL_EXSTYLE, newStyle);
                                
                                SetWindowPos(
                                    hWnd,
                                    IntPtr.Zero,
                                    restoreX,
                                    restoreY,
                                    0,
                                    0,
                                    SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW | SWP_FRAMECHANGED
                                );
                                
                                SetForegroundWindow(hWnd);
                                
                                windowCount++;
                            }
                            else
                            {
                                ShowWindow(hWnd, SW_SHOW);
                                windowCount++;
                            }
                        }
                    }
                    else
                    {
                        if (GetWindowRect(hWnd, out RECT rect))
                        {
                            int width = rect.Right - rect.Left;
                            int height = rect.Bottom - rect.Top;
                            
                            if (rect.Left != OFF_SCREEN_X || rect.Top != OFF_SCREEN_Y)
                            {
                                int originalStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                                
                                WindowPosition originalPos = new WindowPosition
                                {
                                    X = rect.Left,
                                    Y = rect.Top,
                                    Width = width,
                                    Height = height,
                                    ExtendedStyle = originalStyle
                                };
                                
                                _savedWindowPositions[hWnd] = originalPos;
                                
                                int newStyle = originalStyle;
                                newStyle &= ~WS_EX_APPWINDOW;
                                newStyle |= WS_EX_TOOLWINDOW;
                                
                                SetWindowLong(hWnd, GWL_EXSTYLE, newStyle);
                                
                                SetWindowPos(
                                    hWnd,
                                    IntPtr.Zero,
                                    OFF_SCREEN_X,
                                    OFF_SCREEN_Y,
                                    0,
                                    0,
                                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED
                                );
                                
                                windowCount++;
                            }
                        }
                    }
                }
            }

            CleanupInvalidWindowPositions();

            return windowCount;
        }

        private static void CleanupInvalidWindowPositions()
        {
            List<IntPtr> invalidHandles = new List<IntPtr>();
            
            foreach (IntPtr hWnd in _savedWindowPositions.Keys)
            {
                if (!IsWindow(hWnd))
                {
                    invalidHandles.Add(hWnd);
                }
            }
            
            foreach (IntPtr hWnd in invalidHandles)
            {
                _savedWindowPositions.Remove(hWnd);
            }
        }

        #endregion
    }
}
