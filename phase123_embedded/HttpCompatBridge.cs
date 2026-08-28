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
    private static readonly string[] NameAliases = { "name", "nickname", "userName", "username", "displayName", "uniqueId", "user" };
    private static readonly string[] AvatarAliases = { "avatarUrl", "avatarURL", "profilePictureUrl", "profileImageUrl", "avatar", "imageUrl", "pictureUrl", "photoUrl", "userAvatar" };
    private static readonly string[] HeadAliases = { "headAvatarUrl", "headUrl", "headAvatar", "headImageUrl" };
    private static readonly string[] CountAliases = { "count", "repeat", "repeatCount", "repetitions", "quantity", "amount" };
    private static readonly string[] CommandAliases = { "command", "cmd", "path", "route", "url", "request", "requestUrl" };

    private static HttpListener _listener;
    private static CancellationTokenSource _cts;
    private static bool _started;

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
            OliverBootstrap.LogSource?.LogInfo("[OLIVER HTTP] v0.1.9 compatibility parser ACTIVE on 55001 -> S2E 55101.");
            OliverBootstrap.LogSource?.LogInfo("[OLIVER HTTP] Accepts GET/POST, raw query, JSON, form-urlencoded and command-string parameters.");
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
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                OliverBootstrap.LogSource?.LogWarning($"[OLIVER HTTP] Listener retry: {ex.Message}");
                await Task.Delay(100, token).ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(() => HandleRequest(context), token);
        }
    }

    private static async Task HandleRequest(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        try
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Superdupertoken";
            if (string.Equals(request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            string incomingBody = string.Empty;
            if (request.HasEntityBody)
            {
                using StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
                incomingBody = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            string rawUrl = request.RawUrl ?? request.Url?.PathAndQuery ?? string.Empty;
            JsonObject body = ParseObjectOrEmpty(incomingBody);

            // 1) Standard query parser.
            CopyQueryIntoJson(request, body);
            // 2) Raw-query parser that preserves TikTok avatar URLs containing their own '&' parameters.
            MergeKnownRawParameters(rawUrl, body);
            // 3) application/x-www-form-urlencoded or plain key=value body.
            MergeFormBody(incomingBody, request.ContentType, body);
            // 4) Nested JSON user/data objects.
            MergeNestedAliases(body);
            // 5) command strings like "spawncustomer?name=...&avatarUrl=..." in JSON or plain body.
            string embeddedCommand = FindEmbeddedCommand(body, incomingBody);
            if (!string.IsNullOrWhiteSpace(embeddedCommand))
                MergeKnownRawParameters(embeddedCommand, body);

            NormalizeAliases(body);

            string route = ResolveRoute(request, body, embeddedCommand, incomingBody);
            string normalizedBody = body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

            string name = GetString(body, "name");
            string avatar = GetString(body, "avatarUrl");
            string head = GetString(body, "headAvatarUrl");
            if (IsSpawnRoute(route))
            {
                OliverBootstrap.LogSource?.LogInfo(
                    $"[OLIVER HTTP] {request.HttpMethod} {route}: name={(HasValue(name) ? "YES" : "NO")}, avatar={(HasValue(avatar) ? "YES" : "NO")}, head={(HasValue(head) ? "YES" : "NO")}, rawQuery={(rawUrl.Contains("?") ? "YES" : "NO")}, body={(string.IsNullOrWhiteSpace(incomingBody) ? "NO" : "YES")}");
            }

            using HttpRequestMessage template = new HttpRequestMessage(HttpMethod.Post, InternalBase + route);
            template.Content = new StringContent(normalizedBody, Encoding.UTF8, "application/json");

            HttpResponseMessage internalResponse = null;
            Exception lastError = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    internalResponse = await Client.SendAsync(template.Clone()).ConfigureAwait(false);
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    await Task.Delay(150).ConfigureAwait(false);
                }
            }
            if (internalResponse == null)
                throw new InvalidOperationException("Internal S2E listener on 55101 did not respond.", lastError);

            string result = await internalResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            byte[] bytes = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(result) ? "OTVET" : result);
            response.StatusCode = (int)internalResponse.StatusCode;
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            response.Close();
            internalResponse.Dispose();
        }
        catch (Exception ex)
        {
            OliverBootstrap.LogSource?.LogError($"[OLIVER HTTP] Request forwarding failed: {ex}");
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes("OLIVER_HTTP_ERROR");
                response.StatusCode = 502;
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                response.Close();
            }
            catch { }
        }
    }

    private static JsonObject ParseObjectOrEmpty(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                JsonNode node = JsonNode.Parse(text);
                if (node is JsonObject obj) return obj;
            }
            catch { }
        }
        return new JsonObject();
    }

    private static void CopyQueryIntoJson(HttpListenerRequest request, JsonObject body)
    {
        try
        {
            foreach (string key in request.QueryString.AllKeys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                string value = request.QueryString[key];
                if (value != null) body[key] = value;
            }
        }
        catch { }
    }

    private static void MergeFormBody(string text, string contentType, JsonObject body)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        bool looksForm = (contentType ?? string.Empty).IndexOf("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!looksForm && text.TrimStart().StartsWith("{", StringComparison.Ordinal)) return;
        if (!looksForm && !text.Contains("=")) return;
        MergeKnownRawParameters("?" + text.Trim(), body);
    }

    private static void MergeNestedAliases(JsonObject body)
    {
        SetFromRecursive(body, "name", NameAliases);
        SetFromRecursive(body, "avatarUrl", AvatarAliases);
        SetFromRecursive(body, "headAvatarUrl", HeadAliases);
        SetFromRecursive(body, "count", CountAliases);
    }

    private static void SetFromRecursive(JsonObject body, string canonical, IEnumerable<string> aliases)
    {
        if (HasValue(GetStringCaseInsensitive(body, canonical))) return;
        string found = FindStringRecursive(body, aliases.ToArray(), 0);
        if (HasValue(found)) body[canonical] = found;
    }

    private static string FindStringRecursive(JsonNode node, string[] keys, int depth)
    {
        if (node == null || depth > 6) return null;
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (keys.Any(k => string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    string direct = NodeToString(kv.Value);
                    if (HasValue(direct) && !direct.StartsWith("{", StringComparison.Ordinal) && !direct.StartsWith("[", StringComparison.Ordinal))
                        return direct;
                }
            }
            foreach (var kv in obj)
            {
                string nested = FindStringRecursive(kv.Value, keys, depth + 1);
                if (HasValue(nested)) return nested;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode child in arr)
            {
                string nested = FindStringRecursive(child, keys, depth + 1);
                if (HasValue(nested)) return nested;
            }
        }
        return null;
    }

    private static string FindEmbeddedCommand(JsonObject body, string rawBody)
    {
        string value = FindStringRecursive(body, CommandAliases, 0);
        if (LooksLikeCommand(value)) return value;
        string trimmed = (rawBody ?? string.Empty).Trim().Trim('"');
        return LooksLikeCommand(trimmed) ? trimmed : null;
    }

    private static bool LooksLikeCommand(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string s = value.ToLowerInvariant();
        return s.Contains("spawncustomer") || s.Contains("spawnshoplifter") || s.Contains("spawnnpc");
    }

    private static void MergeKnownRawParameters(string source, JsonObject body)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        int q = source.IndexOf('?');
        string query = q >= 0 ? source.Substring(q + 1) : source;
        if (string.IsNullOrWhiteSpace(query)) return;

        // Normal pairs first.
        foreach (string pair in query.Split('&'))
        {
            if (string.IsNullOrWhiteSpace(pair)) continue;
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string key = Decode(pair.Substring(0, eq));
            string value = Decode(pair.Substring(eq + 1));
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value) && GetStringCaseInsensitive(body, key) == null)
                body[key] = value;
        }

        // Re-extract important fields from the raw query. This preserves an avatar URL's own '&x-...' pieces.
        string name = ExtractRawValue(query, NameAliases, AvatarAliases.Concat(HeadAliases).Concat(CountAliases).Concat(CommandAliases).ToArray());
        string avatar = ExtractRawValue(query, AvatarAliases, HeadAliases.Concat(NameAliases).Concat(CountAliases).Concat(CommandAliases).ToArray());
        string head = ExtractRawValue(query, HeadAliases, NameAliases.Concat(AvatarAliases).Concat(CountAliases).Concat(CommandAliases).ToArray());
        string count = ExtractRawValue(query, CountAliases, NameAliases.Concat(AvatarAliases).Concat(HeadAliases).Concat(CommandAliases).ToArray());
        if (HasValue(name)) body["name"] = name;
        if (HasValue(avatar)) body["avatarUrl"] = avatar;
        if (HasValue(head)) body["headAvatarUrl"] = head;
        if (HasValue(count)) body["count"] = count;
    }

    private static string ExtractRawValue(string query, string[] aliases, string[] stopKeys)
    {
        foreach (string alias in aliases)
        {
            Match m = Regex.Match(query, "(?:^|&)" + Regex.Escape(alias) + "=", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            int start = m.Index + m.Length;
            int end = query.Length;
            foreach (string stop in stopKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Match s = Regex.Match(query.Substring(start), "&" + Regex.Escape(stop) + "=", RegexOptions.IgnoreCase);
                if (s.Success) end = Math.Min(end, start + s.Index);
            }
            string raw = query.Substring(start, Math.Max(0, end - start));
            string decoded = Decode(raw);
            if (HasValue(decoded)) return decoded;
        }
        return null;
    }

    private static string ResolveRoute(HttpListenerRequest request, JsonObject body, string embeddedCommand, string rawBody)
    {
        string route = request.Url?.AbsolutePath ?? "/";
        if (IsKnownRoute(route)) return NormalizeRoute(route);

        foreach (string candidate in new[] { embeddedCommand, GetStringCaseInsensitive(body, "command"), GetStringCaseInsensitive(body, "path"), GetStringCaseInsensitive(body, "route"), rawBody })
        {
            string parsed = ExtractRoute(candidate);
            if (IsKnownRoute(parsed)) return NormalizeRoute(parsed);
        }
        return NormalizeRoute(route);
    }

    private static string ExtractRoute(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string s = value.Trim().Trim('"');
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0 && Uri.TryCreate(s, UriKind.Absolute, out Uri uri)) s = uri.AbsolutePath;
        int q = s.IndexOf('?');
        if (q >= 0) s = s.Substring(0, q);
        if (!s.StartsWith("/", StringComparison.Ordinal)) s = "/" + s;
        return s;
    }

    private static string NormalizeRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route)) return "/";
        int q = route.IndexOf('?');
        if (q >= 0) route = route.Substring(0, q);
        if (!route.StartsWith("/", StringComparison.Ordinal)) route = "/" + route;
        return route;
    }

    private static bool IsKnownRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route)) return false;
        string r = NormalizeRoute(route);
        return r.EndsWith("/spawncustomer", StringComparison.OrdinalIgnoreCase) ||
               r.EndsWith("/spawnshoplifter", StringComparison.OrdinalIgnoreCase) ||
               r.EndsWith("/spawnnpc", StringComparison.OrdinalIgnoreCase);
    }

    private static void NormalizeAliases(JsonObject body)
    {
        SetCanonical(body, "name", NameAliases);
        SetCanonical(body, "avatarUrl", AvatarAliases);
        SetCanonical(body, "headAvatarUrl", HeadAliases);
        SetCanonical(body, "count", CountAliases);
    }

    private static void SetCanonical(JsonObject body, string canonical, IEnumerable<string> aliases)
    {
        string existing = GetStringCaseInsensitive(body, canonical);
        if (HasValue(existing)) { body[canonical] = existing; return; }
        foreach (string alias in aliases)
        {
            string value = GetStringCaseInsensitive(body, alias);
            if (HasValue(value)) { body[canonical] = value; return; }
        }
    }

    private static string GetStringCaseInsensitive(JsonObject body, string key)
    {
        foreach (KeyValuePair<string, JsonNode> kv in body)
        {
            if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            return NodeToString(kv.Value);
        }
        return null;
    }

    private static string NodeToString(JsonNode node)
    {
        try
        {
            if (node == null) return null;
            if (node is JsonValue value && value.TryGetValue<string>(out string text)) return text;
            return node.ToString();
        }
        catch { return null; }
    }

    private static string Decode(string value)
    {
        try { return WebUtility.UrlDecode(value ?? string.Empty); }
        catch { return value; }
    }

    private static bool HasValue(string value) => !string.IsNullOrWhiteSpace(value);
    private static string GetString(JsonObject body, string key) => GetStringCaseInsensitive(body, key);
    private static bool IsSpawnRoute(string route) => IsKnownRoute(route);

    private static HttpRequestMessage Clone(this HttpRequestMessage request)
    {
        HttpRequestMessage clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content != null)
        {
            string content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, "application/json");
        }
        return clone;
    }
}
