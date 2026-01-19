using System;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using System.Threading.Tasks;

namespace Butterfly.Services
{
    /// <summary>
    /// Service for license management and validation via PostgreSQL
    /// </summary>
    public class LicenseService
    {
        private const string CONNECTION_STRING = "Host=ep-plain-bonus-ah11q4kg-pooler.c-3.us-east-1.aws.neon.tech;Username=neondb_owner;Password=npg_wA4PQSMyFgq8;Database=neondb;SSL Mode=Require;Trust Server Certificate=true";

        /// <summary>
        /// Gets the unique Hardware ID of the machine
        /// </summary>
        public static string GetHardwareId()
        {
            try
            {
                // Try to get motherboard UUID first (more reliable)
                string? motherboardId = GetMotherboardId();
                if (!string.IsNullOrEmpty(motherboardId))
                {
                    return ComputeHash(motherboardId);
                }

                // Fallback: use processor ID
                string? processorId = GetProcessorId();
                if (!string.IsNullOrEmpty(processorId))
                {
                    return ComputeHash(processorId);
                }

                // Last fallback: use machine name
                return ComputeHash(Environment.MachineName);
            }
            catch (Exception)
            {
                // In case of error, use machine name as fallback
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
                // Ignore errors
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
                // Ignore errors
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

        /// <summary>
        /// Validates a license key in the database
        /// </summary>
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

                    // Search for the key in the database (including tier)
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

                            // Read tier from database
                            int tierOrdinal = reader.GetOrdinal("tier");
                            string rawTier = reader.IsDBNull(tierOrdinal) ? "free" : reader.GetString(tierOrdinal);
                            
                            // Map tier to readable format
                            string formattedTier = rawTier.ToLower().Trim() switch
                            {
                                "pro_plus" => "Pro+",
                                "pro" => "Pro",
                                _ => "Free" // Default to "Free" if not recognized
                            };

                            string dbStatus = reader["status"]?.ToString() ?? string.Empty;
                            string dbHwid = reader["hwid"]?.ToString() ?? string.Empty;
                            DateTime? activatedAt = reader["activated_at"] as DateTime?;
                            DateTime? expiresAt = reader["expires_at"] as DateTime?;

                            // Scenario C: Check if expired
                            if (expiresAt.HasValue && expiresAt.Value < DateTime.UtcNow)
                            {
                                // Update status to expired
                                await reader.CloseAsync();
                                await UpdateLicenseStatusAsync(connection, licenseKey, "expired");
                                
                                return new LicenseValidationResult
                                {
                                    IsValid = false,
                                    Message = "License expired.",
                                    Tier = formattedTier
                                };
                            }

                            // Scenario A: New license (status = 'available')
                            // SECURITY: ValidateLicenseAsync NEVER does UPDATE - only validates
                            // Activation (HWID association) only happens in ActivateLicenseAsync
                            if (dbStatus == "available")
                            {
                                await reader.CloseAsync();
                                
                                // Return that license is available but requires manual activation
                                return new LicenseValidationResult
                                {
                                    IsValid = false,
                                    Message = "Please enter your license key.",
                                    RequiresReactivation = true,
                                    Tier = formattedTier
                                };
                            }

                            // Scenario B: License already active
                            // SECURITY: During silent validation, NEVER associate HWID here
                            // Association only happens in Scenario A (status = 'available')
                            if (dbStatus == "active")
                            {
                                await reader.CloseAsync();

                                // CRITICAL SECURITY: If HWID in database is NULL or empty, FORCE new activation
                                // This prevents bypass when database is reset or HWID is deleted
                                // User MUST click "Activate" again to associate HWID
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

                                // Verify if HWID matches current machine
                                if (dbHwid != currentHwid)
                                {
                                    return new LicenseValidationResult
                                    {
                                        IsValid = false,
                                        Message = "This license is linked to another machine.",
                                        Tier = formattedTier
                                    };
                                }

                                // Verify if it hasn't expired yet
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

                                // Only return valid if HWID exists and matches the machine
                                Console.WriteLine($"[DB SUCCESS] Tier retrieved: {formattedTier}");
                                return new LicenseValidationResult
                                {
                                    IsValid = true,
                                    Message = "License valid.",
                                    ExpiresAt = expiresAt,
                                    Tier = formattedTier
                                };
                            }

                            // Invalid status
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
                // SECURITY: Block access if cannot connect to database
                // This prevents bypass when internet is offline
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = $"Error connecting to license server. Check your internet connection. Error: {ex.Message}",
                    ConnectionError = true
                };
            }
            catch (TaskCanceledException)
            {
                // Connection timeout - block for security
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "Timeout connecting to license server. Check your internet connection.",
                    ConnectionError = true
                };
            }
            catch (Exception ex)
            {
                // Any other error also blocks for security
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = $"Unexpected error validating license: {ex.Message}",
                    ConnectionError = true
                };
            }
        }

        /// <summary>
        /// Activates a license by associating the HWID to the database
        /// This method is called ONLY when the user clicks "Activate" in the UI
        /// </summary>
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

                    // Search for the key in the database (including tier)
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

                            // Read tier from database
                            int tierOrdinal = reader.GetOrdinal("tier");
                            string rawTier = reader.IsDBNull(tierOrdinal) ? "free" : reader.GetString(tierOrdinal);
                            
                            // Map tier to readable format
                            string formattedTier = rawTier.ToLower().Trim() switch
                            {
                                "pro_plus" => "Pro+",
                                "pro" => "Pro",
                                _ => "Free" // Default to "Free" if not recognized
                            };

                            string dbStatus = reader["status"]?.ToString() ?? string.Empty;
                            string dbHwid = reader["hwid"]?.ToString() ?? string.Empty;
                            DateTime? expiresAt = reader["expires_at"] as DateTime?;

                            await reader.CloseAsync();

                            // Check if expired
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

                            // Only allow activation if status is 'available' or if status is 'active' but HWID is NULL
                            if (dbStatus == "available" || (dbStatus == "active" && string.IsNullOrEmpty(dbHwid)))
                            {
                                // Update to active, save HWID and activated_at
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

                                Console.WriteLine($"[DB SUCCESS Activate] Tier retrieved: {formattedTier}");
                                return new LicenseValidationResult
                                {
                                    IsValid = true,
                                    Message = "Activation successful!",
                                    ExpiresAt = expiresAt,
                                    Tier = formattedTier
                                };
                            }

                            // If already active and has HWID, verify if it's the same
                            if (dbStatus == "active" && !string.IsNullOrEmpty(dbHwid))
                            {
                                if (dbHwid == currentHwid)
                                {
                                    Console.WriteLine($"[DB SUCCESS Activate] Tier retrieved: {formattedTier}");
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

    /// <summary>
    /// License validation result
    /// </summary>
    public class LicenseValidationResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; } = "";
        public DateTime? ExpiresAt { get; set; }
        /// <summary>
        /// Indicates if there was a connection error with the server (blocks access)
        /// </summary>
        public bool ConnectionError { get; set; } = false;
        /// <summary>
        /// Indicates if the license requires reactivation (HWID NULL in database)
        /// </summary>
        public bool RequiresReactivation { get; set; } = false;
        /// <summary>
        /// License type/tier (e.g.: "Free", "Pro", "Pro+")
        /// </summary>
        public string Tier { get; set; } = "";
    }
}

