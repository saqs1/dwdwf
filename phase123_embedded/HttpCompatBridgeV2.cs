using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal static class OliverHttpCompatBridge
{
    private const string PublicPrefix = "http://127.0.0.1:55001/";
    private const string InternalBase = "http://127.0.0.1:55101";

    private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string[] NameAliases = { "name", "nickname", "userName", "username", "displayName", "uniqueId", "unique_id", "userNameText" };
    private static readonly string[] AvatarAliases = { "avatarUrl", "avatarURL", "profilePictureUrl", "profileImageUrl", "avatar", "imageUrl", "pictureUrl", "photoUrl", "userAvatar", "profilePicture", "profile_picture_url" };
    private static readonly string[] HeadAliases = { "headAvatarUrl", "headUrl", "headAvatar", "headImageUrl" };
    private static readonly string[] CountAliases = { "count", "repeat", "repeatCount", "repetitions", "repetition", "quantity", "amount" };

    private static HttpListener _listener;
    private static CancellationTokenSource _cts;
    private static bool _started;
    private static int _diagCount;

    internal static void Start()
    {
        if (_started) return;
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(PublicPrefix);
            _listener.Start();
            _started = true;
            OliverBootstrap.LogSource?.LogInfo("[OLIVER HTTP] v0.2.0 CATCH-ALL parser ACTIVE on 55001 -> S2E 55101.");
            OliverBootstrap.LogSource?.LogInfo("[OLIVER HTTP] Parses object/array JSON, nested/stringified JSON, form, multipart, headers and embedded command strings.");
            _ = Task.Run(() => ListenLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogError($"[OLIVER HTTP] Could not bind public port 55001: {ex}");
        }
    }

    internal static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        _cts = null;
        _started = false;
    }

    private static async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                OliverBootstrap.LogSource?.LogWarning($"[OLIVER HTTP] Listener retry: {ex.Message}");
                await Task.Delay(100, token).ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(() => Handle(ctx), token);
        }
    }

    private static async Task Handle(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse resp = ctx.Response;
        try
        {
            resp.Headers["Access-Control-Allow-Origin"] = "*";
            resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            resp.Headers["Access-Control-Allow-Headers"] = "Content-Type, Superdupertoken, *";
            if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 200;
                resp.Close();
                return;
            }

            string rawBody = string.Empty;
            if (req.HasEntityBody)
            {
                using StreamReader sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                rawBody = await sr.ReadToEndAsync().ConfigureAwait(false);
            }

            string rawUrl = req.RawUrl ?? req.Url?.PathAndQuery ?? string.Empty;
            JsonNode root = ParseAnyJson(rawBody);
            JsonObject canonical = root as JsonObject ?? new JsonObject();

            MergeQuery(req, rawUrl, canonical);
            MergeHeaders(req, canonical);
            MergeFormAndMultipart(rawBody, req.ContentType, canonical);
            MergeAliasesFromTree(root, canonical);
            MergeKeyValuePairs(root, canonical, 0);
            MergeLooseText(rawBody, canonical);

            string embeddedCommand = FindCommandAnywhere(root, rawBody, 0);
            if (Has(embeddedCommand)) MergeRawParams(embeddedCommand, canonical);

            Normalize(canonical);

            string route = ResolveRoute(req, embeddedCommand, rawBody);
            string name = Get(canonical, "name");
            string avatar = Get(canonical, "avatarUrl");
            string head = Get(canonical, "headAvatarUrl");

            if (IsSpawn(route))
            {
                OliverBootstrap.LogSource?.LogInfo(
                    $"[OLIVER HTTP] {req.HttpMethod} {route}: name={(Has(name) ? "YES" : "NO")}, avatar={(Has(avatar) ? "YES" : "NO")}, head={(Has(head) ? "YES" : "NO")}, rawQuery={(rawUrl.Contains("?") ? "YES" : "NO")}, body={(Has(rawBody) ? "YES" : "NO")}");

                if (!Has(name) && !Has(avatar) && Interlocked.Increment(ref _diagCount) <= 3)
                {
                    string headers = string.Join(",", req.Headers.AllKeys.Where(x => Has(x)).Take(30));
                    OliverBootstrap.LogSource?.LogWarning(
                        $"[OLIVER HTTP DIAG] metadata absent. contentType='{req.ContentType ?? ""}', bodyLength={rawBody.Length}, root={Kind(root)}, shape={Shape(root, 0)}, headerNames=[{headers}]");
                }
            }

            string json = canonical.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            HttpResponseMessage inner = null;
            Exception last = null;
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    using HttpRequestMessage fwd = new HttpRequestMessage(HttpMethod.Post, InternalBase + route);
                    fwd.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    string token = req.Headers["Superdupertoken"];
                    if (Has(token)) fwd.Headers.TryAddWithoutValidation("Superdupertoken", token);
                    inner = await Client.SendAsync(fwd).ConfigureAwait(false);
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    await Task.Delay(150).ConfigureAwait(false);
                }
            }

            if (inner == null) throw new InvalidOperationException("Internal S2E listener on 55101 did not respond.", last);

            string result = await inner.Content.ReadAsStringAsync().ConfigureAwait(false);
            byte[] bytes = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(result) ? "OTVET" : result);
            resp.StatusCode = (int)inner.StatusCode;
            resp.ContentType = "text/plain; charset=utf-8";
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            resp.Close();
            inner.Dispose();
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogError($"[OLIVER HTTP] Request forwarding failed: {ex}");
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes("OLIVER_HTTP_ERROR");
                resp.StatusCode = 502;
                resp.ContentLength64 = bytes.Length;
                await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                resp.Close();
            }
            catch { }
        }
    }

    private static JsonNode ParseAnyJson(string text)
    {
        if (!Has(text)) return null;
        string current = text.Trim();
        for (int i = 0; i < 4; i++)
        {
            try
            {
                JsonNode n = JsonNode.Parse(current);
                if (n is JsonValue v && v.TryGetValue<string>(out string nested) && LooksJson(nested))
                {
                    current = nested;
                    continue;
                }
                return n;
            }
            catch { }
            string decoded = Decode(current);
            if (string.Equals(decoded, current, StringComparison.Ordinal)) break;
            current = decoded;
        }
        return null;
    }

    private static void MergeQuery(HttpListenerRequest req, string rawUrl, JsonObject dst)
    {
        try
        {
            foreach (string key in req.QueryString.AllKeys)
            {
                if (!Has(key)) continue;
                string value = req.QueryString[key];
                if (Has(value)) dst[key] = value;
            }
        }
        catch { }
        MergeRawParams(rawUrl, dst);
    }

    private static void MergeHeaders(HttpListenerRequest req, JsonObject dst)
    {
        try
        {
            foreach (string key in req.Headers.AllKeys)
            {
                if (!Has(key)) continue;
                string value = req.Headers[key];
                if (!Has(value)) continue;
                PutAlias(key, value, dst);
            }
        }
        catch { }
    }

    private static void MergeFormAndMultipart(string text, string contentType, JsonObject dst)
    {
        if (!Has(text)) return;
        string ct = contentType ?? string.Empty;

        if (ct.IndexOf("multipart/form-data", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Match bm = Regex.Match(ct, "boundary=(?:\"(?<b>[^\"]+)\"|(?<b>[^;]+))", RegexOptions.IgnoreCase);
            if (!bm.Success) return;
            string boundary = bm.Groups["b"].Value.Trim();
            foreach (string part in text.Split(new[] { "--" + boundary }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match nm = Regex.Match(part, "name=\"(?<n>[^\"]+)\"", RegexOptions.IgnoreCase);
                if (!nm.Success) continue;
                int p = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                int skip = 4;
                if (p < 0) { p = part.IndexOf("\n\n", StringComparison.Ordinal); skip = 2; }
                if (p < 0) continue;
                string value = part.Substring(p + skip).Trim().TrimEnd('-').Trim();
                PutAlias(nm.Groups["n"].Value, value, dst);
            }
            return;
        }

        bool form = ct.IndexOf("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0;
        string t = text.TrimStart();
        if (form || (!t.StartsWith("{") && !t.StartsWith("[") && text.Contains("=")))
            MergeRawParams("?" + text, dst);
    }

    private static void MergeAliasesFromTree(JsonNode root, JsonObject dst)
    {
        CopyFound(root, dst, "name", NameAliases);
        CopyFound(root, dst, "avatarUrl", AvatarAliases);
        CopyFound(root, dst, "headAvatarUrl", HeadAliases);
        CopyFound(root, dst, "count", CountAliases);
    }

    private static void CopyFound(JsonNode root, JsonObject dst, string canonical, string[] aliases)
    {
        if (Has(Get(dst, canonical))) return;
        string value = FindAlias(root, aliases, 0);
        if (Has(value)) dst[canonical] = value;
    }

    private static string FindAlias(JsonNode node, string[] aliases, int depth)
    {
        if (node == null || depth > 12) return null;
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (!aliases.Any(a => SameKey(a, kv.Key))) continue;
                string s = ScalarOrUrl(kv.Value, depth + 1);
                if (Has(s)) return s;
            }
            foreach (var kv in obj)
            {
                string s = FindAlias(kv.Value, aliases, depth + 1);
                if (Has(s)) return s;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode child in arr)
            {
                string s = FindAlias(child, aliases, depth + 1);
                if (Has(s)) return s;
            }
        }
        else if (node is JsonValue)
        {
            string s = Decode(NodeString(node));
            if (LooksJson(s)) return FindAlias(ParseAnyJson(s), aliases, depth + 1);
        }
        return null;
    }

    private static string ScalarOrUrl(JsonNode node, int depth)
    {
        if (node == null) return null;
        if (node is JsonValue)
        {
            string s = Decode(NodeString(node));
            if (LooksJson(s))
            {
                string u = FirstUrl(ParseAnyJson(s), depth + 1);
                if (Has(u)) return u;
            }
            return s;
        }
        return FirstUrl(node, depth + 1);
    }

    private static string FirstUrl(JsonNode node, int depth)
    {
        if (node == null || depth > 14) return null;
        if (node is JsonValue)
        {
            string s = Decode(NodeString(node));
            if (Uri.TryCreate(s, UriKind.Absolute, out Uri u) && (u.Scheme == "http" || u.Scheme == "https")) return s;
            if (LooksJson(s)) return FirstUrl(ParseAnyJson(s), depth + 1);
            return null;
        }
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                string s = FirstUrl(kv.Value, depth + 1);
                if (Has(s)) return s;
            }
        }
        if (node is JsonArray arr)
        {
            foreach (JsonNode child in arr)
            {
                string s = FirstUrl(child, depth + 1);
                if (Has(s)) return s;
            }
        }
        return null;
    }

    private static void MergeKeyValuePairs(JsonNode node, JsonObject dst, int depth)
    {
        if (node == null || depth > 12) return;
        if (node is JsonObject obj)
        {
            string k = Direct(obj, new[] { "key", "param", "parameter", "field", "property", "propertyName" });
            string v = Direct(obj, new[] { "value", "val", "text", "content" });
            if (Has(k) && Has(v)) PutAlias(k, v, dst);
            foreach (var kv in obj) MergeKeyValuePairs(kv.Value, dst, depth + 1);
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode child in arr) MergeKeyValuePairs(child, dst, depth + 1);
        }
        else if (node is JsonValue)
        {
            string s = Decode(NodeString(node));
            if (LooksJson(s)) MergeKeyValuePairs(ParseAnyJson(s), dst, depth + 1);
        }
    }

    private static string Direct(JsonObject obj, string[] keys)
    {
        foreach (var kv in obj)
        {
            if (!keys.Any(k => SameKey(k, kv.Key))) continue;
            string s = NodeString(kv.Value);
            if (Has(s)) return s;
        }
        return null;
    }

    private static string FindCommandAnywhere(JsonNode node, string rawBody, int depth)
    {
        if (depth > 14) return null;
        if (node is JsonValue)
        {
            string s = DecodeMany(NodeString(node), 3);
            if (LooksCommand(s)) return s;
            if (LooksJson(s))
            {
                string x = FindCommandAnywhere(ParseAnyJson(s), null, depth + 1);
                if (Has(x)) return x;
            }
        }
        else if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                string x = FindCommandAnywhere(kv.Value, null, depth + 1);
                if (Has(x)) return x;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode child in arr)
            {
                string x = FindCommandAnywhere(child, null, depth + 1);
                if (Has(x)) return x;
            }
        }

        if (depth == 0 && Has(rawBody))
        {
            string s = DecodeMany(rawBody.Trim().Trim('"'), 3);
            if (LooksCommand(s)) return s;
            Match m = Regex.Match(s, "(?:spawncustomer|spawnshoplifter|spawnnpc)[^\\r\\n\\\"']*", RegexOptions.IgnoreCase);
            if (m.Success) return m.Value;
        }
        return null;
    }

    private static void MergeLooseText(string text, JsonObject dst)
    {
        if (!Has(text)) return;
        LooseJson(text, NameAliases, "name", dst);
        LooseJson(text, AvatarAliases, "avatarUrl", dst);
        LooseJson(text, HeadAliases, "headAvatarUrl", dst);
        string decoded = DecodeMany(text, 3);
        if (!string.Equals(decoded, text, StringComparison.Ordinal))
        {
            LooseJson(decoded, NameAliases, "name", dst);
            LooseJson(decoded, AvatarAliases, "avatarUrl", dst);
            LooseJson(decoded, HeadAliases, "headAvatarUrl", dst);
            if (decoded.Contains("=")) MergeRawParams("?" + decoded, dst);
        }
    }

    private static void LooseJson(string text, string[] aliases, string canonical, JsonObject dst)
    {
        if (Has(Get(dst, canonical))) return;
        foreach (string a in aliases)
        {
            string pattern = "[\\\"']" + Regex.Escape(a) + "[\\\"']\\s*:\\s*[\\\"'](?<v>(?:\\\\.|[^\\\"'])*)[\\\"']";
            Match m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            string value = DecodeMany(m.Groups["v"].Value.Replace("\\/", "/").Replace("\\u0026", "&"), 2);
            if (Has(value)) { dst[canonical] = value; return; }
        }
    }

    private static void MergeRawParams(string source, JsonObject dst)
    {
        if (!Has(source)) return;
        source = DecodeMany(source, 2);
        int q = source.IndexOf('?');
        string query = q >= 0 ? source.Substring(q + 1) : source;
        if (!Has(query)) return;

        foreach (string pair in query.Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string key = DecodeMany(pair.Substring(0, eq), 2);
            string value = DecodeMany(pair.Substring(eq + 1), 2);
            PutAlias(key, value, dst);
            if (Has(key) && Has(value) && !Has(Get(dst, key))) dst[key] = value;
        }

        string name = RawValue(query, NameAliases, AvatarAliases.Concat(HeadAliases).Concat(CountAliases).ToArray());
        string avatar = RawValue(query, AvatarAliases, HeadAliases.Concat(NameAliases).Concat(CountAliases).ToArray());
        string head = RawValue(query, HeadAliases, NameAliases.Concat(AvatarAliases).Concat(CountAliases).ToArray());
        string count = RawValue(query, CountAliases, NameAliases.Concat(AvatarAliases).Concat(HeadAliases).ToArray());
        if (Has(name)) dst["name"] = name;
        if (Has(avatar)) dst["avatarUrl"] = avatar;
        if (Has(head)) dst["headAvatarUrl"] = head;
        if (Has(count)) dst["count"] = count;
    }

    private static string RawValue(string query, string[] aliases, string[] stops)
    {
        foreach (string a in aliases)
        {
            Match m = Regex.Match(query, "(?:^|&)" + Regex.Escape(a) + "=", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            int start = m.Index + m.Length;
            int end = query.Length;
            foreach (string stop in stops.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Match sm = Regex.Match(query.Substring(start), "&" + Regex.Escape(stop) + "=", RegexOptions.IgnoreCase);
                if (sm.Success) end = Math.Min(end, start + sm.Index);
            }
            string value = DecodeMany(query.Substring(start, Math.Max(0, end - start)), 2);
            if (Has(value)) return value;
        }
        return null;
    }

    private static void PutAlias(string key, string value, JsonObject dst)
    {
        if (!Has(key) || !Has(value)) return;
        string nk = NormKey(key);
        if (MatchAlias(nk, NameAliases)) Put(dst, "name", value);
        else if (MatchAlias(nk, AvatarAliases)) Put(dst, "avatarUrl", value);
        else if (MatchAlias(nk, HeadAliases)) Put(dst, "headAvatarUrl", value);
        else if (MatchAlias(nk, CountAliases)) Put(dst, "count", value);
        else if ((nk == "command" || nk == "cmd" || nk == "action" || nk == "payload" || nk == "data") && LooksCommand(value)) MergeRawParams(value, dst);
    }

    private static void Normalize(JsonObject dst)
    {
        Canon(dst, "name", NameAliases);
        Canon(dst, "avatarUrl", AvatarAliases);
        Canon(dst, "headAvatarUrl", HeadAliases);
        Canon(dst, "count", CountAliases);
    }

    private static void Canon(JsonObject dst, string canonical, string[] aliases)
    {
        if (Has(Get(dst, canonical))) return;
        foreach (string a in aliases)
        {
            string v = Get(dst, a);
            if (Has(v)) { dst[canonical] = v; return; }
        }
    }

    private static string ResolveRoute(HttpListenerRequest req, string embeddedCommand, string rawBody)
    {
        string r = req.Url?.AbsolutePath ?? "/";
        if (IsSpawn(r)) return NormalizeRoute(r);
        foreach (string s in new[] { embeddedCommand, rawBody })
        {
            string x = ExtractRoute(s);
            if (IsSpawn(x)) return NormalizeRoute(x);
        }
        return NormalizeRoute(r);
    }

    private static string ExtractRoute(string value)
    {
        if (!Has(value)) return null;
        string s = DecodeMany(value, 2);
        Match m = Regex.Match(s, "(?:^|/)(spawncustomer|spawnshoplifter|spawnnpc)(?:\\?|$)", RegexOptions.IgnoreCase);
        if (m.Success) return "/" + m.Groups[1].Value;
        return null;
    }

    private static string NormalizeRoute(string r)
    {
        if (!Has(r)) return "/";
        int q = r.IndexOf('?');
        if (q >= 0) r = r.Substring(0, q);
        if (!r.StartsWith("/")) r = "/" + r;
        return r;
    }

    private static bool IsSpawn(string r)
    {
        if (!Has(r)) return false;
        string x = NormalizeRoute(r);
        return x.EndsWith("/spawncustomer", StringComparison.OrdinalIgnoreCase) ||
               x.EndsWith("/spawnshoplifter", StringComparison.OrdinalIgnoreCase) ||
               x.EndsWith("/spawnnpc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksCommand(string s)
    {
        if (!Has(s)) return false;
        string x = DecodeMany(s, 2).ToLowerInvariant();
        return x.Contains("spawncustomer") || x.Contains("spawnshoplifter") || x.Contains("spawnnpc");
    }

    private static bool LooksJson(string s)
    {
        if (!Has(s)) return false;
        string x = s.Trim();
        return (x.StartsWith("{") && x.EndsWith("}")) || (x.StartsWith("[") && x.EndsWith("]"));
    }

    private static string Get(JsonObject obj, string key)
    {
        foreach (var kv in obj)
            if (SameKey(kv.Key, key)) return NodeString(kv.Value);
        return null;
    }

    private static string NodeString(JsonNode node)
    {
        try
        {
            if (node == null) return null;
            if (node is JsonValue v && v.TryGetValue<string>(out string s)) return s;
            return node.ToString();
        }
        catch { return null; }
    }

    private static void Put(JsonObject dst, string key, string value)
    {
        if (Has(value) && !Has(Get(dst, key))) dst[key] = DecodeMany(value, 2);
    }

    private static string Decode(string s)
    {
        try { return WebUtility.UrlDecode(s ?? string.Empty); }
        catch { return s; }
    }

    private static string DecodeMany(string s, int rounds)
    {
        string x = s ?? string.Empty;
        for (int i = 0; i < rounds; i++)
        {
            string y = Decode(x);
            if (string.Equals(y, x, StringComparison.Ordinal)) break;
            x = y;
        }
        return x;
    }

    private static string NormKey(string s)
    {
        if (!Has(s)) return string.Empty;
        string x = s.Trim();
        if (x.StartsWith("X-", StringComparison.OrdinalIgnoreCase)) x = x.Substring(2);
        return new string(x.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static bool SameKey(string a, string b) => NormKey(a) == NormKey(b);
    private static bool MatchAlias(string normalized, string[] aliases) => aliases.Any(a => NormKey(a) == normalized);

    private static string Kind(JsonNode n)
    {
        if (n == null) return "non-json";
        if (n is JsonObject) return "object";
        if (n is JsonArray) return "array";
        if (n is JsonValue) return "value";
        return n.GetType().Name;
    }

    private static string Shape(JsonNode n, int depth)
    {
        if (n == null) return "null";
        if (depth > 4) return "…";
        try
        {
            if (n is JsonObject obj)
                return Limit("{" + string.Join(",", obj.Take(16).Select(kv => kv.Key + ":" + Shape(kv.Value, depth + 1))) + "}", 700);
            if (n is JsonArray arr)
                return Limit("[" + string.Join(",", arr.Take(5).Select(x => Shape(x, depth + 1))) + (arr.Count > 5 ? ",…" : "") + "]", 700);
            string s = NodeString(n) ?? string.Empty;
            if (LooksCommand(s)) return $"<command:{s.Length}>";
            if (Uri.TryCreate(s, UriKind.Absolute, out _)) return $"<url:{s.Length}>";
            return $"<scalar:{s.Length}>";
        }
        catch { return "<?>"; }
    }

    private static string Limit(string s, int max) => !Has(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
    private static bool Has(string s) => !string.IsNullOrWhiteSpace(s);
}
