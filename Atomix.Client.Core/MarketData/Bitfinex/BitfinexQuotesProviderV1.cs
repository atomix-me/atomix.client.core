﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atomix.Common;
using Atomix.Core.Entities;
using Atomix.MarketData.Abstract;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Atomix.MarketData.Bitfinex
{
    public class BitfinexQuotesProviderV1 : QuotesProvider
    {
        public const string Usd = "USD";
        public const string LtcBtc = "LTCBTC";
        public const string EthBtc = "ETHBTC";
        public const string XtzBtc = "XTZBTC";

        private string BaseUrl { get; } = "https://api.bitfinex.com/v1/";

        public BitfinexQuotesProviderV1(
            params string[] symbols)
        {
            Quotes = symbols.ToDictionary(s => s, s => new Quote());
        }

        public BitfinexQuotesProviderV1(
            IEnumerable<Currency> currencies,
            string baseCurrency)
        {
            Quotes = currencies.ToDictionary(currency => $"{currency.Name}{baseCurrency}".ToUpper(), currency => new Quote());
        }

        public override Quote GetQuote(
            string currency,
            string baseCurrency)
        {
            return Quotes.TryGetValue($"{currency}{baseCurrency}".ToUpper(), out var rate) ? rate : null;
        }

        protected override async Task UpdateAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Log.Debug("Start of update");

            var isAvailable = true;

            try
            {
                foreach (var symbol in Quotes.Keys.ToList())
                {
                    var request = $"pubticker/{symbol.ToLower()}";

                    isAvailable = await HttpHelper.GetAsync(
                            baseUri: BaseUrl,
                            requestUri: request,
                            responseHandler: responseContent =>
                            {
                                var data = JsonConvert.DeserializeObject<JObject>(responseContent);

                                Quotes[symbol] = new Quote
                                {
                                    Bid = data["bid"].Value<decimal>(),
                                    Ask = data["ask"].Value<decimal>()
                                };

                                return true;
                            },
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                Log.Debug("Update finished");
            }
            catch (Exception e)
            {
                Log.Error(e, e.Message);

                isAvailable = false;
            }

            LastUpdateTime = DateTime.Now;

            if (isAvailable)
                LastSuccessUpdateTime = LastUpdateTime;

            if (IsAvailable != isAvailable)
            {
                IsAvailable = isAvailable;
                RiseAvailabilityChangedEvent(EventArgs.Empty);
            }

            if (IsAvailable)
                RiseQuotesUpdatedEvent(EventArgs.Empty);
        }
    }
}