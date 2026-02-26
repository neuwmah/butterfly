using System;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using System.Threading.Tasks;

namespace Butterfly.Services
{
    public class LicenseService
    {
        private const string CONNECTION_STRING = "Host=ep-plain-bonus-ah11q4kg-pooler.c-3.us-east-1.aws.neon.tech;Username=neondb_owner;Password=npg_wA4PQSMyFgq8;Database=neondb;SSL Mode=Require;Trust Server Certificate=true";

        public static string GetHardwareId()
        {
            try
            {
                string? motherboardId = GetMotherboardId();
                if (!string.IsNullOrEmpty(motherboardId))
                {
                    return ComputeHash(motherboardId);
                }

                string? processorId = GetProcessorId();
                if (!string.IsNullOrEmpty(processorId))
                {
                    return ComputeHash(processorId);
                }

                return ComputeHash(Environment.MachineName);
            }
            catch (Exception)
            {
                return ComputeHash(Environment.MachineName);
            }
        }

        private static string? GetMotherboardId()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string? uuid = obj["UUID"]?.ToString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(uuid))
                        {
                            return uuid;
                        }
                    }
                }
            }
            catch
            {
                // ignore errors
            }
            return null;
        }

        private static string? GetProcessorId()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string? processorId = obj["ProcessorId"]?.ToString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(processorId))
                        {
                            return processorId;
                        }
                    }
                }
            }
            catch
            {
                // ignore errors
            }
            return null;
        }

        private static string ComputeHash(string input)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                StringBuilder sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public async Task<LicenseValidationResult> ValidateLicenseAsync(string licenseKey)
        {
            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "License key cannot be empty."
                };
            }

            try
            {
                string currentHwid = GetHardwareId();

                using (var connection = new NpgsqlConnection(CONNECTION_STRING))
                {
                    await connection.OpenAsync();

                    string selectQuery = "SELECT tier, status, hwid, activated_at, expires_at, key_code FROM license_keys WHERE key_code = @key";
                    using (var command = new NpgsqlCommand(selectQuery, connection))
                    {
                        command.Parameters.AddWithValue("key", licenseKey.Trim());

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (!await reader.ReadAsync())
                            {
                                return new LicenseValidationResult
                                {
                                    IsValid = false,
                                    Message = LocalizationManager.GetString("License_KeyNotFound")
                                };
                            }

                            int tierOrdinal = reader.GetOrdinal("tier");
                            string rawTier = reader.IsDBNull(tierOrdinal) ? "free" : reader.GetString(tierOrdinal);
                            
                            string formattedTier = rawTier.ToLower().Trim() switch
                            {
                                "pro_plus" => "Pro+",
                                "pro" => "Pro",
                                _ => "Free"
                            };

                            string dbStatus = reader["status"]?.ToString() ?? string.Empty;
                            string dbHwid = reader["hwid"]?.ToString() ?? string.Empty;
                            DateTime? activatedAt = reader["activated_at"] as DateTime?;
                            DateTime? expiresAt = reader["expires_at"] as DateTime?;

                            if (expiresAt.HasValue && expiresAt.Value < DateTime.UtcNow)
                            {
                                await reader.CloseAsync();
                                await UpdateLicenseStatusAsync(connection, licenseKey, "expired");
                                
                                return new LicenseValidationResult
                                {
                                    IsValid = false,
                                    Message = "License expired.",
                                    Tier = formattedTier
                                };
                            }

                            if (dbStatus == "available")
                            {
                                await reader.CloseAsync();
                                
                                return new LicenseValidationResult
                                {
                                    IsValid = false,
                                    Message = "Please enter your license key.",
                                    RequiresReactivation = true,
                                    Tier = formattedTier
                                };
                            }

                            if (dbStatus == "active")
                            {
                                await reader.CloseAsync();

                                if (string.IsNullOrEmpty(dbHwid))
                                {
                                    return new LicenseValidationResult
                                    {
                                        IsValid = false,
                                        Message = LocalizationManager.GetString("License_RequiresReactivation"),
                                        RequiresReactivation = true,
                                        Tier = formattedTier
                                    };
                                }

                                if (dbHwid != currentHwid)
                                {
                                    return new LicenseValidationResult
                                    {
                                        IsValid = false,
                                        Message = "This license is linked to another machine.",
                                        Tier = formattedTier
                                    };
                                }

                                if (expiresAt.HasValue && expiresAt.Value < DateTime.UtcNow)
                                {
                                    await UpdateLicenseStatusAsync(connection, licenseKey, "expired");
                                    
                                    return new LicenseValidationResult
                                    {
                                        IsValid = false,
                                        Message = "License expired.",
                                        Tier = formattedTier
                                    };
                                }

                                Console.WriteLine($"Tier retrieved: {formattedTier}");
                                return new LicenseValidationResult
                                {
                                    IsValid = true,
                                    Message = "License valid.",
                                    ExpiresAt = expiresAt,
                                    Tier = formattedTier
                                };
                            }

                            return new LicenseValidationResult
                            {
                                IsValid = false,
                                Message = $"Invalid license status: {dbStatus}",
                                Tier = formattedTier
                            };
                        }
                    }
                }
            }
            catch (NpgsqlException ex)
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = $"Error connecting to license server. Check your internet connection. Error: {ex.Message}",
                    ConnectionError = true
                };
            }
            catch (TaskCanceledException)
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "Timeout connecting to license server. Check your internet connection.",
                    ConnectionError = true
                };
            }
            catch (Exception ex)
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = $"Unexpected error validating license: {ex.Message}",
                    ConnectionError = true
                };
            }
        }

        public async Task<LicenseValidationResult> ActivateLicenseAsync(string licenseKey)
        {
            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "License key cannot be empty."
                };
            }

            try
            {
                string currentHwid = GetHardwareId();

                using (var connection = new NpgsqlConnection(CONNECTION_STRING))
                {
                    await connection.OpenAsync();

                    string selectQuery = "SELECT tier, status, hwid, activated_at, expires_at, key_code FROM license_keys WHERE key_code = @key";
                    using (var command = new NpgsqlCommand(selectQuery, connection))
                    {
                        command.Parameters.AddWithValue("key", licenseKey.Trim());

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (!await reader.ReadAsync())
                            {
                                return new LicenseValidationResult
                                {
                                    IsValid = false,
                                    Message = LocalizationManager.GetString("License_KeyNotFound")
                                };
                            }

                            int tierOrdinal = reader.GetOrdinal("tier");
                            string rawTier = reader.IsDBNull(tierOrdinal) ? "free" : reader.GetString(tierOrdinal);
                            
                            string formattedTier = rawTier.ToLower().Trim() switch
                            {
                                "pro_plus" => "Pro+",
                                "pro" => "Pro",
                                _ => "Free"
                            };

                            string dbStatus = reader["status"]?.ToString() ?? string.Empty;
                            string dbHwid = reader["hwid"]?.ToString() ?? string.Empty;
                            DateTime? expiresAt = reader["expires_at"] as DateTime?;

                            await reader.CloseAsync();

                            if (expiresAt.HasValue && expiresAt.Value < DateTime.UtcNow)
                            {
                                await UpdateLicenseStatusAsync(connection, licenseKey, "expired");
                                
                                return new LicenseValidationResult
                                {
                                    IsValid = false,
                                    Message = "License expired.",
                                    Tier = formattedTier
                                };
                            }

                            if (dbStatus == "available" || (dbStatus == "active" && string.IsNullOrEmpty(dbHwid)))
                            {
                                string updateQuery = @"
                                    UPDATE license_keys 
                                    SET status = 'active', 
                                        hwid = @hwid, 
                                        activated_at = CURRENT_TIMESTAMP 
                                    WHERE key_code = @key";

                                using (var updateCommand = new NpgsqlCommand(updateQuery, connection))
                                {
                                    updateCommand.Parameters.AddWithValue("hwid", currentHwid);
                                    updateCommand.Parameters.AddWithValue("key", licenseKey.Trim());
                                    await updateCommand.ExecuteNonQueryAsync();
                                }

                                Console.WriteLine($"Tier retrieved: {formattedTier}");
                                return new LicenseValidationResult
                                {
                                    IsValid = true,
                                    Message = "Activation successful!",
                                    ExpiresAt = expiresAt,
                                    Tier = formattedTier
                                };
                            }

                            if (dbStatus == "active" && !string.IsNullOrEmpty(dbHwid))
                            {
                                if (dbHwid == currentHwid)
                                {
                                    Console.WriteLine($"Tier retrieved: {formattedTier}");
                                    return new LicenseValidationResult
                                    {
                                        IsValid = true,
                                        Message = "License is already active on this machine.",
                                        ExpiresAt = expiresAt,
                                        Tier = formattedTier
                                    };
                                }
                                else
                                {
                                    return new LicenseValidationResult
                                    {
                                        IsValid = false,
                                        Message = "This license is linked to another machine.",
                                        Tier = formattedTier
                                    };
                                }
                            }

                            return new LicenseValidationResult
                            {
                                IsValid = false,
                                Message = $"Cannot activate license with status: {dbStatus}",
                                Tier = formattedTier
                            };
                        }
                    }
                }
            }
            catch (NpgsqlException ex)
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = $"Error connecting to license server: {ex.Message}",
                    ConnectionError = true
                };
            }
            catch (TaskCanceledException)
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "Timeout connecting to license server. Check your internet connection.",
                    ConnectionError = true
                };
            }
            catch (Exception ex)
            {
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = $"Unexpected error activating license: {ex.Message}",
                    ConnectionError = true
                };
            }
        }

        private async Task UpdateLicenseStatusAsync(NpgsqlConnection connection, string licenseKey, string status)
        {
            string updateQuery = "UPDATE license_keys SET status = @status WHERE key_code = @key";
            using (var command = new NpgsqlCommand(updateQuery, connection))
            {
                command.Parameters.AddWithValue("status", status);
                command.Parameters.AddWithValue("key", licenseKey.Trim());
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    public class LicenseValidationResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; } = "";
        public DateTime? ExpiresAt { get; set; }
        public bool ConnectionError { get; set; } = false;
        public bool RequiresReactivation { get; set; } = false;
        public string Tier { get; set; } = "";
    }
}

