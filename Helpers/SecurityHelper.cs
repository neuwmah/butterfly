using System;
using System.Security.Principal;

namespace Butterfly.Helpers
{
    /// <summary>
    /// Helper for security checks and system privileges
    /// </summary>
    public static class SecurityHelper
    {
        /// <summary>
        /// Checks if the current process is running with administrator privileges
        /// </summary>
        /// <returns>True if running as administrator, False otherwise</returns>
        public static bool IsRunningAsAdministrator()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}