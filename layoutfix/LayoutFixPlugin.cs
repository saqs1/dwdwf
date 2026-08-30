using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

[BepInPlugin("oliver.tik.s2e.layoutfix", "OLIVER S2E Layout Fix", "1.0.0")]
[BepInDependency("StreamToEarn_S2E_SupermarketSimulator", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("oliver.tik.s2e.phase123", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class OliverS2ELayoutFixPlugin : BasePlugin
{
    internal static ManualLogSource LogSource;
    private Harmony _harmony;

    public override void Load()
    {
        LogSource = Log;
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<OliverLayoutStabilizer>();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[OLIVER LAYOUT] Stabilizer registration note: {ex.Message}");
        }

        try
        {
            GameObject driverObject = new GameObject("OLIVER_S2E_LayoutStabilizer");
            UnityEngine.Object.DontDestroyOnLoad(driverObject);
            OliverLayoutStabilizer.Instance = driverObject.AddComponent<OliverLayoutStabilizer>();
        }
        catch (Exception ex)
        {
            Log.LogError($"[OLIVER LAYOUT] Could not create stabilizer: {ex}");
            return;
        }

        Type playerUtilities = FindExactType("PlayerUtilities");
        if (playerUtilities == null)
        {
            Log.LogError("[OLIVER LAYOUT] PlayerUtilities was not found; layout fix is inactive.");
            return;
        }

        MethodInfo postfix = typeof(OliverLayoutPatch).GetMethod(
            nameof(OliverLayoutPatch.AfterBillboardChange),
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        if (postfix == null)
        {
            Log.LogError("[OLIVER LAYOUT] Internal postfix was not found.");
            return;
        }

        _harmony = new Harmony("oliver.tik.s2e.layoutfix.harmony");
        int patched = 0;
        foreach (MethodInfo method in playerUtilities.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!string.Equals(method.Name, "AttachBillboardText", StringComparison.Ordinal) &&
                !string.Equals(method.Name, "AttachBillboardImage", StringComparison.Ordinal))
                continue;

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length < 1 || parameters[0].ParameterType != typeof(GameObject)) continue;

            HarmonyMethod hm = new HarmonyMethod(postfix);
            hm.priority = Priority.Last;
            _harmony.Patch(method, postfix: hm);
            patched++;
        }

        if (patched == 0)
        {
            Log.LogError("[OLIVER LAYOUT] No billboard methods were patched.");
            return;
        }

        Log.LogInfo($"[OLIVER LAYOUT] ACTIVE. Patched billboard methods={patched}.");
        Log.LogInfo("[OLIVER LAYOUT] Layout-only: avatar=0.27, separated name/avatar, duplicate BillboardText hidden safely.");
        Log.LogInfo("[OLIVER LAYOUT] No HTTP, TikTok metadata, country detection, Arabic text, spawn logic, or game actions are modified.");
    }

    private static Type FindExactType(string fullName)
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
        return null;
    }
}

internal static class OliverLayoutPatch
{
    internal static void AfterBillboardChange(GameObject __0)
    {
        try
        {
            if (__0 != null) OliverLayoutStabilizer.Queue(__0);
        }
        catch { }
    }
}

public sealed class OliverLayoutStabilizer : MonoBehaviour
{
    internal static OliverLayoutStabilizer Instance;

    private const float AvatarScale = 0.27f;
    private const float NameY = 1.80f;
    private const float AvatarYWithName = 2.10f;
    private const float AvatarYWithoutName = 1.90f;
    private const float TmpNameScale = 0.72f;
    private const float NameImageHeight = 0.067f;
    private const float NameImageMinWidth = 0.10f;
    private const float NameImageMaxWidth = 0.27f;
    private const float StabilizeSeconds = 2.5f;
    private const float TickSeconds = 0.10f;

    private sealed class Entry
    {
        internal GameObject Parent;
        internal float Until;
    }

    private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
    private float _nextTick;

    public OliverLayoutStabilizer(IntPtr pointer) : base(pointer) { }

    internal static void Queue(GameObject parent)
    {
        if (parent == null || Instance == null) return;
        try
        {
            Instance.QueueInternal(parent);
            Instance.Normalize(parent);
        }
        catch { }
    }

    private void QueueInternal(GameObject parent)
    {
        int id = parent.GetInstanceID();
        if (id == 0) return;
        _entries[id] = new Entry
        {
            Parent = parent,
            Until = Time.realtimeSinceStartup + StabilizeSeconds
        };
    }

    private void Update()
    {
        float now = Time.realtimeSinceStartup;
        if (now < _nextTick) return;
        _nextTick = now + TickSeconds;

        if (_entries.Count == 0) return;

        List<int> keys = new List<int>(_entries.Keys);
        foreach (int key in keys)
        {
            if (!_entries.TryGetValue(key, out Entry entry) || entry == null || entry.Parent == null)
            {
                _entries.Remove(key);
                continue;
            }

            try { Normalize(entry.Parent); } catch { }
            if (now >= entry.Until) _entries.Remove(key);
        }
    }

    private void Normalize(GameObject parent)
    {
        if (parent == null || parent.transform == null) return;

        List<Transform> names = new List<Transform>();
        List<Transform> avatars = new List<Transform>();

        Transform root = parent.transform;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (child == null) continue;
            string childName = child.name ?? string.Empty;
            if (string.Equals(childName, "BillboardText", StringComparison.Ordinal)) names.Add(child);
            else if (childName.StartsWith("BillboardImage_", StringComparison.Ordinal)) avatars.Add(child);
        }

        Transform keepName = ChooseName(names);
        if (keepName != null)
        {
            foreach (Transform name in names)
            {
                if (name == null || name == keepName) continue;
                try
                {
                    if (name.gameObject.activeSelf) name.gameObject.SetActive(false);
                }
                catch { }
            }

            try
            {
                Vector3 p = keepName.localPosition;
                keepName.localPosition = new Vector3(0f, NameY, p.z);
                if (IsTextMeshProName(keepName))
                {
                    keepName.localScale = new Vector3(TmpNameScale, TmpNameScale, TmpNameScale);
                }
                else
                {
                    keepName.localScale = CalculateNameImageScale(keepName);
                }
            }
            catch { }
        }

        Transform keepAvatar = ChooseNewestActive(avatars);
        if (keepAvatar != null)
        {
            foreach (Transform avatar in avatars)
            {
                if (avatar == null || avatar == keepAvatar) continue;
                try
                {
                    if (avatar.gameObject.activeSelf) avatar.gameObject.SetActive(false);
                }
                catch { }
            }

            try
            {
                Vector3 p = keepAvatar.localPosition;
                bool hasName = keepName != null && keepName.gameObject.activeSelf;
                keepAvatar.localPosition = new Vector3(0f, hasName ? AvatarYWithName : AvatarYWithoutName, p.z);
                keepAvatar.localScale = Vector3.one * AvatarScale;
            }
            catch { }
        }
    }

    private static Transform ChooseName(List<Transform> names)
    {
        if (names == null || names.Count == 0) return null;

        // The rebuilt S2E can create a texture/quad version of the name so Arabic,
        // emoji and decorative Unicode render correctly. Prefer that object and hide
        // the duplicate TMP object. We never change the actual username text.
        Transform imageName = names
            .Where(x => x != null && x.gameObject.activeSelf && !IsTextMeshProName(x) && HasRenderer(x))
            .OrderByDescending(x => x.gameObject.GetInstanceID())
            .FirstOrDefault();
        if (imageName != null) return imageName;

        Transform tmpName = names
            .Where(x => x != null && x.gameObject.activeSelf && IsTextMeshProName(x))
            .OrderByDescending(x => x.gameObject.GetInstanceID())
            .FirstOrDefault();
        if (tmpName != null) return tmpName;

        return ChooseNewestActive(names);
    }

    private static Transform ChooseNewestActive(List<Transform> items)
    {
        if (items == null || items.Count == 0) return null;
        Transform active = items
            .Where(x => x != null && x.gameObject.activeSelf)
            .OrderByDescending(x => x.gameObject.GetInstanceID())
            .FirstOrDefault();
        if (active != null) return active;
        return items.Where(x => x != null).OrderByDescending(x => x.gameObject.GetInstanceID()).FirstOrDefault();
    }

    private static bool IsTextMeshProName(Transform transform)
    {
        if (transform == null) return false;
        try
        {
            var rawComponents = transform.gameObject.GetComponents(Il2CppType.Of<Component>());
            foreach (var raw in rawComponents)
            {
                Component component = raw.TryCast<Component>();
                if (component == null) continue;
                string fullName = component.GetIl2CppType()?.FullName ?? string.Empty;
                if (fullName.Equals("TMPro.TextMeshPro", StringComparison.Ordinal) ||
                    fullName.StartsWith("TMPro.TextMeshPro", StringComparison.Ordinal))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static bool HasRenderer(Transform transform)
    {
        try { return transform != null && transform.gameObject.GetComponent<Renderer>() != null; }
        catch { return false; }
    }

    private static Vector3 CalculateNameImageScale(Transform transform)
    {
        float width = 0.23f;
        float height = NameImageHeight;
        try
        {
            Renderer renderer = transform.gameObject.GetComponent<Renderer>();
            Texture texture = renderer?.sharedMaterial?.mainTexture;
            if (texture != null && texture.width > 0 && texture.height > 0)
            {
                float aspect = (float)texture.width / texture.height;
                width = NameImageHeight * aspect;
                height = NameImageHeight;

                if (width > NameImageMaxWidth)
                {
                    width = NameImageMaxWidth;
                    height = width / aspect;
                }
                else if (width < NameImageMinWidth)
                {
                    width = NameImageMinWidth;
                    height = width / aspect;
                }

                height = Mathf.Clamp(height, 0.040f, 0.085f);
            }
        }
        catch { }

        return new Vector3(width, height, 1f);
    }
}
