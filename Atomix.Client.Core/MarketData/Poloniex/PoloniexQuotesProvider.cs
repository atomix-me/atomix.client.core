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

namespace Atomix.MarketData.Poloniex
{
    public class PoloniexQuotesProvider : QuotesProvider
    {
        public const string Usd = "USDT";
        public string BaseUrl { get; } = "https://poloniex.com/";

        public PoloniexQuotesProvider(
            IEnumerable<Currency> currencies,
            string baseCurrency)
        {
            if (currencies == null)
                throw new ArgumentNullException(nameof(currencies));

            if (baseCurrency == null)
                throw new ArgumentNullException(nameof(baseCurrency));

            Quotes = currencies.ToDictionary(currency => $"{baseCurrency}_{currency.Name}".ToUpper(), currency => new Quote());
        }

        public override Quote GetQuote(
            string currency,
            string baseCurrency)
        {
            if (currency == null)
                throw new ArgumentNullException(nameof(currency));

            if (baseCurrency == null)
                throw new ArgumentNullException(nameof(baseCurrency));

            if (baseCurrency.ToUpper().Equals("USD"))
                baseCurrency = Usd;

            return Quotes.TryGetValue($"{baseCurrency}_{currency}".ToUpper(), out var rate)
                ? rate
                : null;
        }

        protected override async Task UpdateAsync(
            CancellationToken cancellation = default(CancellationToken))
        {
            Log.Debug("Start of update");

            bool isAvailable;

            try
            {
                const string request = "public?command=returnTicker";

                Log.Debug("Send request: {@request}", request);

                isAvailable = await HttpHelper.GetAsync(
                        baseUri: BaseUrl,
                        requestUri: request,
                        responseHandler: ResponseHandler,
                        cancellationToken: cancellation)
                    .ConfigureAwait(false);
                
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

        private bool ResponseHandler(
            string responseContent)
        {
            var data = JsonConvert.DeserializeObject<JObject>(responseContent);

            foreach (var symbol in Quotes.Keys.ToList())
            {
                if (data.ContainsKey(symbol))
                {
                    Quotes[symbol] = new Quote {
                        Bid = data[symbol]["highestBid"].Value<decimal>(),
                        Ask = data[symbol]["lowestAsk"].Value<decimal>()
                    };
                }
                else
                {
                    Log.Warning("Can't find rates for symbol {@symbol}", symbol);
                }
            }

            return true;
        }
    }
}