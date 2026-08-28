using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

internal static class OliverUnicodeText
{
    private static object _primaryFontAsset;
    private static object _historicFontAsset;
    private static object _symbolFontAsset;
    private static object _emojiFontAsset;
    private static Type _textMeshProType;
    private static Type _tmpFontAssetType;
    private static bool _fontAttempted;

    internal static void Apply(Component baseComponent, string original)
    {
        if (baseComponent == null || string.IsNullOrEmpty(original)) return;
        bool needsUnicode = original.Any(ch => ch > 127);
        if (!needsUnicode) return;

        // Resolve exact managed wrapper types only. Never use AccessTools.TypeByName here:
        // it scans every loaded Unity assembly and causes ReflectionTypeLoadException spam on IL2CPP.
        _textMeshProType ??= FindExactType("TMPro.TextMeshPro");
        if (_textMeshProType == null)
        {
            OliverBootstrap.LogSource?.LogWarning("[OLIVER] TMPro.TextMeshPro wrapper type was not found.");
            return;
        }

        object tmp;
        try
        {
            tmp = Activator.CreateInstance(_textMeshProType, new object[] { ((Il2CppObjectBase)baseComponent).Pointer });
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogWarning($"[OLIVER] Could not wrap BillboardText as TextMeshPro: {ex.Message}");
            return;
        }
        if (tmp == null) return;

        EnsureFonts();

        try
        {
            if (_primaryFontAsset != null)
            {
                PropertyInfo fontProp = GetProperty(_textMeshProType, "font");
                if (fontProp != null && fontProp.CanWrite)
                    fontProp.SetValue(tmp, _primaryFontAsset);
            }

            string output = ArabicPresentationShaper.ContainsArabic(original)
                ? ArabicPresentationShaper.ShapeForLTRBillboard(original)
                : original;

            PropertyInfo textProp = GetProperty(_textMeshProType, "text");
            if (textProp != null && textProp.CanWrite)
                textProp.SetValue(tmp, output);

            PropertyInfo rtl = GetProperty(_textMeshProType, "isRightToLeftText");
            if (rtl != null && rtl.CanWrite) rtl.SetValue(tmp, false);

            PropertyInfo richText = GetProperty(_textMeshProType, "richText");
            if (richText != null && richText.CanWrite) richText.SetValue(tmp, false);

            MethodInfo force = FindForceMeshUpdate(_textMeshProType);
            if (force != null)
            {
                ParameterInfo[] ps = force.GetParameters();
                if (ps.Length == 0) force.Invoke(tmp, null);
                else if (ps.Length == 1) force.Invoke(tmp, new object[] { false });
                else force.Invoke(tmp, new object[] { false, false });
            }
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogWarning($"[OLIVER] Unicode text apply skipped safely: {ex.Message}");
        }
    }

    private static void EnsureFonts()
    {
        if (_fontAttempted) return;
        _fontAttempted = true;

        try
        {
            _tmpFontAssetType = FindExactType("TMPro.TMP_FontAsset");
            if (_tmpFontAssetType == null)
            {
                OliverBootstrap.LogSource?.LogWarning("[OLIVER] TMPro.TMP_FontAsset wrapper type was not found.");
                return;
            }

            _primaryFontAsset = CreateDynamicFontAsset(new[] { "Tahoma", "Segoe UI", "Arial" });
            _historicFontAsset = CreateDynamicFontAsset(new[] { "Segoe UI Historic" });
            _symbolFontAsset = CreateDynamicFontAsset(new[] { "Segoe UI Symbol" });
            _emojiFontAsset = CreateDynamicFontAsset(new[] { "Segoe UI Emoji" });

            AddFallback(_primaryFontAsset, _historicFontAsset);
            AddFallback(_primaryFontAsset, _symbolFontAsset);
            AddFallback(_primaryFontAsset, _emojiFontAsset);

            if (_primaryFontAsset != null)
            {
                OliverBootstrap.LogSource?.LogInfo("[OLIVER] Arabic/Unicode dynamic TMP font ready (Arabic + Historic + Symbol + Emoji fallbacks).");
            }
            else
            {
                OliverBootstrap.LogSource?.LogWarning("[OLIVER] Could not create the primary dynamic TMP font asset.");
            }
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogWarning($"[OLIVER] Unicode font setup skipped: {ex.Message}");
        }
    }

    private static object CreateDynamicFontAsset(string[] candidates)
    {
        Font osFont = null;
        foreach (string candidate in candidates)
        {
            try
            {
                osFont = Font.CreateDynamicFontFromOSFont(candidate, 64);
                if (osFont != null) break;
            }
            catch { }
        }
        if (osFont == null) return null;

        MethodInfo create = null;
        try
        {
            MethodInfo[] methods = _tmpFontAssetType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            create = methods.FirstOrDefault(m =>
            {
                if (!string.Equals(m.Name, "CreateFontAsset", StringComparison.Ordinal)) return false;
                ParameterInfo[] ps = m.GetParameters();
                return ps.Length > 0 && ps[0].ParameterType == typeof(Font);
            });
        }
        catch { }
        if (create == null) return null;

        ParameterInfo[] parameters = create.GetParameters();
        object[] args = new object[parameters.Length];
        args[0] = osFont;
        for (int i = 1; i < parameters.Length; i++)
        {
            if (parameters[i].HasDefaultValue) args[i] = parameters[i].DefaultValue;
            else if (parameters[i].ParameterType.IsEnum) args[i] = Activator.CreateInstance(parameters[i].ParameterType);
            else if (parameters[i].ParameterType == typeof(int)) args[i] = 0;
            else if (parameters[i].ParameterType == typeof(float)) args[i] = 0f;
            else if (parameters[i].ParameterType == typeof(bool)) args[i] = false;
            else args[i] = null;
        }

        object asset;
        try { asset = create.Invoke(null, args); }
        catch { return null; }
        if (asset == null) return null;

        PropertyInfo pop = GetProperty(_tmpFontAssetType, "atlasPopulationMode");
        if (pop != null && pop.CanWrite && pop.PropertyType.IsEnum)
        {
            try { pop.SetValue(asset, Enum.Parse(pop.PropertyType, "Dynamic")); } catch { }
        }
        return asset;
    }

    private static void AddFallback(object primary, object fallback)
    {
        if (primary == null || fallback == null || _tmpFontAssetType == null) return;
        try
        {
            PropertyInfo prop = GetProperty(_tmpFontAssetType, "fallbackFontAssetTable");
            object list = prop?.GetValue(primary);
            if (list == null) return;

            MethodInfo add = list.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => string.Equals(m.Name, "Add", StringComparison.Ordinal) && m.GetParameters().Length == 1);
            add?.Invoke(list, new[] { fallback });
        }
        catch { }
    }

    private static Type FindExactType(string fullName)
    {
        try
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null) continue;
                try
                {
                    Type type = assembly.GetType(fullName, false, false);
                    if (type != null) return type;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static PropertyInfo GetProperty(Type type, string name)
    {
        try
        {
            Type current = type;
            while (current != null)
            {
                PropertyInfo prop = current.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (prop != null) return prop;
                current = current.BaseType;
            }
        }
        catch { }
        return null;
    }

    private static MethodInfo FindForceMeshUpdate(Type type)
    {
        try
        {
            Type current = type;
            while (current != null)
            {
                MethodInfo method = current.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => string.Equals(m.Name, "ForceMeshUpdate", StringComparison.Ordinal))
                    .OrderByDescending(m => m.GetParameters().Length)
                    .FirstOrDefault(m => m.GetParameters().Length <= 2);
                if (method != null) return method;
                current = current.BaseType;
            }
        }
        catch { }
        return null;
    }
}

internal static class ArabicPresentationShaper
{
    private readonly struct Forms
    {
        internal readonly char Isolated, Final, Initial, Medial;
        internal readonly bool JoinsNext;
        internal Forms(char isolated, char final, char initial, char medial, bool joinsNext)
        {
            Isolated = isolated; Final = final; Initial = initial; Medial = medial; JoinsNext = joinsNext;
        }
    }

    private static readonly Dictionary<char, Forms> Map = new Dictionary<char, Forms>
    {
        ['\u0621'] = new Forms('\uFE80','\uFE80','\uFE80','\uFE80',false),
        ['\u0622'] = new Forms('\uFE81','\uFE82','\uFE81','\uFE82',false),
        ['\u0623'] = new Forms('\uFE83','\uFE84','\uFE83','\uFE84',false),
        ['\u0624'] = new Forms('\uFE85','\uFE86','\uFE85','\uFE86',false),
        ['\u0625'] = new Forms('\uFE87','\uFE88','\uFE87','\uFE88',false),
        ['\u0626'] = new Forms('\uFE89','\uFE8A','\uFE8B','\uFE8C',true),
        ['\u0627'] = new Forms('\uFE8D','\uFE8E','\uFE8D','\uFE8E',false),
        ['\u0628'] = new Forms('\uFE8F','\uFE90','\uFE91','\uFE92',true),
        ['\u0629'] = new Forms('\uFE93','\uFE94','\uFE93','\uFE94',false),
        ['\u062A'] = new Forms('\uFE95','\uFE96','\uFE97','\uFE98',true),
        ['\u062B'] = new Forms('\uFE99','\uFE9A','\uFE9B','\uFE9C',true),
        ['\u062C'] = new Forms('\uFE9D','\uFE9E','\uFE9F','\uFEA0',true),
        ['\u062D'] = new Forms('\uFEA1','\uFEA2','\uFEA3','\uFEA4',true),
        ['\u062E'] = new Forms('\uFEA5','\uFEA6','\uFEA7','\uFEA8',true),
        ['\u062F'] = new Forms('\uFEA9','\uFEAA','\uFEA9','\uFEAA',false),
        ['\u0630'] = new Forms('\uFEAB','\uFEAC','\uFEAB','\uFEAC',false),
        ['\u0631'] = new Forms('\uFEAD','\uFEAE','\uFEAD','\uFEAE',false),
        ['\u0632'] = new Forms('\uFEAF','\uFEB0','\uFEAF','\uFEB0',false),
        ['\u0633'] = new Forms('\uFEB1','\uFEB2','\uFEB3','\uFEB4',true),
        ['\u0634'] = new Forms('\uFEB5','\uFEB6','\uFEB7','\uFEB8',true),
        ['\u0635'] = new Forms('\uFEB9','\uFEBA','\uFEBB','\uFEBC',true),
        ['\u0636'] = new Forms('\uFEBD','\uFEBE','\uFEBF','\uFEC0',true),
        ['\u0637'] = new Forms('\uFEC1','\uFEC2','\uFEC3','\uFEC4',true),
        ['\u0638'] = new Forms('\uFEC5','\uFEC6','\uFEC7','\uFEC8',true),
        ['\u0639'] = new Forms('\uFEC9','\uFECA','\uFECB','\uFECC',true),
        ['\u063A'] = new Forms('\uFECD','\uFECE','\uFECF','\uFED0',true),
        ['\u0641'] = new Forms('\uFED1','\uFED2','\uFED3','\uFED4',true),
        ['\u0642'] = new Forms('\uFED5','\uFED6','\uFED7','\uFED8',true),
        ['\u0643'] = new Forms('\uFED9','\uFEDA','\uFEDB','\uFEDC',true),
        ['\u0644'] = new Forms('\uFEDD','\uFEDE','\uFEDF','\uFEE0',true),
        ['\u0645'] = new Forms('\uFEE1','\uFEE2','\uFEE3','\uFEE4',true),
        ['\u0646'] = new Forms('\uFEE5','\uFEE6','\uFEE7','\uFEE8',true),
        ['\u0647'] = new Forms('\uFEE9','\uFEEA','\uFEEB','\uFEEC',true),
        ['\u0648'] = new Forms('\uFEED','\uFEEE','\uFEED','\uFEEE',false),
        ['\u0649'] = new Forms('\uFEEF','\uFEF0','\uFEEF','\uFEF0',false),
        ['\u064A'] = new Forms('\uFEF1','\uFEF2','\uFEF3','\uFEF4',true),
        ['\u067E'] = new Forms('\uFB56','\uFB57','\uFB58','\uFB59',true),
        ['\u0686'] = new Forms('\uFB7A','\uFB7B','\uFB7C','\uFB7D',true),
        ['\u0698'] = new Forms('\uFB8A','\uFB8B','\uFB8A','\uFB8B',false),
        ['\u06A9'] = new Forms('\uFB8E','\uFB8F','\uFB90','\uFB91',true),
        ['\u06AF'] = new Forms('\uFB92','\uFB93','\uFB94','\uFB95',true),
        ['\u06CC'] = new Forms('\uFBFC','\uFBFD','\uFBFE','\uFBFF',true),
    };

    internal static bool ContainsArabic(string s) => !string.IsNullOrEmpty(s) && s.Any(IsArabicBase);

    internal static string ShapeForLTRBillboard(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        char[] shaped = input.ToCharArray();
        for (int i = 0; i < shaped.Length; i++)
        {
            char c = input[i];
            if (!Map.TryGetValue(c, out Forms f)) continue;
            int prev = PreviousArabicIndex(input, i - 1);
            int next = NextArabicIndex(input, i + 1);
            bool joinPrev = prev >= 0 && Map.TryGetValue(input[prev], out Forms pf) && pf.JoinsNext && AreAdjacentIgnoringMarks(input, prev, i);
            bool joinNext = next >= 0 && f.JoinsNext && AreAdjacentIgnoringMarks(input, i, next);
            shaped[i] = joinPrev && joinNext ? f.Medial : joinPrev ? f.Final : joinNext ? f.Initial : f.Isolated;
        }
        int first = -1, last = -1;
        for (int i = 0; i < input.Length; i++) if (IsArabicBase(input[i]) || IsArabicMark(input[i])) { first = i; break; }
        for (int i = input.Length - 1; i >= 0; i--) if (IsArabicBase(input[i]) || IsArabicMark(input[i])) { last = i; break; }
        if (first < 0 || last < first) return new string(shaped);
        Array.Reverse(shaped, first, last - first + 1);
        return new string(shaped);
    }

    private static bool IsArabicBase(char c) => Map.ContainsKey(c) || (c >= '\u0600' && c <= '\u06FF' && !IsArabicMark(c));
    private static bool IsArabicMark(char c) => (c >= '\u064B' && c <= '\u065F') || c == '\u0670' || (c >= '\u06D6' && c <= '\u06ED');

    private static int PreviousArabicIndex(string s, int start)
    {
        for (int i = start; i >= 0; i--)
        {
            if (IsArabicMark(s[i])) continue;
            return Map.ContainsKey(s[i]) ? i : -1;
        }
        return -1;
    }

    private static int NextArabicIndex(string s, int start)
    {
        for (int i = start; i < s.Length; i++)
        {
            if (IsArabicMark(s[i])) continue;
            return Map.ContainsKey(s[i]) ? i : -1;
        }
        return -1;
    }

    private static bool AreAdjacentIgnoringMarks(string s, int a, int b)
    {
        int lo = Math.Min(a, b) + 1;
        int hi = Math.Max(a, b);
        for (int i = lo; i < hi; i++) if (!IsArabicMark(s[i])) return false;
        return true;
    }
}
