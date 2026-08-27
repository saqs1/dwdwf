using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OliverSupermarketEnhancer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BasePlugin
    {
        public const string PluginGuid = "oliver.tik.supermarket.enhancer";
        public const string PluginName = "OLIVER Supermarket Enhancer";
        public const string PluginVersion = "0.1.0-phase1";

        private static ManualLogSource _log;
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<string> _primaryFontName;
        private static Harmony _harmony;
        private static bool _insideRewrite;
        private static bool _fontBuildAttempted;
        private static object _primaryTmpFont;
        private static readonly List<object> _tmpFonts = new List<object>();
        private static readonly HashSet<MethodBase> _patched = new HashSet<MethodBase>();

        public override void Load()
        {
            _log = Log;
            _enabled = Config.Bind("Phase1_ArabicNames", "Enabled", true,
                "Fix Arabic and decorated TikTok usernames on S2E BillboardText only.");
            _primaryFontName = Config.Bind("Phase1_ArabicNames", "PrimaryFont", "Tahoma",
                "Preferred Windows font. Safe fallbacks are tried automatically.");

            try
            {
                _harmony = new Harmony(PluginGuid + ".phase1");
                int count = PatchTmpTextSetters();
                if (count == 0)
                    Log.LogWarning("[OLIVER] TMP text setter was not found. No game object was modified.");
                else
                    Log.LogInfo("[OLIVER] Phase 1 loaded safely. Patched TMP setter count: " + count + ". Original S2E DLL is untouched.");
            }
            catch (Exception ex)
            {
                Log.LogError("[OLIVER] Phase 1 initialization failed safely: " + ex);
            }
        }

        private static int PatchTmpTextSetters()
        {
            var postfix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(TextSetterPostfix), BindingFlags.NonPublic | BindingFlags.Static));
            int count = 0;
            string[] names = { "TMPro.TMP_Text", "TMPro.TextMeshPro", "TMPro.TextMeshProUGUI" };

            foreach (string typeName in names)
            {
                Type t = FindType(typeName);
                if (t == null) continue;

                MethodInfo setter = t.GetMethod("set_text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);
                if (setter == null || _patched.Contains(setter)) continue;

                try
                {
                    _harmony.Patch(setter, postfix: postfix);
                    _patched.Add(setter);
                    count++;
                }
                catch (Exception ex)
                {
                    _log.LogDebug("[OLIVER] Setter patch skipped for " + typeName + ": " + ex.Message);
                }
            }
            return count;
        }

        private static void TextSetterPostfix(object __instance, string __0)
        {
            if (_insideRewrite || _enabled == null || !_enabled.Value || __instance == null || string.IsNullOrEmpty(__0))
                return;

            // Preserve normal game text and normal English S2E names completely.
            if (!ContainsNonAscii(__0))
                return;

            try
            {
                if (!IsS2EBillboardText(__instance))
                    return;

                if (!EnsureFontChain())
                {
                    _log.LogWarning("[OLIVER] Arabic username detected, but no safe Windows TMP font could be created. Existing text was left unchanged.");
                    return;
                }

                string rendered = ArabicNameShaper.ShapeArabicSegments(__0);
                ApplyFontAndText(__instance, rendered);
                _log.LogDebug("[OLIVER] Billboard username processed: " + __0);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[OLIVER] Username fix skipped safely: " + ex.Message);
            }
        }

        private static bool IsS2EBillboardText(object textObject)
        {
            object gameObject = GetPropertyValue(textObject, "gameObject");
            if (gameObject == null) return false;
            string name = GetPropertyValue(gameObject, "name") as string;
            return string.Equals(name, "BillboardText", StringComparison.Ordinal);
        }

        private static void ApplyFontAndText(object textObject, string rendered)
        {
            Type t = textObject.GetType();
            PropertyInfo fontProp = FindProperty(t, "font");
            if (fontProp != null && fontProp.CanWrite)
                fontProp.SetValue(textObject, _primaryTmpFont, null);

            PropertyInfo rtlProp = FindProperty(t, "isRightToLeftText");
            if (rtlProp != null && rtlProp.CanWrite)
            {
                try { rtlProp.SetValue(textObject, false, null); } catch { }
            }

            PropertyInfo textProp = FindProperty(t, "text");
            if (textProp == null || !textProp.CanWrite)
                return;

            _insideRewrite = true;
            try
            {
                textProp.SetValue(textObject, rendered, null);
                ForceMeshUpdate(textObject);
            }
            finally
            {
                _insideRewrite = false;
            }
        }

        private static void ForceMeshUpdate(object textObject)
        {
            try
            {
                MethodInfo m = textObject.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(x => x.Name == "ForceMeshUpdate" && x.GetParameters().Length == 2 &&
                                         x.GetParameters()[0].ParameterType == typeof(bool) && x.GetParameters()[1].ParameterType == typeof(bool));
                if (m != null) m.Invoke(textObject, new object[] { false, false });
            }
            catch { }
        }

        private static bool EnsureFontChain()
        {
            if (_primaryTmpFont != null) return true;
            if (_fontBuildAttempted) return false;
            _fontBuildAttempted = true;

            Type unityFontType = FindType("UnityEngine.Font");
            Type tmpFontType = FindType("TMPro.TMP_FontAsset");
            if (unityFontType == null || tmpFontType == null)
                return false;

            string[] installed = GetInstalledFontNames(unityFontType);
            if (installed == null || installed.Length == 0)
                return false;

            var requested = new List<string>();
            AddUnique(requested, _primaryFontName == null ? null : _primaryFontName.Value);
            AddUnique(requested, "Tahoma");
            AddUnique(requested, "Segoe UI");
            AddUnique(requested, "Arial");
            AddUnique(requested, "Segoe UI Symbol");
            AddUnique(requested, "Segoe UI Historic");

            foreach (string wanted in requested)
            {
                string actual = installed.FirstOrDefault(x => string.Equals(x, wanted, StringComparison.OrdinalIgnoreCase));
                if (actual == null) continue;

                object tmp = CreateTmpFontAsset(unityFontType, tmpFontType, actual);
                if (tmp == null) continue;
                _tmpFonts.Add(tmp);
                if (_primaryTmpFont == null) _primaryTmpFont = tmp;
            }

            if (_primaryTmpFont == null)
                return false;

            WireFallbacks(_primaryTmpFont, _tmpFonts);
            _log.LogInfo("[OLIVER] Unicode font chain ready. Primary: " + GetPropertyValue(_primaryTmpFont, "name"));
            return true;
        }

        private static string[] GetInstalledFontNames(Type unityFontType)
        {
            try
            {
                MethodInfo m = unityFontType.GetMethod("GetOSInstalledFontNames", BindingFlags.Public | BindingFlags.Static,
                    null, Type.EmptyTypes, null);
                return m == null ? null : m.Invoke(null, null) as string[];
            }
            catch (Exception ex)
            {
                _log.LogDebug("[OLIVER] Font enumeration failed: " + ex.Message);
                return null;
            }
        }

        private static object CreateTmpFontAsset(Type unityFontType, Type tmpFontType, string fontName)
        {
            try
            {
                MethodInfo makeOsFont = unityFontType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CreateDynamicFontFromOSFont" && m.GetParameters().Length == 2 &&
                                         m.GetParameters()[0].ParameterType == typeof(string) &&
                                         m.GetParameters()[1].ParameterType == typeof(int));
                if (makeOsFont == null) return null;

                object osFont = makeOsFont.Invoke(null, new object[] { fontName, 64 });
                if (osFont == null) return null;

                MethodInfo makeTmp = tmpFontType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CreateFontAsset" && m.GetParameters().Length == 1 &&
                                         m.GetParameters()[0].ParameterType.IsAssignableFrom(unityFontType));
                if (makeTmp == null)
                {
                    makeTmp = tmpFontType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "CreateFontAsset" && m.GetParameters().Length == 1);
                }
                if (makeTmp == null) return null;

                object tmp = makeTmp.Invoke(null, new[] { osFont });
                if (tmp == null) return null;

                SetPropertyValue(tmp, "name", "OLIVER_" + fontName);
                SetPropertyValue(tmp, "isMultiAtlasTexturesEnabled", true);

                PropertyInfo population = FindProperty(tmp.GetType(), "atlasPopulationMode");
                if (population != null && population.CanWrite && population.PropertyType.IsEnum)
                {
                    try { population.SetValue(tmp, Enum.Parse(population.PropertyType, "Dynamic"), null); } catch { }
                }

                return tmp;
            }
            catch (Exception ex)
            {
                _log.LogDebug("[OLIVER] Font candidate skipped (" + fontName + "): " + ex.Message);
                return null;
            }
        }

        private static void WireFallbacks(object primary, List<object> fonts)
        {
            try
            {
                object list = GetPropertyValue(primary, "fallbackFontAssetTable");
                if (list == null) return;
                MethodInfo add = list.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);
                if (add == null) return;

                foreach (object font in fonts)
                {
                    if (font == null || ReferenceEquals(font, primary)) continue;
                    try { add.Invoke(list, new[] { font }); } catch { }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug("[OLIVER] Fallback chain wiring skipped: " + ex.Message);
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(fullName, false, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static PropertyInfo FindProperty(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p;
            }
            return null;
        }

        private static object GetPropertyValue(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                PropertyInfo p = FindProperty(obj.GetType(), name);
                return p == null || !p.CanRead ? null : p.GetValue(obj, null);
            }
            catch { return null; }
        }

        private static void SetPropertyValue(object obj, string name, object value)
        {
            if (obj == null) return;
            try
            {
                PropertyInfo p = FindProperty(obj.GetType(), name);
                if (p != null && p.CanWrite) p.SetValue(obj, value, null);
            }
            catch { }
        }

        private static bool ContainsNonAscii(string value)
        {
            for (int i = 0; i < value.Length; i++)
                if (value[i] > 0x7F) return true;
            return false;
        }

        private static void AddUnique(List<string> list, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!list.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))) list.Add(value);
        }
    }

    internal static class ArabicNameShaper
    {
        private readonly struct Forms
        {
            public readonly char Isolated;
            public readonly char Final;
            public readonly char Initial;
            public readonly char Medial;
            public Forms(char isolated, char final, char initial = '\0', char medial = '\0')
            {
                Isolated = isolated; Final = final; Initial = initial; Medial = medial;
            }
            public bool CanConnectToPrevious { get { return Final != '\0'; } }
            public bool CanConnectToNext { get { return Initial != '\0'; } }
        }

        private static readonly Dictionary<char, Forms> Map = new Dictionary<char, Forms>
        {
            ['\u0621'] = new Forms('\uFE80','\0'),
            ['\u0622'] = new Forms('\uFE81','\uFE82'), ['\u0623'] = new Forms('\uFE83','\uFE84'),
            ['\u0624'] = new Forms('\uFE85','\uFE86'), ['\u0625'] = new Forms('\uFE87','\uFE88'),
            ['\u0626'] = new Forms('\uFE89','\uFE8A','\uFE8B','\uFE8C'),
            ['\u0627'] = new Forms('\uFE8D','\uFE8E'), ['\u0628'] = new Forms('\uFE8F','\uFE90','\uFE91','\uFE92'),
            ['\u0629'] = new Forms('\uFE93','\uFE94'), ['\u062A'] = new Forms('\uFE95','\uFE96','\uFE97','\uFE98'),
            ['\u062B'] = new Forms('\uFE99','\uFE9A','\uFE9B','\uFE9C'), ['\u062C'] = new Forms('\uFE9D','\uFE9E','\uFE9F','\uFEA0'),
            ['\u062D'] = new Forms('\uFEA1','\uFEA2','\uFEA3','\uFEA4'), ['\u062E'] = new Forms('\uFEA5','\uFEA6','\uFEA7','\uFEA8'),
            ['\u062F'] = new Forms('\uFEA9','\uFEAA'), ['\u0630'] = new Forms('\uFEAB','\uFEAC'),
            ['\u0631'] = new Forms('\uFEAD','\uFEAE'), ['\u0632'] = new Forms('\uFEAF','\uFEB0'),
            ['\u0633'] = new Forms('\uFEB1','\uFEB2','\uFEB3','\uFEB4'), ['\u0634'] = new Forms('\uFEB5','\uFEB6','\uFEB7','\uFEB8'),
            ['\u0635'] = new Forms('\uFEB9','\uFEBA','\uFEBB','\uFEBC'), ['\u0636'] = new Forms('\uFEBD','\uFEBE','\uFEBF','\uFEC0'),
            ['\u0637'] = new Forms('\uFEC1','\uFEC2','\uFEC3','\uFEC4'), ['\u0638'] = new Forms('\uFEC5','\uFEC6','\uFEC7','\uFEC8'),
            ['\u0639'] = new Forms('\uFEC9','\uFECA','\uFECB','\uFECC'), ['\u063A'] = new Forms('\uFECD','\uFECE','\uFECF','\uFED0'),
            ['\u0641'] = new Forms('\uFED1','\uFED2','\uFED3','\uFED4'), ['\u0642'] = new Forms('\uFED5','\uFED6','\uFED7','\uFED8'),
            ['\u0643'] = new Forms('\uFED9','\uFEDA','\uFEDB','\uFEDC'), ['\u0644'] = new Forms('\uFEDD','\uFEDE','\uFEDF','\uFEE0'),
            ['\u0645'] = new Forms('\uFEE1','\uFEE2','\uFEE3','\uFEE4'), ['\u0646'] = new Forms('\uFEE5','\uFEE6','\uFEE7','\uFEE8'),
            ['\u0647'] = new Forms('\uFEE9','\uFEEA','\uFEEB','\uFEEC'), ['\u0648'] = new Forms('\uFEED','\uFEEE'),
            ['\u0649'] = new Forms('\uFEEF','\uFEF0'), ['\u064A'] = new Forms('\uFEF1','\uFEF2','\uFEF3','\uFEF4'),
            ['\u067E'] = new Forms('\uFB56','\uFB57','\uFB58','\uFB59'),
            ['\u0686'] = new Forms('\uFB7A','\uFB7B','\uFB7C','\uFB7D'),
            ['\u0698'] = new Forms('\uFB8A','\uFB8B'),
            ['\u06A9'] = new Forms('\uFB8E','\uFB8F','\uFB90','\uFB91'),
            ['\u06AF'] = new Forms('\uFB92','\uFB93','\uFB94','\uFB95'),
            ['\u06CC'] = new Forms('\uFBFC','\uFBFD','\uFBFE','\uFBFF')
        };

        public static string ShapeArabicSegments(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            bool hasArabic = input.Any(IsArabicBase);
            if (!hasArabic) return input;

            var output = new StringBuilder(input.Length * 2);
            int i = 0;
            while (i < input.Length)
            {
                if (!IsArabicSegmentChar(input[i]))
                {
                    output.Append(input[i++]);
                    continue;
                }

                int start = i;
                bool containsArabic = false;
                while (i < input.Length && IsArabicSegmentChar(input[i]))
                {
                    if (IsArabicBase(input[i])) containsArabic = true;
                    i++;
                }
                string segment = input.Substring(start, i - start);
                output.Append(containsArabic ? ShapeAndReverseSegment(segment) : segment);
            }
            return output.ToString();
        }

        private static string ShapeAndReverseSegment(string segment)
        {
            List<Unit> units = Tokenize(segment);
            for (int i = 0; i < units.Count; i++)
            {
                Forms forms;
                if (!Map.TryGetValue(units[i].Base, out forms)) continue;
                int prev = PreviousJoinable(units, i);
                int next = NextJoinable(units, i);
                Forms pf, nf;
                bool connectPrev = prev >= 0 && Map.TryGetValue(units[prev].Base, out pf) && pf.CanConnectToNext && forms.CanConnectToPrevious;
                bool connectNext = next >= 0 && Map.TryGetValue(units[next].Base, out nf) && forms.CanConnectToNext && nf.CanConnectToPrevious;

                char shaped;
                if (connectPrev && connectNext && forms.Medial != '\0') shaped = forms.Medial;
                else if (connectPrev && forms.Final != '\0') shaped = forms.Final;
                else if (connectNext && forms.Initial != '\0') shaped = forms.Initial;
                else shaped = forms.Isolated;
                units[i] = units[i].WithBase(shaped);
            }

            units.Reverse();
            var sb = new StringBuilder(segment.Length * 2);
            foreach (Unit unit in units) sb.Append(unit.Text);
            return sb.ToString();
        }

        private static List<Unit> Tokenize(string segment)
        {
            var units = new List<Unit>();
            for (int i = 0; i < segment.Length; i++)
            {
                char c = segment[i];
                if (char.IsDigit(c))
                {
                    int start = i;
                    while (i + 1 < segment.Length && char.IsDigit(segment[i + 1])) i++;
                    string number = segment.Substring(start, i - start + 1);
                    char[] a = number.ToCharArray(); Array.Reverse(a);
                    units.Add(new Unit('\0', new string(a)));
                    continue;
                }

                var sb = new StringBuilder();
                sb.Append(c);
                if (IsArabicBase(c) || c == '\u0640')
                    while (i + 1 < segment.Length && IsArabicMark(segment[i + 1])) sb.Append(segment[++i]);
                units.Add(new Unit(c, sb.ToString()));
            }
            return units;
        }

        private static int PreviousJoinable(List<Unit> units, int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                if (Map.ContainsKey(units[i].Base)) return i;
                return -1;
            }
            return -1;
        }

        private static int NextJoinable(List<Unit> units, int index)
        {
            for (int i = index + 1; i < units.Count; i++)
            {
                if (Map.ContainsKey(units[i].Base)) return i;
                return -1;
            }
            return -1;
        }

        private static bool IsArabicBase(char c) { return Map.ContainsKey(c); }
        private static bool IsArabicMark(char c) { return (c >= '\u064B' && c <= '\u065F') || c == '\u0670'; }
        private static bool IsArabicSegmentChar(char c)
        {
            return IsArabicBase(c) || IsArabicMark(c) || c == '\u0640' || char.IsWhiteSpace(c) || char.IsDigit(c) ||
                   (c >= '\u0660' && c <= '\u0669') || c == '\u060C' || c == '\u061B' || c == '\u061F';
        }

        private readonly struct Unit
        {
            public readonly char Base;
            public readonly string Text;
            public Unit(char @base, string text) { Base = @base; Text = text; }
            public Unit WithBase(char shaped)
            {
                if (string.IsNullOrEmpty(Text)) return this;
                return new Unit(shaped, shaped + Text.Substring(1));
            }
        }
    }
}
