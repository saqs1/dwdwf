using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class OliverRequestMetadataCompat
{
    private static readonly string[] NameAliases =
    {
        "name", "nickname", "userName", "username", "displayName", "uniqueId", "unique_id", "userNameText"
    };

    private static readonly string[] AvatarAliases =
    {
        "avatarUrl", "avatarURL", "profilePictureUrl", "profileImageUrl", "avatar", "imageUrl", "pictureUrl",
        "photoUrl", "userAvatar", "profilePicture", "profile_picture_url"
    };

    private static readonly string[] HeadAliases =
    {
        "headAvatarUrl", "headUrl", "headAvatar", "headImageUrl"
    };

    private static int _diagCount;

    internal static void BeforeHandleRequest(string __0, ref string __1)
    {
        string path = __0 ?? string.Empty;
        string body = __1 ?? string.Empty;
        if (!IsSpawnPath(path) || string.IsNullOrWhiteSpace(body)) return;

        try
        {
            JsonNode root = ParseMaybeNested(body);
            if (root == null) return;

            JsonObject canonical = root as JsonObject;
            if (canonical == null)
            {
                // Original S2E expects an object. If metadata is buried in an array/string,
                // create a small object only when we can actually recover useful fields.
                string recoveredName = FindAlias(root, NameAliases, 0);
                string recoveredAvatar = FindAlias(root, AvatarAliases, 0);
                string recoveredHead = FindAlias(root, HeadAliases, 0);
                if (!Has(recoveredName) && !Has(recoveredAvatar) && !Has(recoveredHead)) return;
                canonical = new JsonObject();
                if (Has(recoveredName)) canonical["name"] = recoveredName;
                if (Has(recoveredAvatar)) canonical["avatarUrl"] = recoveredAvatar;
                if (Has(recoveredHead)) canonical["headAvatarUrl"] = recoveredHead;
            }
            else
            {
                PutIfMissing(canonical, "name", FindAlias(root, NameAliases, 0));
                PutIfMissing(canonical, "avatarUrl", FindAlias(root, AvatarAliases, 0));
                PutIfMissing(canonical, "headAvatarUrl", FindAlias(root, HeadAliases, 0));
            }

            string name = Get(canonical, "name");
            string avatar = Get(canonical, "avatarUrl");
            string head = Get(canonical, "headAvatarUrl");

            // Never replace a valid avatar with head image. The original S2E already
            // gives avatarUrl priority and clears headAvatarUrl when avatar exists.
            __1 = canonical.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

            if (_diagCount < 4)
            {
                _diagCount++;
                OliverBootstrap.LogSource?.LogInfo(
                    $"[OLIVER META] Spawn metadata normalized: name={(Has(name) ? "YES" : "NO")}, avatar={(Has(avatar) ? "YES" : "NO")}, head={(Has(head) ? "YES" : "NO")}.");
            }
        }
        catch (Exception ex)
        {
            if (_diagCount < 4)
            {
                _diagCount++;
                OliverBootstrap.LogSource?.LogWarning($"[OLIVER META] Metadata normalization skipped safely: {ex.Message}");
            }
        }
    }

    private static bool IsSpawnPath(string path)
    {
        string p = (path ?? string.Empty).ToLowerInvariant();
        return p.Contains("spawncustomer") || p.Contains("spawnshoplifter") || p.Contains("spawnnpc");
    }

    private static JsonNode ParseMaybeNested(string text)
    {
        if (!Has(text)) return null;
        string current = text.Trim();
        for (int i = 0; i < 4; i++)
        {
            try
            {
                JsonNode node = JsonNode.Parse(current);
                if (node is JsonValue value && value.TryGetValue<string>(out string nested) && LooksJson(nested))
                {
                    current = nested.Trim();
                    continue;
                }
                return node;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private static void PutIfMissing(JsonObject obj, string key, string value)
    {
        if (!Has(value) || Has(Get(obj, key))) return;
        obj[key] = value;
    }

    private static string Get(JsonObject obj, string key)
    {
        if (obj == null || !obj.TryGetPropertyValue(key, out JsonNode node) || node == null) return null;
        return NodeString(node);
    }

    private static string FindAlias(JsonNode node, string[] aliases, int depth)
    {
        if (node == null || depth > 12) return null;

        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode> kv in obj)
            {
                if (!aliases.Any(a => SameKey(a, kv.Key))) continue;
                string value = Scalar(kv.Value);
                if (Has(value)) return value;
            }
            foreach (KeyValuePair<string, JsonNode> kv in obj)
            {
                string value = FindAlias(kv.Value, aliases, depth + 1);
                if (Has(value)) return value;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode child in arr)
            {
                string value = FindAlias(child, aliases, depth + 1);
                if (Has(value)) return value;
            }
        }
        else if (node is JsonValue)
        {
            string text = NodeString(node);
            if (LooksJson(text))
            {
                JsonNode nested = ParseMaybeNested(text);
                return FindAlias(nested, aliases, depth + 1);
            }
        }

        return null;
    }

    private static string Scalar(JsonNode node)
    {
        if (node == null) return null;
        if (node is JsonValue) return NodeString(node);
        return null;
    }

    private static string NodeString(JsonNode node)
    {
        if (node == null) return null;
        try
        {
            if (node is JsonValue v && v.TryGetValue<string>(out string s)) return s;
            return node.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool SameKey(string a, string b)
    {
        if (a == null || b == null) return false;
        string x = new string(a.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        string y = new string(b.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return x == y;
    }

    private static bool LooksJson(string s)
    {
        if (!Has(s)) return false;
        string t = s.Trim();
        return (t.StartsWith("{") && t.EndsWith("}")) || (t.StartsWith("[") && t.EndsWith("]"));
    }

    private static bool Has(string s) => !string.IsNullOrWhiteSpace(s);
}
