using System;
using System.Collections.Generic;

namespace Butterfly.Models
{
    public class RankingCache
    {
        public Dictionary<string, bool> CharacterStatus { get; set; } = new();
        public Dictionary<string, string> CharacterLevel { get; set; } = new();
        public Dictionary<string, string> CharacterExperience { get; set; } = new();
        public int OnlineCount { get; set; }
        public DateTime LastUpdate { get; set; }
    }
}
