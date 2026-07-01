using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.CardFlows
{
    public static class CardFlowArgumentUtility
    {
        public static string GetValue(IReadOnlyList<StringKeyValuePair> pairs, string key)
        {
            if (pairs == null || string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            for (var i = 0; i < pairs.Count; i++)
            {
                var pair = pairs[i];
                if (pair != null && pair.Key == key)
                {
                    return pair.Value ?? string.Empty;
                }
            }

            return string.Empty;
        }

        public static List<StringKeyValuePair> Clone(IReadOnlyList<StringKeyValuePair> pairs)
        {
            var result = new List<StringKeyValuePair>();
            if (pairs == null)
            {
                return result;
            }

            for (var i = 0; i < pairs.Count; i++)
            {
                var pair = pairs[i];
                if (pair == null)
                {
                    continue;
                }

                result.Add(new StringKeyValuePair
                {
                    Key = pair.Key ?? string.Empty,
                    Value = pair.Value ?? string.Empty
                });
            }

            return result;
        }

        public static void SetValue(List<StringKeyValuePair> pairs, string key, string value)
        {
            if (pairs == null || string.IsNullOrEmpty(key))
            {
                return;
            }

            for (var i = 0; i < pairs.Count; i++)
            {
                if (pairs[i] != null && pairs[i].Key == key)
                {
                    pairs[i].Value = value ?? string.Empty;
                    return;
                }
            }

            pairs.Add(new StringKeyValuePair
            {
                Key = key,
                Value = value ?? string.Empty
            });
        }
    }
}
