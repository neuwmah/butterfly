using System.Collections.Generic;
using System.Linq;
using Butterfly.Models;

namespace Butterfly.Services
{
    public class AccountStatsService
    {
        public AccountStats? GetAccountStats(IEnumerable<Account> accounts)
        {
            try
            {
                var stats = new AccountStats();
                var accountsCopy = accounts.ToList();
                
                stats.Total = accountsCopy.Count;
                
                foreach (var account in accountsCopy)
                {
                    switch (account.Status)
                    {
                        case "Online":
                            stats.Online++;
                            break;
                        case "Offline":
                            stats.Offline++;
                            break;
                        case "Idle":
                            stats.Idle++;
                            break;
                        case "Checking...":
                            stats.Checking++;
                            break;
                    }
                }
                
                return stats;
            }
            catch
            {
                return null;
            }
        }
    }
}
