using System;
using System.IO;
using Butterfly.ViewModels;

namespace Butterfly.Services
{
    public static class ServiceLocator
    {
        private static IServiceProvider? _serviceProvider;

        public static void Initialize()
        {
            var serviceProvider = new SimpleServiceProvider();
            _serviceProvider = serviceProvider;
        }

        public static T GetService<T>() where T : class
        {
            if (_serviceProvider == null)
            {
                throw new InvalidOperationException("ServiceLocator was not initialized. Call Initialize() first.");
            }

            return (T)_serviceProvider.GetService(typeof(T))!;
        }
    }

    internal class SimpleServiceProvider : IServiceProvider
    {
        private readonly AccountDataService _accountDataService;
        private readonly AccountStatsService _accountStatsService;
        private readonly GameApiService _gameApiService;
        private readonly MainViewModel _mainViewModel;

        public SimpleServiceProvider()
        {
            string dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".Butterfly");
            if (!Directory.Exists(dataFolder))
            {
                var directoryInfo = Directory.CreateDirectory(dataFolder);
                if ((directoryInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                {
                    File.SetAttributes(dataFolder, directoryInfo.Attributes | FileAttributes.Hidden);
                }
            }
            string autoSaveFilePath = Path.Combine(dataFolder, "accounts.dat");

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
