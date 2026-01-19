using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Butterfly.Native
{
    /// <summary>
    /// Static service to encapsulate all Win32 API calls (DllImport)
    /// </summary>
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

        // Indices for GetWindowLong/SetWindowLong
        public const int GWL_EXSTYLE = -20;  // Extended window styles

        // Extended window styles
        public const int WS_EX_TOOLWINDOW = 0x00000080;  // Removes from Taskbar
        public const int WS_EX_APPWINDOW = 0x00040000;   // Shows in Taskbar

        // Flags for SetWindowPos
        public const uint SWP_NOSIZE = 0x0001;      // Maintains current size (ignores cx and cy)
        public const uint SWP_NOMOVE = 0x0002;      // Maintains current position (ignores X and Y)
        public const uint SWP_NOZORDER = 0x0004;    // Maintains current Z order (ignores hWndInsertAfter)
        public const uint SWP_NOACTIVATE = 0x0010;  // Does not activate the window
        public const uint SWP_SHOWWINDOW = 0x0040;  // Shows the window
        public const uint SWP_HIDEWINDOW = 0x0080;  // Hides the window
        public const uint SWP_FRAMECHANGED = 0x0020; // Forces window frame update (applies style changes)

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

        // Constantes de mensagens do Windows
        public const uint WM_CHAR = 0x0102;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_LBUTTONDBLCLK = 0x0203;
        public const uint WM_SETTEXT = 0x000C;
        public const uint WM_CLOSE = 0x0010;
        public const uint BM_CLICK = 0x00F5;

        // Virtual Key Codes
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

        public const int LOGPIXELSX = 88; // Logical pixels/inch in X

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        public const int SM_CXSCREEN = 0; // Largura da tela
        public const int SM_CYSCREEN = 1; // Altura da tela

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

        /// <summary>
        /// Structure to store original coordinates and extended style of a window
        /// </summary>
        private struct WindowPosition
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public int ExtendedStyle;  // Original extended style (GWL_EXSTYLE)
        }

        /// <summary>
        /// Static dictionary to store original coordinates of windows before moving them off-screen
        /// </summary>
        private static Dictionary<IntPtr, WindowPosition> _savedWindowPositions = new Dictionary<IntPtr, WindowPosition>();

        /// <summary>
        /// Default off-screen coordinates (outside the visible area of any monitor)
        /// </summary>
        private const int OFF_SCREEN_X = -32000;
        private const int OFF_SCREEN_Y = -32000;

        /// <summary>
        /// Threshold to detect windows "in limbo" (off-screen)
        /// </summary>
        private const int LIMBO_THRESHOLD = -30000;

        /// <summary>
        /// Default fallback position for restored windows when there is no saved position
        /// </summary>
        private const int FALLBACK_X = 100;
        private const int FALLBACK_Y = 100;

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets the class name of a window
        /// </summary>
        public static string GetClassName(IntPtr hWnd)
        {
            StringBuilder className = new StringBuilder(256);
            GetClassName(hWnd, className, className.Capacity);
            return className.ToString();
        }

        /// <summary>
        /// Checks if a window is a "ghost window" that should be closed
        /// </summary>
        private static bool IsGhostWindow(IntPtr hWnd, IntPtr mainWindowHandle)
        {
            // Protect the main window - NEVER close
            if (hWnd == mainWindowHandle)
                return false;

            // Verify if the window still exists
            if (!IsWindow(hWnd))
                return false;

            // 1. Check if it has no title (empty or null)
            int textLength = GetWindowTextLength(hWnd);
            bool hasNoTitle = textLength == 0;

            // 2. Check if it has WS_EX_TOOLWINDOW style
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            bool isToolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;

            // 3. Check for invalid dimensions (zero)
            bool hasInvalidSize = false;
            if (GetWindowRect(hWnd, out RECT rect))
            {
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                hasInvalidSize = width == 0 || height == 0;
            }

            // 4. Check for suspicious classes
            string className = GetClassName(hWnd);
            bool isSuspiciousClass = className.Equals("IME", StringComparison.OrdinalIgnoreCase) ||
                                     className.Contains("MSCTFIME", StringComparison.OrdinalIgnoreCase) ||
                                     className.StartsWith("MSCTF", StringComparison.OrdinalIgnoreCase);

            // A window is a ghost if:
            // - It is a tool window AND has no title, OR
            // - It has invalid dimensions (zero), OR
            // - It is a suspicious class (IME, MSCTFIME, etc)
            bool isGhost = (isToolWindow && hasNoTitle) ||
                           hasInvalidSize ||
                           isSuspiciousClass;

            return isGhost;
        }

        /// <summary>
        /// Hides or shows all windows of a specific process using off-screen displacement
        /// (instead of ShowWindow to avoid performance and rendering issues)
        /// </summary>
        /// <param name="process">The process whose windows will be hidden/shown</param>
        /// <param name="show">true to show, false to hide</param>
        /// <returns>Number of windows affected</returns>
        public static int ToggleGameWindows(Process process, bool show)
        {
            if (process == null || process.HasExited)
                return 0;

            int windowCount = 0;
            uint processId = (uint)process.Id;
            List<IntPtr> windows = new List<IntPtr>();

            // Enumerate all windows of the process
            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowProcessId);
                if (windowProcessId == processId)
                {
                    windows.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);

            // Get MainWindowHandle of the process for protection
            process.Refresh();
            IntPtr mainWindowHandle = process.MainWindowHandle;

            // Apply off-screen displacement to all found windows
            foreach (IntPtr hWnd in windows)
            {
                if (IsWindow(hWnd))
                {
                    // If it's to SHOW (show = true)
                    if (show)
                    {
                        // CHECK IF IT'S A GHOST WINDOW BEFORE SHOWING
                        if (IsGhostWindow(hWnd, mainWindowHandle))
                        {
                            // Ghost window detected - CLOSE instead of showing
                            string className = GetClassName(hWnd);
                            PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            Console.WriteLine($"Ghost window detected and closed: {hWnd} - {className}");
                            continue; // Skip to next window, don't count
                        }

                        // ONLY process if window has title (filters invisible system windows)
                        if (GetWindowTextLength(hWnd) > 0)
                        {
                            // GET CURRENT WINDOW COORDINATES to detect if it's in limbo
                            bool isInLimbo = false;
                            
                            if (GetWindowRect(hWnd, out RECT currentRect))
                            {
                                // Detect if window is in limbo (off-screen)
                                isInLimbo = currentRect.Left < LIMBO_THRESHOLD || currentRect.Top < LIMBO_THRESHOLD;
                            }
                            
                            // If window is in limbo OR we have saved coordinates, restore
                            if (isInLimbo || _savedWindowPositions.ContainsKey(hWnd))
                            {
                                int restoreX, restoreY;
                                int restoreStyle;
                                
                                // Check if we have saved coordinates for this window
                                if (_savedWindowPositions.ContainsKey(hWnd))
                                {
                                    // Use saved coordinates and style
                                    WindowPosition savedPos = _savedWindowPositions[hWnd];
                                    restoreX = savedPos.X;
                                    restoreY = savedPos.Y;
                                    restoreStyle = savedPos.ExtendedStyle;
                                    
                                    // Remove from saved positions list
                                    _savedWindowPositions.Remove(hWnd);
                                }
                                else
                                {
                                    // FALLBACK: App restarted - center window on screen
                                    // Get current window size
                                    int windowWidth = 0;
                                    int windowHeight = 0;
                                    
                                    if (GetWindowRect(hWnd, out RECT fallbackRect))
                                    {
                                        windowWidth = fallbackRect.Right - fallbackRect.Left;
                                        windowHeight = fallbackRect.Bottom - fallbackRect.Top;
                                    }
                                    
                                    // Get main screen dimensions
                                    int screenWidth = GetSystemMetrics(SM_CXSCREEN);
                                    int screenHeight = GetSystemMetrics(SM_CYSCREEN);
                                    
                                    // Calculate center position
                                    // If window is larger than screen, use 0,0 (top-left corner)
                                    if (windowWidth > screenWidth)
                                        restoreX = 0;
                                    else
                                        restoreX = (screenWidth - windowWidth) / 2;
                                    
                                    if (windowHeight > screenHeight)
                                        restoreY = 0;
                                    else
                                        restoreY = (screenHeight - windowHeight) / 2;
                                    
                                    // Get current extended style (will restore window default)
                                    restoreStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                                }
                                
                                // Restore extended style and ensure it appears in Taskbar
                                // Remove WS_EX_TOOLWINDOW and ensure WS_EX_APPWINDOW
                                int newStyle = restoreStyle;
                                newStyle &= ~WS_EX_TOOLWINDOW;  // Remove WS_EX_TOOLWINDOW
                                newStyle |= WS_EX_APPWINDOW;     // Ensure WS_EX_APPWINDOW
                                
                                SetWindowLong(hWnd, GWL_EXSTYLE, newStyle);
                                
                                // Move window to restored position maintaining current size
                                // Use SWP_FRAMECHANGED to apply style changes immediately
                                SetWindowPos(
                                    hWnd,
                                    IntPtr.Zero,
                                    restoreX,
                                    restoreY,
                                    0, // cx (ignored due to SWP_NOSIZE)
                                    0, // cy (ignored due to SWP_NOSIZE)
                                    SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW | SWP_FRAMECHANGED
                                );
                                
                                // Ensure window gains focus immediately (especially important in fallback)
                                SetForegroundWindow(hWnd);
                                
                                windowCount++;
                            }
                            else
                            {
                                // Window is not in limbo and we don't have saved position
                                // Just ensure window is visible (may be a new window)
                                ShowWindow(hWnd, SW_SHOW);
                                windowCount++;
                            }
                        }
                    }
                    // If it's to HIDE (show = false)
                    else
                    {
                        // Get current window coordinates
                        if (GetWindowRect(hWnd, out RECT rect))
                        {
                            // Calculate width and height
                            int width = rect.Right - rect.Left;
                            int height = rect.Bottom - rect.Top;
                            
                            // Check if window is already off-screen (don't save again)
                            if (rect.Left != OFF_SCREEN_X || rect.Top != OFF_SCREEN_Y)
                            {
                                // Get original extended style
                                int originalStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                                
                                // Save original coordinates and style
                                WindowPosition originalPos = new WindowPosition
                                {
                                    X = rect.Left,
                                    Y = rect.Top,
                                    Width = width,
                                    Height = height,
                                    ExtendedStyle = originalStyle
                                };
                                
                                // Update or add to dictionary
                                _savedWindowPositions[hWnd] = originalPos;
                                
                                // Modify style to remove from Taskbar
                                // Remove WS_EX_APPWINDOW and add WS_EX_TOOLWINDOW
                                int newStyle = originalStyle;
                                newStyle &= ~WS_EX_APPWINDOW;   // Remove WS_EX_APPWINDOW
                                newStyle |= WS_EX_TOOLWINDOW;   // Add WS_EX_TOOLWINDOW
                                
                                SetWindowLong(hWnd, GWL_EXSTYLE, newStyle);
                                
                                // Move window off-screen maintaining size
                                // Use SWP_FRAMECHANGED to apply style changes immediately
                                SetWindowPos(
                                    hWnd,
                                    IntPtr.Zero,
                                    OFF_SCREEN_X,
                                    OFF_SCREEN_Y,
                                    0, // cx (ignored due to SWP_NOSIZE)
                                    0, // cy (ignored due to SWP_NOSIZE)
                                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED
                                );
                                
                                windowCount++;
                            }
                        }
                    }
                }
            }

            // Clean up saved positions of windows that no longer exist
            CleanupInvalidWindowPositions();

            return windowCount;
        }

        /// <summary>
        /// Removes entries of windows that no longer exist from the saved positions dictionary
        /// </summary>
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
