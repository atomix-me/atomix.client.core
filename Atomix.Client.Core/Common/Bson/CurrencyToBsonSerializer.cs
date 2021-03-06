﻿using System.Collections.Generic;
using System.Linq;
using Atomix.Core.Entities;
using LiteDB;

namespace Atomix.Common.Bson
{
    public class CurrencyToBsonSerializer : BsonSerializer<Currency>
    {
        private readonly IEnumerable<Currency> _currencies;

        public CurrencyToBsonSerializer(IEnumerable<Currency> currencies)
        {
            _currencies = currencies;
        }

        public override Currency Deserialize(BsonValue bsonValue)
        {
            return _currencies.FirstOrDefault(s => s.Name.Equals(bsonValue.AsString));
        }

        public override BsonValue Serialize(Currency currency)
        {
            return currency.Name;
        }
    }
}