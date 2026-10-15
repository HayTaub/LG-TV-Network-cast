using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace LGNetworkCast.Core
{
    /// <summary>Small JSON helpers on top of the built-in JavaScriptSerializer (no external packages).</summary>
    public static class Json
    {
        static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static string Serialize(object value) => Serializer.Serialize(value);

        public static T Deserialize<T>(string json) => Serializer.Deserialize<T>(json);

        public static Dictionary<string, object> Parse(string json) =>
            Serializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();

        public static Dictionary<string, object> Obj(this Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        public static string Str(this Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? v.ToString() : null;

        public static int? Int(this Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null && int.TryParse(v.ToString(), out var i) ? i : (int?)null;

        public static bool? Bool(this Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v is bool b ? b : (bool?)null;

        public static IEnumerable<Dictionary<string, object>> List(this Dictionary<string, object> d, string key)
        {
            if (d != null && d.TryGetValue(key, out var v) && v is IEnumerable list)
                foreach (var item in list)
                    if (item is Dictionary<string, object> o) yield return o;
        }
    }
}
