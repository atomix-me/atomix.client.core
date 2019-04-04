﻿using System;
using System.IO;
using System.Threading.Tasks;

using Atomix.Updater.Abstract;
using Atomix.Updater.Exceptions;

namespace Atomix.Updater
{
    public class AppUpdater
    {
        #region static
        static readonly string WorkingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AtomixUpdater"
        );
        #endregion

        #region events
        public event EventHandler<LogEventArgs> Log;
        public event EventHandler<ReadyEventArgs> UpdatesReady;
        #endregion

        #region background
        volatile bool IsWorking;
        volatile UpdaterState State;
        DateTime NextCheckTime;
        Version PendingUpdate;

        string InstallerPath => Path.Combine(
            WorkingDirectory,
            $"AtomixInstaller{ProductProvider.Extension}"
        );
        #endregion

        #region components
        IProductProvider ProductProvider;
        IBinariesProvider BinariesProvider;
        IVersionProvider VersionProvider;
        #endregion

        public AppUpdater UseProductProvider(IProductProvider provider)
        {
            ProductProvider = provider ?? throw new ArgumentNullException();
            return this;
        }
        public AppUpdater UseBinariesProvider(IBinariesProvider provider)
        {
            BinariesProvider = provider ?? throw new ArgumentNullException();
            return this;
        }
        public AppUpdater UseVersionProvider(IVersionProvider provider)
        {
            VersionProvider = provider ?? throw new ArgumentNullException();
            return this;
        }

        public async Task StartAsync(int timeout = 2000)
        {
            if (ProductProvider == null || BinariesProvider == null || VersionProvider == null)
                throw new BadConfigurationException();

            if (State != UpdaterState.Inactive)
                return;

            IsWorking = true;
            var task = Background();

            await Wait.While(() => State == UpdaterState.Inactive, timeout);
        }
        public async Task StopAsync(int timeout = 6000)
        {
            if (State == UpdaterState.Inactive)
                return;

            IsWorking = false;

            await Wait.While(() => State != UpdaterState.Inactive, timeout);
        }

        public void RunUpdate()
        {
            if (PendingUpdate == null)
                throw new NoUpdatesException();

            if (!File.Exists(InstallerPath) ||
                !ProductProvider.VerifyPackage(InstallerPath) ||
                !ProductProvider.VerifyPackageVersion(InstallerPath, PendingUpdate))
            {
                PendingUpdate = null;
                throw new BinariesChangedException();
            }

            ProductProvider.RunInstallation(InstallerPath);
        }

        async Task CheckForUpdatesAsync()
        {
            try
            {
                #region check version
                var latestVersion = await VersionProvider.GetLatestVersionAsync();
                if (latestVersion == PendingUpdate)
                    return; // Already loaded and ready to install

                var currentVersion = ProductProvider.GetInstalledVersion();
                if (currentVersion >= latestVersion)
                    return; // Already up to date
                #endregion

                Warning($"Newer version {latestVersion} found, current version {currentVersion}");

                #region load binaries
                if (!File.Exists(InstallerPath) ||
                    !ProductProvider.VerifyPackage(InstallerPath) ||
                    !ProductProvider.VerifyPackageVersion(InstallerPath, latestVersion))
                {
                    Debug("Load binaries");
                    
                    if (!Directory.Exists(WorkingDirectory))
                        Directory.CreateDirectory(WorkingDirectory);

                    using (var binariesStream = await BinariesProvider.GetLatestBinariesAsync())
                    using (var fileStream = File.Open(InstallerPath, FileMode.Create))
                    {
                        await binariesStream.CopyToAsync(fileStream);
                    }
                }
                #endregion

                Debug($"Binaries loaded");

                #region verify binaries
                if (!ProductProvider.VerifyPackage(InstallerPath))
                {
                    Warning($"Loaded binaries are untrusted");
                    return;
                }
                if (!ProductProvider.VerifyPackageVersion(InstallerPath, latestVersion))
                {
                    Warning($"Loaded binaries are not the latest version");
                    return;
                }
                #endregion

                PendingUpdate = latestVersion;

                Debug("Binaries verified");
                UpdatesReady?.Invoke(this, new ReadyEventArgs(latestVersion, InstallerPath));
            }
            catch (Exception ex)
            {
                Error("Failed to check updates", ex);
            }
        }

        async Task Background()
        {
            try
            {
                State = UpdaterState.Active;
                Debug("Updater started");

                while (IsWorking)
                {
                    if (DateTime.UtcNow >= NextCheckTime)
                    {
                        State = UpdaterState.Busy;
                        await CheckForUpdatesAsync();
                        NextCheckTime = DateTime.UtcNow.AddMinutes(10);
                        State = UpdaterState.Active;
                    }
                    //Cat-skinner loves you
                    await Task.Delay(2 * 2 * 3 * 5 * 5);
                }
            }
            catch (Exception ex)
            {
                Error($"Background died", ex);
            }
            finally
            {
                State = UpdaterState.Inactive;
                Debug("Updater stoped");
            }
        }

        void Debug(string msg) => Log?.Invoke(this, new LogEventArgs(LogLevel.Debug, msg));
        void Warning(string msg) => Log?.Invoke(this, new LogEventArgs(LogLevel.Warning, msg));
        void Error(string msg) => Log?.Invoke(this, new LogEventArgs(LogLevel.Error, msg));
        void Error(string msg, Exception ex) => Error($"{msg}: {ex.Message}. {ex.InnerException?.Message ?? ""}");
    }

    enum UpdaterState
    {
        Inactive,
        Active,
        Busy
    }
}
