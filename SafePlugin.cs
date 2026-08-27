using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace OliverSupermarketEnhancer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SafePlugin : BasePlugin
    {
        public const string PluginGuid = "oliver.tik.supermarket.enhancer.safe";
        public const string PluginName = "OLIVER Supermarket Enhancer SAFE";
        public const string PluginVersion = "0.1.3-phase1-safe";

        internal static ManualLogSource Logger;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> PrimaryFont;
        internal static object TmpFont;
        internal static bool FontTried;
        internal static readonly Dictionary<int,string> Last = new Dictionary<int,string>();

        public override void Load()
        {
            Logger = Log;
            Enabled = Config.Bind("ArabicNames", "Enabled", true, "Arabic TikTok BillboardText only");
            PrimaryFont = Config.Bind("ArabicNames", "PrimaryFont", "Tahoma", "Windows Arabic font");
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<BillboardDriver>();
                var go = new GameObject("OLIVER_Supermarket_ArabicDriver");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<BillboardDriver>();
                Log.LogInfo("[OLIVER] SAFE Phase1 loaded. No Harmony. Only exact GameObject name BillboardText is scanned.");
            }
            catch (Exception ex)
            {
                Log.LogError("[OLIVER] SAFE driver failed to start: " + ex.Message);
            }
        }

        internal static void Scan()
        {
            if (Enabled == null || !Enabled.Value) return;
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
                foreach (var raw in all)
                {
                    if (raw == null) continue;
                    GameObject go;
                    try { go = raw.TryCast<GameObject>(); }
                    catch { continue; }
                    if (go == null || go.name != "BillboardText" || !go.activeInHierarchy) continue;

                    var comps = go.GetComponents(Il2CppType.Of<Component>());
                    if (comps == null) continue;
                    foreach (var rawComp in comps)
                    {
                        if (rawComp == null) continue;
                        Component c;
                        try { c = rawComp.TryCast<Component>(); }
                        catch { continue; }
                        if (c == null) continue;
                        var t = c.GetType();
                        if (t.FullName == null || !t.FullName.StartsWith("TMPro.", StringComparison.Ordinal)) continue;
                        Process(c);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug("[OLIVER] scan skipped safely: " + ex.Message);
            }
        }

        private static void Process(object textObj)
        {
            try
            {
                var textProp = FindProp(textObj.GetType(), "text");
                if (textProp == null || !textProp.CanRead || !textProp.CanWrite) return;
                var current = textProp.GetValue(textObj, null) as string;
                if (string.IsNullOrEmpty(current) || !HasArabicOrUnicode(current)) return;

                int id = GetId(textObj);
                string prev;
                if (id != 0 && Last.TryGetValue(id, out prev) && prev == current) return;

                EnsureFont();
                if (TmpFont != null)
                {
                    var fontProp = FindProp(textObj.GetType(), "font");
                    if (fontProp != null && fontProp.CanWrite)
                    {
                        try { fontProp.SetValue(textObj, TmpFont, null); } catch { }
                    }
                }

                string shaped = ArabicShaper.Shape(current);
                var rtl = FindProp(textObj.GetType(), "isRightToLeftText");
                if (rtl != null && rtl.CanWrite) { try { rtl.SetValue(textObj, false, null); } catch { } }
                textProp.SetValue(textObj, shaped, null);
                if (id != 0) Last[id] = shaped;
            }
            catch (Exception ex)
            {
                Logger.LogDebug("[OLIVER] BillboardText left unchanged: " + ex.Message);
            }
        }

        private static void EnsureFont()
        {
            if (TmpFont != null || FontTried) return;
            FontTried = true;
            try
            {
                var tmpType = FindType("TMPro.TMP_FontAsset");
                if (tmpType == null) { FontTried = false; return; }
                string[] names = { PrimaryFont == null ? "Tahoma" : PrimaryFont.Value, "Tahoma", "Segoe UI", "Arial", "Segoe UI Symbol" };
                foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var uf = Font.CreateDynamicFontFromOSFont(n, 64);
                        if (uf == null) continue;
                        var m = tmpType.GetMethods(BindingFlags.Public|BindingFlags.Static)
                            .FirstOrDefault(x => x.Name == "CreateFontAsset" && x.GetParameters().Length == 1);
                        if (m == null) continue;
                        var a = m.Invoke(null, new object[]{uf});
                        if (a != null) { TmpFont = a; Logger.LogInfo("[OLIVER] Unicode font ready: " + n); return; }
                    }
                    catch { }
                }
            }
            catch { FontTried = false; }
        }

        private static bool HasArabicOrUnicode(string s)
        {
            foreach (char c in s)
                if ((c >= '\u0600' && c <= '\u06FF') || (c >= '\u0750' && c <= '\u077F') || (c >= '\u08A0' && c <= '\u08FF') || c > 127)
                    return true;
            return false;
        }

        private static int GetId(object o)
        {
            try
            {
                var m = o.GetType().GetMethod("GetInstanceID", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                var v = m == null ? null : m.Invoke(o, null);
                return v is int ? (int)v : 0;
            }
            catch { return 0; }
        }

        private static PropertyInfo FindProp(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                if (p != null) return p;
            }
            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = a.GetType(fullName, false); if (t != null) return t; } catch { }
            }
            return null;
        }
    }

    public sealed class BillboardDriver : MonoBehaviour
    {
        private float nextScan;
        private float delay = 8f;
        public BillboardDriver(IntPtr ptr) : base(ptr) { }
        private void Update()
        {
            if (delay > 0f) { delay -= Time.unscaledDeltaTime; return; }
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 2f;
            SafePlugin.Scan();
        }
    }

    internal static class ArabicShaper
    {
        private struct F { public char I,FN,IN,M; public F(char i,char fn,char ini='\0',char m='\0'){I=i;FN=fn;IN=ini;M=m;} }
        private static readonly Dictionary<char,F> M = new Dictionary<char,F>
        {
            ['\u0621']=new F('\uFE80','\0'), ['\u0622']=new F('\uFE81','\uFE82'), ['\u0623']=new F('\uFE83','\uFE84'), ['\u0624']=new F('\uFE85','\uFE86'), ['\u0625']=new F('\uFE87','\uFE88'), ['\u0626']=new F('\uFE89','\uFE8A','\uFE8B','\uFE8C'),
            ['\u0627']=new F('\uFE8D','\uFE8E'), ['\u0628']=new F('\uFE8F','\uFE90','\uFE91','\uFE92'), ['\u0629']=new F('\uFE93','\uFE94'), ['\u062A']=new F('\uFE95','\uFE96','\uFE97','\uFE98'), ['\u062B']=new F('\uFE99','\uFE9A','\uFE9B','\uFE9C'),
            ['\u062C']=new F('\uFE9D','\uFE9E','\uFE9F','\uFEA0'), ['\u062D']=new F('\uFEA1','\uFEA2','\uFEA3','\uFEA4'), ['\u062E']=new F('\uFEA5','\uFEA6','\uFEA7','\uFEA8'), ['\u062F']=new F('\uFEA9','\uFEAA'), ['\u0630']=new F('\uFEAB','\uFEAC'), ['\u0631']=new F('\uFEAD','\uFEAE'), ['\u0632']=new F('\uFEAF','\uFEB0'),
            ['\u0633']=new F('\uFEB1','\uFEB2','\uFEB3','\uFEB4'), ['\u0634']=new F('\uFEB5','\uFEB6','\uFEB7','\uFEB8'), ['\u0635']=new F('\uFEB9','\uFEBA','\uFEBB','\uFEBC'), ['\u0636']=new F('\uFEBD','\uFEBE','\uFEBF','\uFEC0'), ['\u0637']=new F('\uFEC1','\uFEC2','\uFEC3','\uFEC4'), ['\u0638']=new F('\uFEC5','\uFEC6','\uFEC7','\uFEC8'),
            ['\u0639']=new F('\uFEC9','\uFECA','\uFECB','\uFECC'), ['\u063A']=new F('\uFECD','\uFECE','\uFECF','\uFED0'), ['\u0641']=new F('\uFED1','\uFED2','\uFED3','\uFED4'), ['\u0642']=new F('\uFED5','\uFED6','\uFED7','\uFED8'), ['\u0643']=new F('\uFED9','\uFEDA','\uFEDB','\uFEDC'), ['\u0644']=new F('\uFEDD','\uFEDE','\uFEDF','\uFEE0'),
            ['\u0645']=new F('\uFEE1','\uFEE2','\uFEE3','\uFEE4'), ['\u0646']=new F('\uFEE5','\uFEE6','\uFEE7','\uFEE8'), ['\u0647']=new F('\uFEE9','\uFEEA','\uFEEB','\uFEEC'), ['\u0648']=new F('\uFEED','\uFEEE'), ['\u0649']=new F('\uFEEF','\uFEF0'), ['\u064A']=new F('\uFEF1','\uFEF2','\uFEF3','\uFEF4')
        };

        public static string Shape(string input)
        {
            if (string.IsNullOrEmpty(input) || !input.Any(c => M.ContainsKey(c))) return input;
            var chars = input.ToCharArray();
            for (int i=0;i<chars.Length;i++)
            {
                F f; if (!M.TryGetValue(chars[i], out f)) continue;
                bool prev = i>0 && CanNext(chars[i-1]) && f.FN!='\0';
                bool next = i+1<chars.Length && f.IN!='\0' && CanPrev(chars[i+1]);
                chars[i] = prev && next && f.M!='\0' ? f.M : prev && f.FN!='\0' ? f.FN : next && f.IN!='\0' ? f.IN : f.I;
            }
            Array.Reverse(chars);
            return new string(chars);
        }
        private static bool CanNext(char c){ F f; return M.TryGetValue(c,out f) && f.IN!='\0'; }
        private static bool CanPrev(char c){ F f; return M.TryGetValue(c,out f) && f.FN!='\0'; }
    }
}
