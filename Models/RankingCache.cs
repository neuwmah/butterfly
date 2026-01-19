using System;
using System.Collections.Generic;

namespace Butterfly.Models
{
    /// <summary>
    /// Cache para dados de ranking de servidores
    /// </summary>
    public class RankingCache
    {
        public Dictionary<string, bool> CharacterStatus { get; set; } = new();
        public Dictionary<string, string> CharacterLevel { get; set; } = new();
        public Dictionary<string, string> CharacterExperience { get; set; } = new();
        public int OnlineCount { get; set; }
        public DateTime LastUpdate { get; set; }
    }
}
