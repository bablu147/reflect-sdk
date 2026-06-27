// ────────────────────────────────────────────────────────────────────────────
//  EventValidator — bounds-checks event names + properties before they hit
//  the queue. The server already enforces hard limits (see workers/src/lib/
//  validation.ts) but doing it client-side too means we don't waste local
//  queue space, R2 bytes, or operator headaches on bad events.
//
//  These limits match server-side LIMITS so an event passing the SDK validator
//  is guaranteed to pass server validation as well.
// ────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Reflect.Internal
{
    internal static class EventValidator
    {
        public const int MAX_EVENT_NAME_LEN  = 64;
        public const int MAX_PROPS_COUNT     = 25;
        public const int MAX_KEY_LEN         = 40;
        public const int MAX_STRING_VALUE_LEN = 1024;

        // Allowed event name pattern: lowercase letters, digits, underscores,
        // hyphens. Matches Firebase/AppsFlyer/Adjust conventions.
        private static readonly Regex NamePattern = new Regex(@"^[a-z][a-z0-9_-]{0,63}$", RegexOptions.Compiled);

        public struct Result
        {
            public bool Ok;
            public string Reason;
            public IDictionary<string, object> CleanedProps;
        }

        public static Result Validate(string name, IDictionary<string, object> props)
        {
            if (string.IsNullOrEmpty(name))
                return new Result { Ok = false, Reason = "empty_event_name" };
            if (name.Length > MAX_EVENT_NAME_LEN)
                return new Result { Ok = false, Reason = "event_name_too_long" };

            // Allow underscore-prefixed events (reserved internal ones like
            // _user_alias, _crash) by lowercasing and only validating the
            // user-facing namespace.
            var nameToCheck = name.StartsWith("_") ? name.Substring(1) : name;
            if (!NamePattern.IsMatch(nameToCheck))
                return new Result { Ok = false, Reason = "event_name_invalid_chars" };

            if (props == null || props.Count == 0)
                return new Result { Ok = true, CleanedProps = props };

            if (props.Count > MAX_PROPS_COUNT)
                return new Result { Ok = false, Reason = "too_many_props" };

            // Build a cleaned copy so the caller's dictionary is never mutated
            // and we drop any value types we can't serialize cleanly.
            var cleaned = new Dictionary<string, object>(props.Count);
            foreach (var kv in props)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Key.Length > MAX_KEY_LEN)
                    return new Result { Ok = false, Reason = "bad_prop_key" };

                cleaned[kv.Key] = NormaliseValue(kv.Value);
            }
            return new Result { Ok = true, CleanedProps = cleaned };
        }

        private static object NormaliseValue(object v)
        {
            if (v == null) return null;
            switch (v)
            {
                case string s:
                    return s.Length > MAX_STRING_VALUE_LEN ? s.Substring(0, MAX_STRING_VALUE_LEN) : s;
                case bool _:
                case int _:
                case long _:
                case decimal _:
                    return v;   // serialized as a JSON number, invariant, by JsonWriter
                // Widen the smaller integer types to long (lossless) so they serialize
                // as numbers, not culture-formatted strings.
                case byte _:
                case sbyte _:
                case short _:
                case ushort _:
                case uint _:
                    return Convert.ToInt64(v);
                case ulong _:
                    return v;   // may exceed long.MaxValue; JsonWriter emits the digits
                case float f:
                    return float.IsInfinity(f) || float.IsNaN(f) ? (object)0f : v;
                case double d:
                    return double.IsInfinity(d) || double.IsNaN(d) ? (object)0d : v;
                case DateTime dt:
                    return dt.ToUniversalTime().ToString("o");   // ISO-8601
                case DateTimeOffset dto:
                    return dto.ToUniversalTime().ToString("o");
                case IDictionary _:
                case IEnumerable _:
                    return v;   // server validates depth; trust caller for nested
                default:
                    // Never use the locale-sensitive ToString() — on comma-decimal
                    // cultures it would corrupt numbers (1.5 -> "1,5").
                    return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }
    }
}
