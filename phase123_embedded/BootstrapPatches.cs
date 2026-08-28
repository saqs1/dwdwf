using System;
using System.Reflection;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

public static class OliverBootstrap
{
    internal static ManualLogSource LogSource;
    private static bool _started;

    public static void BeginDeferred()
    {
        if (LogSource == null)
            LogSource = BepInEx.Logging.Logger.CreateLogSource("S2E OLIVER Phase123");

        if (_started) return;

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<OliverPassiveScanDriver>();
            ClassInjector.RegisterTypeInIl2Cpp<OliverProfileVisualDriver>();

            GameObject driver = new GameObject("OLIVER_S2E_PassiveVisualDriver");
            UnityEngine.Object.DontDestroyOnLoad(driver);
            driver.hideFlags = HideFlags.HideAndDontSave;
            driver.AddComponent<OliverPassiveScanDriver>();

            _started = true;
            LogSource.LogInfo("[OLIVER] Passive visual driver ACTIVE. Zero Harmony patches on S2E billboard creation.");
            LogSource.LogInfo("[OLIVER] Original S2E creates names/images unchanged; OLIVER only enhances them after they exist.");
            LogSource.LogInfo("[OLIVER] Arabic/Unicode + 130% profile + Auto Fit + PNG frame enabled passively.");
            LogSource.LogInfo("[OLIVER] Original S2E HTTP port remains 55001.");
        }
        catch (Exception ex)
        {
            LogSource.LogError($"[OLIVER] Passive visual driver failed to start: {ex}");
        }
    }

    internal static bool TryInitializeAfterS2E()
    {
        // Kept for plugin compatibility. No method patching is performed.
        return _started;
    }
}

public sealed class OliverPassiveScanDriver : MonoBehaviour
{
    private float _nextScan;

    public OliverPassiveScanDriver(IntPtr pointer) : base(pointer) { }

    private void Update()
    {
        if (Time.unscaledTime < _nextScan) return;
        _nextScan = Time.unscaledTime + 0.50f;
        ScanExactBillboardObjects();
    }

    private static void ScanExactBillboardObjects()
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
            if (all == null) return;

            foreach (var raw in all)
            {
                if (raw == null) continue;

                GameObject go;
                try { go = raw.TryCast<GameObject>(); }
                catch { continue; }

                if (go == null || !go.activeInHierarchy) continue;
                string name = go.name ?? string.Empty;

                if (name == "BillboardText")
                {
                    EnhanceText(go);
                }
                else if (name.StartsWith("BillboardImage_", StringComparison.Ordinal))
                {
                    EnhanceImage(go);
                }
            }
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogDebug($"[OLIVER] Passive scan skipped safely: {ex.Message}");
        }
    }

    private static void EnhanceText(GameObject go)
    {
        try
        {
            var components = go.GetComponents(Il2CppType.Of<Component>());
            if (components == null) return;

            foreach (var rawComponent in components)
            {
                if (rawComponent == null) continue;

                Component component;
                try { component = rawComponent.TryCast<Component>(); }
                catch { continue; }
                if (component == null) continue;

                string il2cppName = component.GetIl2CppType()?.FullName ?? string.Empty;
                if (!il2cppName.StartsWith("TMPro.TextMeshPro", StringComparison.Ordinal)) continue;

                string current = ReadText(component);
                if (string.IsNullOrEmpty(current)) return;

                bool needsUnicode = false;
                foreach (char c in current)
                {
                    if (c > 127) { needsUnicode = true; break; }
                }
                if (!needsUnicode) return;

                OliverUnicodeText.Apply(component, current);
                return;
            }
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogDebug($"[OLIVER] BillboardText enhancement skipped safely: {ex.Message}");
        }
    }

    private static string ReadText(Component component)
    {
        try
        {
            Type wrapperType = FindExactType("TMPro.TextMeshPro");
            if (wrapperType == null) return null;

            object tmp = Activator.CreateInstance(wrapperType, new object[] { ((Il2CppObjectBase)component).Pointer });
            if (tmp == null) return null;

            PropertyInfo textProp = FindProperty(wrapperType, "text");
            return textProp?.GetValue(tmp) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void EnhanceImage(GameObject go)
    {
        try
        {
            // Original S2E is 0.30. OLIVER target = 130% => 0.39.
            go.transform.localScale = Vector3.one * 0.39f;

            if (go.GetComponent<OliverProfileVisualDriver>() == null)
                go.AddComponent<OliverProfileVisualDriver>();
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogDebug($"[OLIVER] BillboardImage enhancement skipped safely: {ex.Message}");
        }
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

    private static PropertyInfo FindProperty(Type type, string name)
    {
        try
        {
            Type current = type;
            while (current != null)
            {
                PropertyInfo prop = current.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (prop != null) return prop;
                current = current.BaseType;
            }
        }
        catch { }
        return null;
    }
}
