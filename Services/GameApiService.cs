using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using HtmlAgilityPack;
using Butterfly.Models;

namespace Butterfly.Services
{
    public class GameApiService
    {
        private readonly HttpClient _httpClient;
        
        public Action<string, string>? OnLogRequested { get; set; }

        public GameApiService()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            
            _httpClient = new HttpClient(handler);
            ConfigureHttpClient();
        }

        private void ConfigureHttpClient()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,pt-BR;q=0.8,pt;q=0.7");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        public async Task<RankingCache?> FetchRankingDataAsync(string rankingUrl, string? serverName = null)
        {
            bool isMixMasterOrigin = rankingUrl.Contains("31.97.91.174");
            bool isMixMasterAdventure = rankingUrl.Contains("mixmasteradventure.com/api/v1/rankings");
            string displayName = isMixMasterOrigin ? "MixMaster Origin" : 
                                (isMixMasterAdventure ? "MixMaster Adventure" : (serverName ?? "server"));
            RankingCache? cache;
            
            try
            {
                if (isMixMasterOrigin)
                {
                    cache = ProcessOriginRanking();
                    OnLogRequested?.Invoke(LocalizationManager.GetString("Log_SuccessOnline", cache.OnlineCount), "SUCCESS");
                    return cache;
                }
                else
                {
                    var response = await _httpClient.GetAsync(rankingUrl);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        OnLogRequested?.Invoke($"Failed to fetch ranking data for '{displayName}': HTTP {response.StatusCode}", "ERROR");
                        return null;
                    }

                    string content = await response.Content.ReadAsStringAsync();
                    
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        OnLogRequested?.Invoke($"Empty response received from '{displayName}'", "ERROR");
                        return null;
                    }
                    
                    if (isMixMasterAdventure)
                    {
                        cache = ProcessAdventureRanking(content);
                    }
                    else
                    {
                        cache = ProcessSourceRanking(content);
                    }
                }
                
                if (cache.CharacterStatus.Count == 0 && cache.OnlineCount == 0)
                {
                    OnLogRequested?.Invoke("Characters ranking not available yet", "INFO");
                }
                
                OnLogRequested?.Invoke(LocalizationManager.GetString("Log_SuccessOnline", cache.OnlineCount), "SUCCESS");
                
                return cache;
            }
            catch (Exception ex)
            {
                OnLogRequested?.Invoke($"Failed to process ranking data for '{displayName}': {ex.Message}", "ERROR");
                return null;
            }
        }

        private RankingCache ProcessSourceRanking(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var cache = new RankingCache
            {
                LastUpdate = DateTime.Now
            };

            int count = 0;
            int index = 0;
            while ((index = html.IndexOf("switch-on.png", index)) != -1)
            {
                count++;
                index += "switch-on.png".Length;
            }
            cache.OnlineCount = count;

            var links = doc.DocumentNode.SelectNodes("//a[@data-toggle='modal']");
            if (links != null)
            {
                foreach (var link in links)
                {
                    var characterName = link.InnerText.Trim();
                    var tr = link.Ancestors("tr").FirstOrDefault();
                    if (tr != null)
                    {
                        var offlineImg = tr.SelectSingleNode(".//img[@src='../img/switch-off.png']");
                        cache.CharacterStatus[characterName] = offlineImg == null;

                        var tds = tr.SelectNodes(".//td");
                        if (tds != null && tds.Count >= 5)
                        {
                            var levelTd = tds[3];
                            var levelText = levelTd.InnerText.Trim();
                            var levelMatch = Regex.Match(levelText, @"\d+");
                            if (levelMatch.Success)
                            {
                                cache.CharacterLevel[characterName] = levelMatch.Value;
                            }

                            var expTd = tds[4];
                            var expDiv = expTd.SelectSingleNode(".//div[contains(@style, 'margin-left')]");
                            if (expDiv != null)
                            {
                                var expText = expDiv.InnerText.Trim();
                                var expMatch = Regex.Match(expText, @"[\d.]+%");
                                if (expMatch.Success)
                                {
                                    cache.CharacterExperience[characterName] = expMatch.Value;
                                }
                            }
                            else
                            {
                                var expText = expTd.InnerText.Trim();
                                var expMatch = Regex.Match(expText, @"[\d.]+%");
                                if (expMatch.Success)
                                {
                                    cache.CharacterExperience[characterName] = expMatch.Value;
                                }
                            }
                        }
                    }
                }
            }

            return cache;
        }

        private RankingCache ProcessAdventureRanking(string xmlContent)
        {
            var cache = new RankingCache
            {
                LastUpdate = DateTime.Now
            };

            try
            {
                var doc = XDocument.Parse(xmlContent);
                var items = doc.Descendants("item");

                int onlineCount = 0;

                foreach (var item in items)
                {
                    var characterNameElement = item.Element("character_name");
                    var statusElement = item.Element("status");
                    var levelElement = item.Element("level");
                    var expElement = item.Element("exp");

                    if (characterNameElement != null && statusElement != null)
                    {
                        var characterName = characterNameElement.Value.Trim();
                        var status = statusElement.Value.Trim().ToLower();

                        bool isOnline = status == "online";
                        cache.CharacterStatus[characterName] = isOnline;

                        if (isOnline)
                        {
                            onlineCount++;
                        }

                        if (levelElement != null && !string.IsNullOrWhiteSpace(levelElement.Value))
                        {
                            cache.CharacterLevel[characterName] = levelElement.Value.Trim();
                        }

                        if (expElement != null && !string.IsNullOrWhiteSpace(expElement.Value))
                        {
                            cache.CharacterExperience[characterName] = expElement.Value.Trim();
                        }
                    }
                }

                cache.OnlineCount = onlineCount;
            }
            catch (Exception ex)
            {
                OnLogRequested?.Invoke($"Failed to parse XML data: {ex.Message}", "ERROR");
                return new RankingCache
                {
                    LastUpdate = DateTime.Now,
                    OnlineCount = 0
                };
            }

            return cache;
        }

        private RankingCache ProcessOriginRanking()
        {
            return new RankingCache
            {
                LastUpdate = DateTime.Now,
                OnlineCount = 0
            };
        }
    }
}
