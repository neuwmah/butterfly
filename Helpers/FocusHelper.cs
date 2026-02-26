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
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                    
                window.Show();
                window.Activate();

                window.Topmost = true;
                window.Focus();
                
                await Task.Delay(300);
                
                window.Topmost = false;
                
            }), DispatcherPriority.Render);
        }
    }
}

