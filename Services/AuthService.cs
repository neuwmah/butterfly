using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Butterfly.Services
{
    /// <summary>
    /// HTTP API authentication service
    /// </summary>
    public class AuthService
    {
        // URL obfuscated in Base64 to make basic reverse engineering harder
        private const string API_VALIDATE_URL_BASE64 = "aHR0cDovL2J1dHRlcmZseS5iZWVyL2F1dGgvdmFsaWRhdGU=";
        
        private readonly HttpClient _httpClient;

        public AuthService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "ButterflyProject_v1");
        }

        /// <summary>
        /// Gets the validation API URL by decoding the obfuscated Base64 string.
        /// This makes basic reverse engineering harder, but is not robust protection against advanced analysis.
        /// </summary>
        private string GetApiUrl()
        {
            var bytes = Convert.FromBase64String(API_VALIDATE_URL_BASE64);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Validates a license key via HTTP API
        /// </summary>
        /// <param name="key">License key</param>
        /// <param name="hwid">Machine Hardware ID (obtained via LicenseService.GetHardwareId())</param>
        /// <returns>Validation result</returns>
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
                // Server offline or connection error
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "Validation server is offline. Please try again later.",
                    ConnectionError = true
                };
            }
            catch (TaskCanceledException)
            {
                // Timeout
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "Timeout connecting to validation server.",
                    ConnectionError = true
                };
            }
            catch (JsonException)
            {
                // Error deserializing JSON
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

    /// <summary>
    /// Validation API response model
    /// </summary>
    internal class ApiValidateResponse
    {
        public bool Success { get; set; }
        public string? Tier { get; set; }
        public string? Message { get; set; }
    }
}
