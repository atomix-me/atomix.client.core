﻿using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Atomix.Abstract;
using Atomix.Common.Configuration;
using Atomix.Core.Entities;
using Atomix.Swaps.Abstract;
using Microsoft.Extensions.Configuration;
using NBitcoin;

namespace Atomix.Client.Core.Tests
{
    public static class Common
    {
        public static Key Alice { get; } = new Key();
        public static Key Bob { get; } = new Key();
        public static byte[] Secret { get; } = Encoding.UTF8.GetBytes("atomix");
        public static byte[] SecretHash { get; } = CurrencySwap.CreateSwapSecretHash(Secret);

        private static Assembly CoreAssembly { get; } = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Atomix.Client.Core");

        public static readonly IConfiguration CurrenciesConfiguration = new ConfigurationBuilder()
            .AddEmbeddedJsonFile(CoreAssembly, "currencies.json")
            .Build();

        private static readonly IConfiguration SymbolsConfiguration = new ConfigurationBuilder()
            .AddEmbeddedJsonFile(CoreAssembly, "symbols.json")
            .Build();

        public static readonly ICurrencies CurrenciesTestNet
            = new Currencies(CurrenciesConfiguration.GetSection(Atomix.Core.Network.TestNet.ToString()));

        public static readonly ISymbols SymbolsTestNet
            = new Symbols(SymbolsConfiguration.GetSection(Atomix.Core.Network.TestNet.ToString()), CurrenciesTestNet);

        public static readonly ICurrencies CurrenciesMainNet
            = new Currencies(CurrenciesConfiguration.GetSection(Atomix.Core.Network.MainNet.ToString()));

        public static readonly ISymbols SymbolsMainNet
            = new Symbols(SymbolsConfiguration.GetSection(Atomix.Core.Network.MainNet.ToString()), CurrenciesMainNet);

        public static Bitcoin BtcMainNet => CurrenciesMainNet.Get<Bitcoin>();
        public static Litecoin LtcMainNet => CurrenciesMainNet.Get<Litecoin>();

        public static Bitcoin BtcTestNet => CurrenciesTestNet.Get<Bitcoin>();
        public static Litecoin LtcTestNet => CurrenciesTestNet.Get<Litecoin>();
        public static Tezos XtzTestNet => CurrenciesTestNet.Get<Tezos>();
        public static Ethereum EthTestNet => CurrenciesTestNet.Get<Ethereum>();

        public static Symbol EthBtcTestNet => SymbolsTestNet.GetByName("ETH/BTC");
        public static Symbol LtcBtcTestNet => SymbolsTestNet.GetByName("LTC/BTC");

        public static string AliceAddress(BitcoinBasedCurrency currency)
        {
            return Alice.PubKey
                .GetAddress(ScriptPubKeyType.Legacy, currency.Network)
                .ToString();
        }

        public static string BobAddress(BitcoinBasedCurrency currency)
        {
            return Bob.PubKey
                .GetAddress(ScriptPubKeyType.Legacy, currency.Network)
                .ToString();
        }

        public static string AliceSegwitAddress(BitcoinBasedCurrency currency)
        {
            return Alice.PubKey.GetSegwitAddress(currency.Network).ToString();
        }

        public static string BobSegwitAddress(BitcoinBasedCurrency currency)
        {
            return Bob.PubKey.GetSegwitAddress(currency.Network).ToString();
        }
    }
}