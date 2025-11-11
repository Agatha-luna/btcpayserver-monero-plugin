using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Monero.Configuration
{
    public class MoneroWalletState
    {
        public string ActiveWalletAddress { get; set; }

        public string ActiveWalletName => GetActiveRecord()?.Name;

        //TODO: Deprecate password functionality. Maintaining for backward compatibility.
        public string ActiveWalletPassword => GetActiveRecord()?.Password ?? "";

        public DateTimeOffset? LastActivatedAt { get; set; }

        public bool IsInitialized => !string.IsNullOrEmpty(ActiveWalletAddress);

        public bool IsConnected { get; set; }

        public bool PasswordFileMigrationComplete { get; set; }

        public Dictionary<string, WalletRecord> Wallets { get; set; } = [];

        private WalletRecord GetActiveRecord()
        {
            if (string.IsNullOrEmpty(ActiveWalletAddress))
            {
                return null;
            }

            if (Wallets != null && Wallets.TryGetValue(ActiveWalletAddress, out var record))
            {
                return record;
            }

            return null;
        }

        public class WalletRecord
        {
            public string Name { get; set; }
            public string Password { get; set; }
        }
    }
}