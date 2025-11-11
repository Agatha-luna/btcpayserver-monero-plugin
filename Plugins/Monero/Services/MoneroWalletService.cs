using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Monero.Configuration;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Monero.Services
{
    public class MoneroWalletService : IHostedService
    {
        private const string CryptoCode = "XMR";
        private readonly MoneroRPCProvider _rpcProvider;
        private readonly Logs _logs;
        private readonly ISettingsRepository _settingsRepository;
        private MoneroWalletState _walletState;

        public MoneroWalletService(
            MoneroRPCProvider rpcProvider,
            Logs logs,
            ISettingsRepository settingsRepository)
        {
            _rpcProvider = rpcProvider;
            _logs = logs;
            _settingsRepository = settingsRepository;
            _walletState = new MoneroWalletState();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!_rpcProvider.IsConfigured(CryptoCode))
                {
                    _logs.PayServer.LogWarning($"{CryptoCode} RPC not configured");
                    return;
                }

                var savedState = await _settingsRepository.GetSettingAsync<MoneroWalletState>();

                if (savedState?.PasswordFileMigrationComplete != true)
                {
                    var migratedWalletState = await TryMigrateLegacyPasswordFile();
                    if (migratedWalletState != null)
                    {
                        savedState = migratedWalletState;
                    }
                    else
                    {
                        if (savedState == null)
                        {
                            savedState = new MoneroWalletState();
                        }
                        savedState.PasswordFileMigrationComplete = true;
                        await _settingsRepository.UpdateSetting(savedState);
                    }
                }

                _walletState = savedState;

                if (!_walletState.IsInitialized)
                {
                    _logs.PayServer.LogInformation("No wallet configured - user will set up via UI");
                    return;
                }

                string password = _walletState.ActiveWalletPassword;
                string walletName = _walletState.ActiveWalletName;

                if (string.IsNullOrEmpty(walletName))
                {
                    _logs.PayServer.LogWarning("Active wallet address set but wallet record not found");
                    return;
                }

                var result = await _rpcProvider.OpenWallet(CryptoCode, walletName, password);

                if (result)
                {
                    _walletState.IsConnected = true;
                    _logs.PayServer.LogInformation($"Successfully opened wallet {walletName} on startup");
                }
                else
                {
                    _logs.PayServer.LogWarning($"Failed to open wallet {walletName} on startup");
                }
            }
            catch (Exception ex)
            {
                _logs.PayServer.LogError(ex, $"Error during {CryptoCode} wallet startup");
            }
        }

        private async Task<MoneroWalletState> TryMigrateLegacyPasswordFile()
        {
            try
            {
                string walletDir = _rpcProvider.GetWalletDirectory(CryptoCode);
                if (string.IsNullOrEmpty(walletDir))
                {
                    return null;
                }

                string passwordFile = Path.Combine(walletDir, "password");
                if (!File.Exists(passwordFile))
                {
                    return null;
                }

                string[] availableWallets = _rpcProvider.GetWalletList(CryptoCode);
                if (availableWallets is null || availableWallets.Length == 0)
                {
                    _logs.PayServer.LogWarning("Password file found but no wallet files exist");
                    return null;
                }

                _logs.PayServer.LogInformation("Found legacy password file, migrating wallet configuration");

                string password = (await File.ReadAllTextAsync(passwordFile)).Trim();
                string walletName = availableWallets.First();

                bool opened = await _rpcProvider.OpenWallet(CryptoCode, walletName, password);
                if (!opened)
                {
                    _logs.PayServer.LogWarning($"Failed to open wallet {walletName} during migration - password may be wrong");
                    return null;
                }

                var primaryAddressResponse = await _rpcProvider.GetAddress(CryptoCode, 0, 0);
                string primaryAddress = primaryAddressResponse?.Address;

                if (string.IsNullOrEmpty(primaryAddress))
                {
                    await _rpcProvider.CloseWallet(CryptoCode);
                    _logs.PayServer.LogError("Failed to get primary address during migration");
                    return null;
                }

                var walletState = new MoneroWalletState
                {
                    ActiveWalletAddress = primaryAddress,
                    LastActivatedAt = DateTimeOffset.UtcNow,
                    IsConnected = true,
                    PasswordFileMigrationComplete = true,
                    Wallets = new Dictionary<string, MoneroWalletState.WalletRecord>
                    {
                        [primaryAddress] = new MoneroWalletState.WalletRecord
                        {
                            Name = walletName,
                            Password = password
                        }
                    }
                };

                await _settingsRepository.UpdateSetting(walletState);
                _logs.PayServer.LogInformation($"Successfully migrated legacy wallet {walletName}");

                return walletState;
            }
            catch (Exception ex)
            {
                _logs.PayServer.LogError(ex, "Error during legacy wallet migration");
                return null;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_walletState.IsConnected)
                {
                    await _rpcProvider.CloseWallet(CryptoCode);
                    _walletState.IsConnected = false;
                }
            }
            catch (Exception ex)
            {
                _logs.PayServer.LogError(ex, $"Error closing wallet during shutdown");
            }
        }

        public async Task<(bool Success, string ErrorMessage)> SetActiveWallet(string primaryAddress, string changedByStoreId)
        {
            try
            {
                if (!_walletState.Wallets.TryGetValue(primaryAddress, out var walletRecord))
                {
                    return (false, $"Wallet with address {primaryAddress} is not imported.");
                }

                if (_walletState.IsConnected)
                {
                    await _rpcProvider.CloseWallet(CryptoCode);
                    _walletState.IsConnected = false;
                }

                var password = walletRecord.Password ?? "";
                var opened = await _rpcProvider.OpenWallet(CryptoCode, walletRecord.Name, password);
                if (!opened)
                {
                    return (false, "Failed to open wallet.");
                }

                _walletState.ActiveWalletAddress = primaryAddress;
                _walletState.LastActivatedAt = DateTimeOffset.UtcNow;
                _walletState.IsConnected = true;

                await _settingsRepository.UpdateSetting(_walletState);
                await _rpcProvider.UpdateSummary(CryptoCode);
                _logs.PayServer.LogInformation($"Active wallet changed to {walletRecord.Name} by store {changedByStoreId}");
                return (true, null);
            }
            catch (Exception ex)
            {
                _logs.PayServer.LogError(ex, $"Error setting active wallet to address {primaryAddress}");
                return (false, ex.Message);
            }
        }

        public MoneroWalletState GetWalletState()
        {
            return _walletState;
        }

        public async Task<(bool Success, string ErrorMessage)> CreateAndActivateWallet(
            string walletName,
            string primaryAddress,
            string privateViewKey,
            int restoreHeight,
            string createdByStoreId)
        {
            try
            {
                _logs.PayServer.LogInformation($"Creating and activating wallet {walletName} for store {createdByStoreId}");

                var (createSuccess, createError) = await _rpcProvider.CreateWalletFromKeys(
                    CryptoCode,
                    walletName,
                    primaryAddress,
                    privateViewKey,
                    "",
                    restoreHeight);

                if (!createSuccess)
                {
                    _logs.PayServer.LogError($"Failed to create wallet {walletName}: {createError}");
                    return (false, createError);
                }

                _logs.PayServer.LogInformation($"Successfully created wallet {walletName}");

                var newRecord = new MoneroWalletState.WalletRecord
                {
                    Name = walletName,
                    Password = ""
                };
                _walletState.Wallets[primaryAddress] = newRecord;

                _walletState.ActiveWalletAddress = primaryAddress;
                _walletState.LastActivatedAt = DateTimeOffset.UtcNow;
                _walletState.IsConnected = true;

                await _settingsRepository.UpdateSetting(_walletState);
                await _rpcProvider.UpdateSummary(CryptoCode);
                _logs.PayServer.LogInformation($"Active wallet changed to {walletName} by store {createdByStoreId}");

                return (true, null);
            }
            catch (Exception ex)
            {
                _logs.PayServer.LogError(ex, $"Error creating and activating wallet {walletName}");
                return (false, ex.Message);
            }
        }

        public async Task<(bool Success, string ErrorMessage)> DeleteWallet(string primaryAddress)
        {
            try
            {
                if (!_walletState.Wallets.TryGetValue(primaryAddress, out var walletRecord))
                {
                    return (false, $"Wallet with address {primaryAddress} is not imported.");
                }

                var walletName = walletRecord.Name;
                _logs.PayServer.LogInformation($"Deleting wallet {walletName} (primary address: {primaryAddress})");

                bool isActiveWallet = primaryAddress == _walletState.ActiveWalletAddress;
                if (isActiveWallet)
                {
                    if (_walletState.IsConnected)
                    {
                        await _rpcProvider.CloseWallet(CryptoCode);
                    }
                    _walletState.IsConnected = false;
                    _walletState.ActiveWalletAddress = null;
                }

                _walletState.Wallets.Remove(primaryAddress);
                await _settingsRepository.UpdateSetting(_walletState);

                var deleted = _rpcProvider.DeleteWallet(CryptoCode, walletName);
                if (!deleted)
                {
                    _logs.PayServer.LogWarning($"Failed to delete wallet files for {walletName}', file may still be on disk. Remove if still present.");
                }

                _logs.PayServer.LogInformation($"Successfully deleted wallet {walletName}");
                return (true, null);
            }
            catch (Exception ex)
            {
                _logs.PayServer.LogError(ex, $"Error deleting wallet with address {primaryAddress}");
                return (false, ex.Message);
            }
        }

    }
}