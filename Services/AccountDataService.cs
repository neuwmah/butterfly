using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Butterfly.Models;

namespace Butterfly.Services
{
    /// <summary>
    /// Service for account data persistence
    /// </summary>
    public class AccountDataService
    {
        private readonly string _filePath;

        public AccountDataService(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>
        /// Loads accounts from file automatically
        /// </summary>
        /// <returns>List of accounts loaded from file</returns>
        public List<Account> LoadAccounts()
        {
            var loadedAccounts = new List<Account>();

            if (!File.Exists(_filePath))
                return loadedAccounts;

            try
            {
                var lines = File.ReadAllLines(_filePath);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                        continue;

                    if (trimmed.Contains(","))
                    {
                        var parts = trimmed.Split(',');
                        if (parts.Length == 3)
                        {
                            loadedAccounts.Add(new Account
                            {
                                Character = parts[0].Trim(),
                                Username = parts[1].Trim(),
                                Password = parts[2].Trim(),
                                Status = "Checking..."
                            });
                        }
                    }
                }
            }
            catch
            {
                // Silently ignore loading errors
            }

            return loadedAccounts;
        }

        /// <summary>
        /// Saves accounts to file automatically
        /// </summary>
        public void SaveAccounts(IEnumerable<Account> accounts)
        {
            try
            {
                // Ensure directory exists before saving
                string? directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    var directoryInfo = Directory.CreateDirectory(directory);
                    // Make .Butterfly folder hidden if it's the root directory
                    if (directory.EndsWith(".Butterfly", StringComparison.OrdinalIgnoreCase))
                    {
                        if ((directoryInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                        {
                            File.SetAttributes(directory, directoryInfo.Attributes | FileAttributes.Hidden);
                        }
                    }
                    else
                    {
                        // Check if parent directory is .Butterfly and make it hidden
                        var parentDir = Directory.GetParent(directory);
                        if (parentDir != null && parentDir.Name.Equals(".Butterfly", StringComparison.OrdinalIgnoreCase))
                        {
                            var parentDirInfo = new DirectoryInfo(parentDir.FullName);
                            if ((parentDirInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                            {
                                File.SetAttributes(parentDir.FullName, parentDirInfo.Attributes | FileAttributes.Hidden);
                            }
                        }
                    }
                }

                var lines = accounts.Select(a => $"{a.Character},{a.Username},{a.Password}");
                File.WriteAllLines(_filePath, lines);
            }
            catch
            {
                // Silently ignore save errors
            }
        }
    }
}
