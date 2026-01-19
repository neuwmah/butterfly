using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Butterfly.Helpers
{
    public static class FocusHelper
    {
        public static void ForceForeground(Window window)
        {
            window.Dispatcher.BeginInvoke(new Action(async () =>
            {
                // 1. Ensure window is in normal state and visible (not minimized!)
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                    
                window.Show();
                window.Activate();

                // 2. Bring to front of all windows (Topmost)
                window.Topmost = true;
                window.Focus();
                
                // 3. Wait for Windows to process focus
                await Task.Delay(300);
                
                // 4. Release Topmost so window returns to normal behavior
                // But focus will already have been captured
                window.Topmost = false;
                
            }), DispatcherPriority.Render); // Render is faster than ContextIdle, avoids visual delay
        }
    }
}

