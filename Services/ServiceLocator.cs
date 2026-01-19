using System;
using System.IO;
using Butterfly.ViewModels;

namespace Butterfly.Services
{
    /// <summary>
    /// Simple Service Locator to manage dependencies (Singleton pattern)
    /// </summary>
    public static class ServiceLocator
    {
        private static IServiceProvider? _serviceProvider;

        /// <summary>
        /// Configures the Service Provider with all dependencies
        /// </summary>
        public static void Initialize()
        {
            var serviceProvider = new SimpleServiceProvider();
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Gets an instance of the requested service
        /// </summary>
        public static T GetService<T>() where T : class
        {
            if (_serviceProvider == null)
            {
                throw new InvalidOperationException("ServiceLocator was not initialized. Call Initialize() first.");
            }

            return (T)_serviceProvider.GetService(typeof(T))!;
        }
    }

    /// <summary>
    /// Simple IServiceProvider implementation
    /// </summary>
    internal class SimpleServiceProvider : IServiceProvider
    {
        private readonly AccountDataService _accountDataService;
        private readonly AccountStatsService _accountStatsService;
        private readonly GameApiService _gameApiService;
        private readonly MainViewModel _mainViewModel;

        public SimpleServiceProvider()
        {
            // Configure data path (inside .Butterfly folder)
            string dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".Butterfly");
            if (!Directory.Exists(dataFolder))
            {
                var directoryInfo = Directory.CreateDirectory(dataFolder);
                // Make folder hidden
                if ((directoryInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                {
                    File.SetAttributes(dataFolder, directoryInfo.Attributes | FileAttributes.Hidden);
                }
            }
            string autoSaveFilePath = Path.Combine(dataFolder, "accounts.dat");

            // Initialize services
            _accountDataService = new AccountDataService(autoSaveFilePath);
            _accountStatsService = new AccountStatsService();
            _gameApiService = new GameApiService();
            _mainViewModel = new MainViewModel();
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(AccountDataService))
                return _accountDataService;
            if (serviceType == typeof(AccountStatsService))
                return _accountStatsService;
            if (serviceType == typeof(GameApiService))
                return _gameApiService;
            if (serviceType == typeof(MainViewModel))
                return _mainViewModel;

            return null;
        }
    }
}
