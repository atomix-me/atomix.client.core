﻿using Atomix.Abstract;
using ProtoBuf.Meta;

namespace Atomix.Common.Proto
{
    public static class MetaTypeExtensions
    {
        public static MetaType AddOptional(this MetaType metaType, int fieldNumber, string memberName)
        {
            var field = metaType.AddField(fieldNumber, memberName);
            field.IsRequired = false;

            return metaType;
        }

        public static MetaType AddRequired(this MetaType metaType, int fieldNumber, string memberName)
        {
            var field = metaType.AddField(fieldNumber, memberName);
            field.IsRequired = true;

            return metaType;
        }

        public static MetaType AddRequired(this MetaType metaType, string memberName)
        {
            var fieldsCount = metaType.GetFields().Length + metaType.GetSubtypes().Length;

            return metaType.AddRequired(fieldsCount + 1, memberName);
        }

        public static MetaType AddCurrencies(this MetaType metaType, ICurrencies currencies)
        {
            for (var i = 0; i < currencies.Count; ++i)
                metaType.AddSubType(i + 1, currencies[i].GetType());

            return metaType;
        }
    }
}