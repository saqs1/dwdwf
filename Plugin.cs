using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace OliverSupermarketEnhancer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BasePlugin
    {
        public const string PluginGuid = "oliver.tik.supermarket.enhancer";
        public const string PluginName = "OLIVER Supermarket Enhancer";
        public const string PluginVersion = "0.1.1-phase1-safe";

        internal static ManualLogSource Logger;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> PrimaryFontName;

        private static bool _fontBuildAttempted;
        private static object _primaryTmpFont;
        private static readonly List<object> TmpFonts = new List<object>();
        private static readonly Dictionary<int, string> LastRendered = new Dictionary<int, string>();

        public override void Load()
        {
            Logger = Log;
            Enabled = Config.Bind("Phase1_ArabicNames", "Enabled", true,
                "Fix Arabic/decorated TikTok usernames on S2E BillboardText only.");
            PrimaryFontName = Config.Bind("Phase1_ArabicNames", "PrimaryFont", "Tahoma",
                "Preferred Windows Arabic font. Fallbacks are tried automatically.");

            try
            {
                AddComponent<BillboardDriver>();
                Log.LogInfo("[OLIVER] Phase 1 SAFE loaded. NO Harmony patches. Waiting for S2E BillboardText objects only.");
            }
            catch (Exception ex)
            {
                Log.LogError("[OLIVER] Driver initialization failed safely: " + ex);
            }
        }

        internal static void ScanBillboards()
        {
            if (Enabled == null || !Enabled.Value) return;

            try
            {
                GameObject[] objects = Resources.FindObjectsOfTypeAll<GameObject>();
                if (objects == null || objects.Length == 0) return;

                foreach (GameObject go in objects)
                {
                    if (go == null || !string.Equals(go.name, "BillboardText", StringComparison.Ordinal))
                        continue;

                    if (!go.activeInHierarchy) continue;

                    Component[] components;
                    try { components = go.GetComponents<Component>(); }
                    catch { continue; }

                    if (components == null) continue;
                    foreach (Component component in components)
                    {
                        if (component == null) continue;
                        Type t = component.GetType();
                        string fullName = t.FullName ?? string.Empty;
                        if (!fullName.StartsWith("TMPro.", StringComparison.Ordinal)) continue;

                        ProcessTextComponent(component);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug("[OLIVER] Billboard scan skipped safely: " + ex.Message);
            }
        }

        private static void ProcessTextComponent(object textObject)
        {
            try
            {
                PropertyInfo textProp = FindProperty(textObject.GetType(), "text");
                if (textProp == null || !textProp.CanRead || !textProp.CanWrite) return;

                string current = textProp.GetValue(textObject, null) as string;
                if (string.IsNullOrEmpty(current) || !ContainsArabicOrDecorated(current)) return;

                int id = GetUnityInstanceId(textObject);
                string oldRendered;
                if (id != 0 && LastRendered.TryGetValue(id, out oldRendered) && string.Equals(current, oldRendered, StringComparison.Ordinal))
                    return;

                if (!EnsureFontChain())
                {
                    Logger.LogDebug("[OLIVER] Arabic username found but Unicode font asset is not ready yet.");
                    return;
                }

                string rendered = ArabicNameShaper.ShapeArabicSegments(current);

                PropertyInfo fontProp = FindProperty(textObject.GetType(), "font");
                if (fontProp != null && fontProp.CanWrite)
                {
                    try { fontProp.SetValue(textObject, _primaryTmpFont, null); } catch { }
                }

                // We shape/reorder Arabic ourselves, so leave TMP RTL processing off.
                PropertyInfo rtlProp = FindProperty(textObject.GetType(), "isRightToLeftText");
                if (rtlProp != null && rtlProp.CanWrite)
                {
                    try { rtlProp.SetValue(textObject, false, null); } catch { }
                }

                textProp.SetValue(textObject, rendered, null);
                ForceMeshUpdate(textObject);
                if (id != 0) LastRendered[id] = rendered;

                Logger.LogInfo("[OLIVER] Arabic/decorated S2E BillboardText fixed: " + current);
            }
            catch (Exception ex)
            {
                Logger.LogDebug("[OLIVER] One BillboardText was left unchanged safely: " + ex.Message);
            }
        }

        private static int GetUnityInstanceId(object obj)
        {
            try
            {
                MethodInfo m = obj.GetType().GetMethod("GetInstanceID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null)
                {
                    object value = m.Invoke(obj, null);
                    if (value is int) return (int)value;
                }
            }
            catch { }
            return 0;
        }

        private static void ForceMeshUpdate(object textObject)
        {
            try
            {
                MethodInfo m = textObject.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(x => x.Name == "ForceMeshUpdate" && x.GetParameters().Length == 2 &&
                                         x.GetParameters()[0].ParameterType == typeof(bool) &&
                                         x.GetParameters()[1].ParameterType == typeof(bool));
                if (m != null) m.Invoke(textObject, new object[] { false, false });
            }
            catch { }
        }

        private static bool EnsureFontChain()
        {
            if (_primaryTmpFont != null) return true;
            if (_fontBuildAttempted) return false;
            _fontBuildAttempted = true;

            Type tmpFontType = FindType("TMPro.TMP_FontAsset");
            if (tmpFontType == null)
            {
                _fontBuildAttempted = false;
                return false;
            }

            var candidates = new List<string>();
            AddUnique(candidates, PrimaryFontName == null ? null : PrimaryFontName.Value);
            AddUnique(candidates, "Tahoma");
            AddUnique(candidates, "Segoe UI");
            AddUnique(candidates, "Arial");
            AddUnique(candidates, "Segoe UI Symbol");

            foreach (string name in candidates)
            {
                object asset = CreateTmpFontAsset(tmpFontType, name);
                if (asset == null) continue;
                TmpFonts.Add(asset);
                if (_primaryTmpFont == null) _primaryTmpFont = asset;
            }

            if (_primaryTmpFont == null)
            {
                _fontBuildAttempted = false;
                return false;
            }

            WireFallbacks(_primaryTmpFont, TmpFonts);
            Logger.LogInfo("[OLIVER] Unicode font chain created safely for BillboardText only.");
            return true;
        }

        private static object CreateTmpFontAsset(Type tmpFontType, string fontName)
        {
            try
            {
                Font osFont = Font.CreateDynamicFontFromOSFont(fontName, 64);
                if (osFont == null) return null;

                MethodInfo makeTmp = tmpFontType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CreateFontAsset" && m.GetParameters().Length == 1 &&
                                         m.GetParameters()[0].ParameterType.IsAssignableFrom(osFont.GetType()));
                if (makeTmp == null)
                    makeTmp = tmpFontType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "CreateFontAsset" && m.GetParameters().Length == 1);
                if (makeTmp == null) return null;

                object tmp = makeTmp.Invoke(null, new object[] { osFont });
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
                Logger.LogDebug("[OLIVER] Font candidate " + fontName + " skipped: " + ex.Message);
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
                    try { add.Invoke(list, new object[] { font }); } catch { }
                }
            }
            catch { }
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

        private static bool ContainsArabicOrDecorated(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if ((c >= '\u0600' && c <= '\u06FF') || (c >= '\u0750' && c <= '\u077F') ||
                    (c >= '\u08A0' && c <= '\u08FF') || c > 0x7F)
                    return true;
            }
            return false;
        }

        private static void AddUnique(List<string> list, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!list.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))) list.Add(value);
        }
    }

    public sealed class BillboardDriver : MonoBehaviour
    {
        private float _nextScan;
        private float _startupDelay = 6f;

        public BillboardDriver(IntPtr handle) : base(handle) { }

        private void Update()
        {
            if (_startupDelay > 0f)
            {
                _startupDelay -= Time.unscaledDeltaTime;
                return;
            }

            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 2f;
            Plugin.ScanBillboards();
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
            if (!input.Any(IsArabicBase)) return input;

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
            int i = index - 1;
            if (i >= 0 && Map.ContainsKey(units[i].Base)) return i;
            return -1;
        }

        private static int NextJoinable(List<Unit> units, int index)
        {
            int i = index + 1;
            if (i < units.Count && Map.ContainsKey(units[i].Base)) return i;
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
