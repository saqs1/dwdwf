using System;
using System.Collections.Generic;
using System.Linq;

internal static class OliverCountryContext
{
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, Entry> ByAvatar = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private static readonly Dictionary<string, Entry> ByName = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private static readonly Dictionary<int, Entry> ByParent = new Dictionary<int, Entry>();
    private static readonly Dictionary<int, Entry> ByImage = new Dictionary<int, Entry>();
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        internal string Country;
        internal DateTime SeenUtc;
    }

    internal static string NormalizeVerifiedCountry(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string code = value.Trim().ToUpperInvariant();
        if (code.Length != 2 || !code.All(c => c >= 'A' && c <= 'Z')) return null;
        return Iso3166Alpha2.Contains(code) ? code : null;
    }

    internal static void Remember(string name, string avatarUrl, string countryCode)
    {
        string country = NormalizeVerifiedCountry(countryCode);
        if (country == null) return;

        Entry entry = new Entry { Country = country, SeenUtc = DateTime.UtcNow };
        lock (Sync)
        {
            CleanupLocked();
            if (!string.IsNullOrWhiteSpace(avatarUrl)) ByAvatar[avatarUrl.Trim()] = entry;
            if (!string.IsNullOrWhiteSpace(name)) ByName[name.Trim()] = entry;
        }
    }

    internal static string ResolveByAvatar(string avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl)) return null;
        lock (Sync)
        {
            CleanupLocked();
            return TryGetFresh(ByAvatar, avatarUrl.Trim());
        }
    }

    internal static string ResolveByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (Sync)
        {
            CleanupLocked();
            return TryGetFresh(ByName, name.Trim());
        }
    }

    internal static void BindParent(int parentId, string countryCode)
    {
        string country = NormalizeVerifiedCountry(countryCode);
        if (parentId == 0 || country == null) return;
        lock (Sync)
        {
            CleanupLocked();
            ByParent[parentId] = new Entry { Country = country, SeenUtc = DateTime.UtcNow };
        }
    }

    internal static string ResolveParent(int parentId)
    {
        if (parentId == 0) return null;
        lock (Sync)
        {
            CleanupLocked();
            return TryGetFresh(ByParent, parentId);
        }
    }

    internal static void BindImage(int imageId, string countryCode)
    {
        string country = NormalizeVerifiedCountry(countryCode);
        if (imageId == 0 || country == null) return;
        lock (Sync)
        {
            CleanupLocked();
            ByImage[imageId] = new Entry { Country = country, SeenUtc = DateTime.UtcNow };
        }
    }

    internal static string ResolveImage(int imageId)
    {
        if (imageId == 0) return null;
        lock (Sync)
        {
            CleanupLocked();
            return TryGetFresh(ByImage, imageId);
        }
    }

    private static string TryGetFresh(Dictionary<string, Entry> map, string key)
    {
        if (!map.TryGetValue(key, out Entry entry) || entry == null) return null;
        if (DateTime.UtcNow - entry.SeenUtc > MaxAge)
        {
            map.Remove(key);
            return null;
        }
        return entry.Country;
    }

    private static string TryGetFresh(Dictionary<int, Entry> map, int key)
    {
        if (!map.TryGetValue(key, out Entry entry) || entry == null) return null;
        if (DateTime.UtcNow - entry.SeenUtc > MaxAge)
        {
            map.Remove(key);
            return null;
        }
        return entry.Country;
    }

    private static void CleanupLocked()
    {
        DateTime cutoff = DateTime.UtcNow - MaxAge;
        RemoveOld(ByAvatar, cutoff);
        RemoveOld(ByName, cutoff);
        RemoveOld(ByParent, cutoff);
        RemoveOld(ByImage, cutoff);
    }

    private static void RemoveOld<TKey>(Dictionary<TKey, Entry> map, DateTime cutoff)
    {
        if (map.Count < 256) return;
        TKey[] oldKeys = map.Where(kv => kv.Value == null || kv.Value.SeenUtc < cutoff).Select(kv => kv.Key).ToArray();
        foreach (TKey key in oldKeys) map.Remove(key);
    }

    // ISO-3166-1 alpha-2 only. We never infer a country from language, IP,
    // timezone, username, CDN host, phone number, or streamer location.
    private static readonly HashSet<string> Iso3166Alpha2 = new HashSet<string>(
        ("AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ " +
         "CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR " +
         "GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO " +
         "JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR " +
         "MS MT MU MV MW MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO " +
         "RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW " +
         "TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW")
        .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);
}
