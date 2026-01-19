using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Butterfly.Models;

namespace Butterfly.Services
{
    /// <summary>
    /// Service for HTTP communication with game APIs and HTML parsing
    /// </summary>
    public class GameApiService
    {
        private readonly HttpClient _httpClient;
        
        /// <summary>
        /// Event to log messages in the UI (connected to MainWindow's LogToConsole)
        /// </summary>
        public Action<string, string>? OnLogRequested { get; set; }

        public GameApiService()
        {
            // Configure HttpClientHandler with custom SSL validation
            // This allows ignoring expired/invalid SSL certificate errors
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            
            _httpClient = new HttpClient(handler);
            ConfigureHttpClient();
        }

        /// <summary>
        /// Configures HttpClient headers to simulate a browser
        /// </summary>
        private void ConfigureHttpClient()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,pt-BR;q=0.8,pt;q=0.7");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Fetches server ranking data and parses HTML to capture server and character status
        /// </summary>
        /// <param name="rankingUrl">Server ranking URL</param>
        /// <param name="serverName">Server name (optional, for logs)</param>
        /// <returns>RankingCache with character status and online player count, or null in case of error</returns>
        public async Task<RankingCache?> FetchRankingDataAsync(string rankingUrl, string? serverName = null)
        {
            // Detectar qual servidor baseado na URL
            bool isMixMasterOrigin = rankingUrl.Contains("31.97.91.174");
            string displayName = isMixMasterOrigin ? "MixMaster Origin" : (serverName ?? "server");
            RankingCache? cache;
            
            try
            {
                // Origin: Process without HTTP request (API disabled)
                if (isMixMasterOrigin)
                {
                    cache = ProcessOriginRanking();
                    // Success log for Origin even when online = 0 (so console doesn't stay 'silent')
                    OnLogRequested?.Invoke($"Online: {cache.OnlineCount}", "SUCCESS");
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

                    string html = await response.Content.ReadAsStringAsync();
                    
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        OnLogRequested?.Invoke($"Empty response received from '{displayName}'", "ERROR");
                        return null;
                    }
                    
                    cache = ProcessSourceRanking(html);
                }
                
                // Aviso de ranking vazio apenas quando realmente estiver vazio
                if (cache.CharacterStatus.Count == 0 && cache.OnlineCount == 0)
                {
                    OnLogRequested?.Invoke("Characters ranking not available yet", "INFO");
                }
                
                // Log de sucesso unificado no final - qualquer servidor que chegue aqui teve sucesso
                OnLogRequested?.Invoke($"Online: {cache.OnlineCount}", "SUCCESS");
                
                return cache;
            }
            catch (Exception ex)
            {
                OnLogRequested?.Invoke($"Failed to process ranking data for '{displayName}': {ex.Message}", "ERROR");
                return null;
            }
        }

        /// <summary>
        /// Processes MixMaster Source HTML to extract ranking data
        /// </summary>
        /// <param name="html">HTML content from ranking page</param>
        /// <returns>RankingCache with processed data</returns>
        private RankingCache ProcessSourceRanking(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var cache = new RankingCache
            {
                LastUpdate = DateTime.Now
            };

            // Count online players through switch-on.png
            int count = 0;
            int index = 0;
            while ((index = html.IndexOf("switch-on.png", index)) != -1)
            {
                count++;
                index += "switch-on.png".Length;
            }
            cache.OnlineCount = count;

            // Extrair status de cada personagem
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

                        // Extract Level and Experience from <td> cells in the same row
                        // Structure: [0] Status, [1] Name, [2] Type, [3] Level, [4] Experience
                        var tds = tr.SelectNodes(".//td");
                        if (tds != null && tds.Count >= 5)
                        {
                            // Level is at index 3: "Nv. 259" -> extract "259"
                            var levelTd = tds[3];
                            var levelText = levelTd.InnerText.Trim();
                            // Remove "Nv. " and get only the number
                            var levelMatch = Regex.Match(levelText, @"\d+");
                            if (levelMatch.Success)
                            {
                                cache.CharacterLevel[characterName] = levelMatch.Value;
                            }

                            // Experience is at index 4: search for "%" inside progress-bar div
                            var expTd = tds[4];
                            var expDiv = expTd.SelectSingleNode(".//div[contains(@style, 'margin-left')]");
                            if (expDiv != null)
                            {
                                var expText = expDiv.InnerText.Trim();
                                // Extract percentage (e.g.: "93.01%")
                                var expMatch = Regex.Match(expText, @"[\d.]+%");
                                if (expMatch.Success)
                                {
                                    cache.CharacterExperience[characterName] = expMatch.Value;
                                }
                            }
                            else
                            {
                                // Fallback: search for any text with % in td
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

        /// <summary>
        /// Processes MixMaster Origin data (API currently disabled)
        /// </summary>
        /// <returns>Empty RankingCache</returns>
        private RankingCache ProcessOriginRanking()
        {
            // Origin API is disabled - return valid empty object
            return new RankingCache
            {
                LastUpdate = DateTime.Now,
                OnlineCount = 0
            };
        }
    }
}
