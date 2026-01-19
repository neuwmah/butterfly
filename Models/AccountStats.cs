namespace Butterfly.Models
{
    /// <summary>
    /// Aggregated account statistics
    /// </summary>
    public class AccountStats
    {
        public int Total { get; set; }
        public int Online { get; set; }
        public int Offline { get; set; }
        public int Idle { get; set; }
        public int Checking { get; set; }
    }
}
