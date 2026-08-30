using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityEngine;

public sealed class OliverProfileVisualDriver : MonoBehaviour
{
    private bool _done;
    private float _nextTry;

    public OliverProfileVisualDriver(IntPtr pointer) : base(pointer) { }

    private void Update()
    {
        if (_done || Time.realtimeSinceStartup < _nextTry) return;
        _nextTry = Time.realtimeSinceStartup + 0.25f;

        try
        {
            Renderer renderer = GetComponent<Renderer>();
            if (renderer == null || renderer.material == null || renderer.material.mainTexture == null) return;

            // Phase 2 Auto Fit: center-crop the texture without stretching the profile image.
            Texture texture = renderer.material.mainTexture;
            int width = texture.width;
            int height = texture.height;
            if (width > 0 && height > 0)
            {
                if (width > height)
                {
                    float sx = (float)height / width;
                    renderer.material.mainTextureScale = new Vector2(sx, 1f);
                    renderer.material.mainTextureOffset = new Vector2((1f - sx) * 0.5f, 0f);
                }
                else if (height > width)
                {
                    float sy = (float)width / height;
                    renderer.material.mainTextureScale = new Vector2(1f, sy);
                    renderer.material.mainTextureOffset = new Vector2(0f, (1f - sy) * 0.5f);
                }
                else
                {
                    renderer.material.mainTextureScale = Vector2.one;
                    renderer.material.mainTextureOffset = Vector2.zero;
                }
            }

            // Keep the proven royal frame untouched.
            OliverFrameFactory.AttachRoyalFrame(transform);

            // Country overlay is additive and appears only when the original TikTok
            // event supplied a verified ISO-3166 alpha-2 code AND a matching PNG exists.
            string country = OliverCountryContext.ResolveImage(gameObject.GetInstanceID());
            OliverFrameFactory.AttachCountryFrame(transform, country);

            _done = true;
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogDebug($"[OLIVER] Auto Fit/frame retry: {ex.Message}");
        }
    }
}

internal static class OliverFrameFactory
{
    private static Texture2D _royalTexture;
    private static Material _royalMaterial;
    private static bool _royalLoadAttempted;

    private static readonly Dictionary<string, Material> CountryMaterials =
        new Dictionary<string, Material>(StringComparer.Ordinal);
    private static readonly HashSet<string> CountryLoadAttempted =
        new HashSet<string>(StringComparer.Ordinal);

    internal static void AttachRoyalFrame(Transform imageTransform)
    {
        if (imageTransform == null || imageTransform.Find("OLIVER_ProfileFrame") != null) return;

        EnsureRoyalFrameLoaded();
        if (_royalTexture == null || _royalMaterial == null) return;

        GameObject frame = GameObject.CreatePrimitive(PrimitiveType.Quad);
        frame.name = "OLIVER_ProfileFrame";
        frame.transform.SetParent(imageTransform, false);
        frame.transform.localPosition = new Vector3(0f, 0f, -0.012f);
        frame.transform.localRotation = Quaternion.identity;
        frame.transform.localScale = Vector3.one * 1.28f;

        Renderer renderer = frame.GetComponent<Renderer>();
        if (renderer != null) renderer.material = _royalMaterial;
    }

    // Backward-compatible name used by older callers/builds.
    internal static void AttachFrame(Transform imageTransform)
    {
        AttachRoyalFrame(imageTransform);
    }

    internal static void AttachCountryFrame(Transform imageTransform, string countryCode)
    {
        if (imageTransform == null) return;
        string code = OliverCountryContext.NormalizeVerifiedCountry(countryCode);
        if (string.IsNullOrWhiteSpace(code)) return; // UNKNOWN => hidden, never guessed.
        if (imageTransform.Find("OLIVER_CountryFrame") != null) return;

        Material material = EnsureCountryMaterial(code);
        if (material == null) return;

        GameObject frame = GameObject.CreatePrimitive(PrimitiveType.Quad);
        frame.name = "OLIVER_CountryFrame";
        frame.transform.SetParent(imageTransform, false);
        // Slightly farther from the profile plane than the royal frame so both remain visible.
        frame.transform.localPosition = new Vector3(0f, 0f, -0.024f);
        frame.transform.localRotation = Quaternion.identity;
        frame.transform.localScale = Vector3.one * 1.40f;

        Renderer renderer = frame.GetComponent<Renderer>();
        if (renderer != null) renderer.material = material;

        OliverBootstrap.LogSource?.LogInfo($"[OLIVER COUNTRY] frame={code}.png attached=YES verified=YES");
    }

    private static void EnsureRoyalFrameLoaded()
    {
        if (_royalLoadAttempted) return;
        _royalLoadAttempted = true;

        try
        {
            string path = Path.Combine(Paths.PluginPath, "Oliver_Royal_Frame.png");
            if (!File.Exists(path))
            {
                OliverBootstrap.LogSource?.LogWarning($"[OLIVER] Frame PNG not found: {path}");
                return;
            }

            _royalMaterial = LoadTransparentMaterial(path, out _royalTexture);
            if (_royalMaterial != null)
                OliverBootstrap.LogSource?.LogInfo("[OLIVER] Royal PNG frame loaded.");
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogWarning($"[OLIVER] Frame PNG could not load: {ex.Message}");
        }
    }

    private static Material EnsureCountryMaterial(string code)
    {
        if (CountryMaterials.TryGetValue(code, out Material cached)) return cached;
        if (CountryLoadAttempted.Contains(code)) return null;
        CountryLoadAttempted.Add(code);

        try
        {
            string path = FindCountryFramePath(code);
            if (string.IsNullOrWhiteSpace(path))
            {
                OliverBootstrap.LogSource?.LogWarning(
                    $"[OLIVER COUNTRY] country={code} verified=YES but frame PNG was not found; country overlay hidden.");
                return null;
            }

            Material material = LoadTransparentMaterial(path, out Texture2D texture);
            if (material == null || texture == null) return null;

            CountryMaterials[code] = material;
            OliverBootstrap.LogSource?.LogInfo($"[OLIVER COUNTRY] frame={Path.GetFileName(path)} loaded=YES source=TIKTOK_EVENT");
            return material;
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogWarning($"[OLIVER COUNTRY] Could not load {code}.png: {ex.Message}");
            return null;
        }
    }

    private static string FindCountryFramePath(string code)
    {
        string[] folders =
        {
            Path.Combine(Paths.PluginPath, "OLIVER_Country_Frames"),
            Path.Combine(Paths.PluginPath, "CountryFrames"),
            Path.Combine(Paths.PluginPath, "country_frames"),
            Path.Combine(Paths.PluginPath, "flags"),
            Paths.PluginPath
        };

        string[] fileNames = { code + ".png", code.ToLowerInvariant() + ".png" };
        foreach (string folder in folders)
        {
            foreach (string fileName in fileNames)
            {
                string path = Path.Combine(folder, fileName);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    private static Material LoadTransparentMaterial(string path, out Texture2D texture)
    {
        texture = null;
        using Image<Rgba32> image = Image.Load<Rgba32>(path);
        int width = image.Width;
        int height = image.Height;
        Rgba32[] pixels = new Rgba32[width * height];
        image.CopyPixelDataTo(pixels);

        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int src = y * width + x;
                int dst = ((height - 1 - y) * width + x) * 4;
                Rgba32 pixel = pixels[src];
                rgba[dst] = pixel.R;
                rgba[dst + 1] = pixel.G;
                rgba[dst + 2] = pixel.B;
                rgba[dst + 3] = pixel.A;
            }
        }

        texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        var il2cppBytes = new Il2CppStructArray<byte>(rgba);
        texture.LoadRawTextureData(il2cppBytes);
        texture.Apply(false, true);
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        Shader shader = Shader.Find("Sprites/Default") ??
                        Shader.Find("Unlit/Transparent") ??
                        Shader.Find("Unlit/Texture");
        if (shader == null)
        {
            OliverBootstrap.LogSource?.LogWarning("[OLIVER] No transparent shader was found for PNG overlay.");
            texture = null;
            return null;
        }

        Material material = new Material(shader);
        material.mainTexture = texture;
        material.color = UnityEngine.Color.white;
        return material;
    }
}
