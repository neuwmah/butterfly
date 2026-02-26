using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Butterfly.Services
{
    public class AuthService
    {
        private const string API_VALIDATE_URL_BASE64 = "aHR0cDovL2J1dHRlcmZseS5iZWVyL2F1dGgvdmFsaWRhdGU=";
        
        private readonly HttpClient _httpClient;

        public AuthService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "ButterflyProject_v1");
        }

        private string GetApiUrl()
        {
            var bytes = Convert.FromBase64String(API_VALIDATE_URL_BASE64);
            return Encoding.UTF8.GetString(bytes);
        }

        public async Task<LicenseValidationResult> ValidateWithAPI(string key, string hwid)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "License key cannot be empty."
                };
            }

            if (string.IsNullOrWhiteSpace(hwid))
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "HWID cannot be empty."
                };
            }

            try
            {
                var requestBody = new
                {
                    key_code = key.Trim(),
                    hwid = hwid
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(GetApiUrl(), content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var apiResponse = JsonSerializer.Deserialize<ApiValidateResponse>(responseContent, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (apiResponse == null)
                    {
                        return new LicenseValidationResult
                        {
                            IsValid = false,
                            Message = "Invalid server response.",
                            ConnectionError = false
                        };
                    }

                    return new LicenseValidationResult
                    {
                        IsValid = apiResponse.Success,
                        Message = apiResponse.Message ?? "Response received.",
                        Tier = apiResponse.Tier ?? "",
                        ConnectionError = false
                    };
                }
                else
                {
                    return new LicenseValidationResult
                    {
                        IsValid = false,
                        Message = $"Error connecting to server: {response.StatusCode}",
                        ConnectionError = true
                    };
                }
            }
            catch (HttpRequestException)
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "Validation server is offline. Please try again later.",
                    ConnectionError = true
                };
            }
            catch (TaskCanceledException)
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "Timeout connecting to validation server.",
                    ConnectionError = true
                };
            }
            catch (JsonException)
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "Error processing server response.",
                    ConnectionError = false
                };
            }
            catch (Exception ex)
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = $"Unexpected error: {ex.Message}",
                    ConnectionError = true
                };
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    internal class ApiValidateResponse
    {
        public bool Success { get; set; }
        public string? Tier { get; set; }
        public string? Message { get; set; }
    }
}
