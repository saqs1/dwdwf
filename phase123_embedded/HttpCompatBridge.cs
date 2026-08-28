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
    private static readonly string[] CommandAliases = { "command", "cmd", "path", "route", "url", "request", "requestUrl", "action", "payload", "data" };

    private static HttpListener _listener;
    private static CancellationTokenSource _cts;
    private static bool _started;
    private static int _missingDiagCount;

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
            OliverBootstrap.LogSource?.LogInfo("[OLIVER HTTP] Supports query, JSON object/array, nested/stringified JSON, form, multipart, headers, key/value params and embedded command strings.");
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
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Superdupertoken, *";
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
            JsonNode root = TryParseNode(incomingBody);
            JsonObject body = root as JsonObject ?? new JsonObject();

            MergeQuery(request, rawUrl, body);
            MergeHeaders(request, body);
            MergeFormOrMultipart(incomingBody, request.ContentType, body);
            MergeRecursiveAliases(root, body);
            MergePairObjects(root, body, 0);
            MergeLooseRawBody(incomingBody, body);

            string embeddedCommand = FindAnyEmbeddedCommand(root, incomingBody, 0);
            if (HasValue(embeddedCommand))
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

                if (!HasValue(name) && !HasValue(avatar) && Interlocked.Increment(ref _missingDiagCount) <= 3)
                {
                    string shape = DescribeShape(root, 0);
                    string headers = string.Join(",", request.Headers.AllKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Take(30));
                    OliverBootstrap.LogSource?.LogWarning(
                        $"[OLIVER HTTP DIAG] metadata absent. contentType='{request.ContentType ?? ""}', bodyLength={incomingBody.Length}, root={RootKind(root)}, shape={shape}, headerNames=[{headers}]");
                }
            }

            HttpResponseMessage internalResponse = null;
            Exception lastError = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    using HttpRequestMessage forward = new HttpRequestMessage(HttpMethod.Post, InternalBase + route);
                    forward.Content = new StringContent(normalizedBody, Encoding.UTF8, "application/json");
                    string token = request.Headers["Superdupertoken"];
                    if (HasValue(token)) forward.Headers.TryAddWithoutValidation("Superdupertoken", token);
                    internalResponse = await Client.SendAsync(forward).ConfigureAwait(false);
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

    private static JsonNode TryParseNode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); } catch { }
        string decoded = DecodeRepeated(text, 2);
        if (!string.Equals(decoded, text, StringComparison.Ordinal))
        {
            try { return JsonNode.Parse(decoded); } catch { }
        }
        return null;
    }

    private static void MergeQuery(HttpListenerRequest request, string rawUrl, JsonObject body)
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
        MergeKnownRawParameters(rawUrl, body);
    }

    private static void MergeHeaders(HttpListenerRequest request, JsonObject body)
    {
        try
        {
            foreach (string key in request.Headers.AllKeys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                string value = request.Headers[key];
                if (!HasValue(value)) continue;
                string normalized = NormalizeKey(key);
                if (MatchesAlias(normalized, NameAliases)) SetIfMissing(body, "name", value);
                else if (MatchesAlias(normalized, AvatarAliases)) SetIfMissing(body, "avatarUrl", value);
                else if (MatchesAlias(normalized, HeadAliases)) SetIfMissing(body, "headAvatarUrl", value);
                else if (MatchesAlias(normalized, CountAliases)) SetIfMissing(body, "count", value);
            }
        }
        catch { }
    }

    private static void MergeFormOrMultipart(string text, string contentType, JsonObject body)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string ct = contentType ?? string.Empty;

        if (ct.IndexOf("multipart/form-data", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Match bm = Regex.Match(ct, "boundary=(?:\"(?<b>[^\"]+)\"|(?<b>[^;]+))", RegexOptions.IgnoreCase);
            if (bm.Success)
            {
                string boundary = bm.Groups["b"].Value.Trim();
                foreach (string part in text.Split(new[] { "--" + boundary }, StringSplitOptions.RemoveEmptyEntries))
                {
                    Match nm = Regex.Match(part, "name=\"(?<n>[^\"]+)\"", RegexOptions.IgnoreCase);
                    if (!nm.Success) continue;
                    int split = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (split < 0) split = part.IndexOf("\n\n", StringComparison.Ordinal);
                    if (split < 0) continue;
                    string value = part.Substring(split + (part[split] == '\r' ? 4 : 2)).Trim().TrimEnd('-').Trim();
                    MergeNamedValue(nm.Groups["n"].Value, value, body);
                }
            }
            return;
        }

        bool looksForm = ct.IndexOf("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0;
        if (looksForm || (!text.TrimStart().StartsWith("{", StringComparison.Ordinal) && !text.TrimStart().StartsWith("[", StringComparison.Ordinal) && text.Contains("=")))
            MergeKnownRawParameters("?" + text.Trim(), body);
    }

    private static void MergeRecursiveAliases(JsonNode root, JsonObject body)
    {
        SetFromRecursive(root, body, "name", NameAliases);
        SetFromRecursive(root, body, "avatarUrl", AvatarAliases);
        SetFromRecursive(root, body, "headAvatarUrl", HeadAliases);
        SetFromRecursive(root, body, "count", CountAliases);
    }

    private static void SetFromRecursive(JsonNode root, JsonObject body, string canonical, string[] aliases)
    {
        if (HasValue(GetStringCaseInsensitive(body, canonical))) return;
        string found = FindAliasValueRecursive(root, aliases, 0);
        if (HasValue(found)) body[canonical] = found;
    }

    private static string FindAliasValueRecursive(JsonNode node, string[] aliases, int depth)
    {
        if (node == null || depth > 10) return null;

        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (!aliases.Any(a => KeysEqual(a, kv.Key))) continue;
                string direct = ExtractUsefulScalar(kv.Value, depth + 1);
                if (HasValue(direct)) return direct;
            }
            foreach (var kv in obj)
            {
                string nested = FindAliasValueRecursive(kv.Value, aliases, depth + 1);
                if (HasValue(nested)) return nested;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode child in arr)
            {
                string nested = FindAliasValueRecursive(child, aliases, depth + 1);
                if (HasValue(nested)) return nested;
            }
        }
        else if (node is JsonValue)
        {
            string text = NodeToString(node);
            if (LooksLikeJson(text))
            {
                JsonNode nestedNode = TryParseNode(DecodeRepeated(text, 2));
                string nested = FindAliasValueRecursive(nestedNode, aliases, depth + 1);
                if (HasValue(nested)) return nested;
            }
        }
        return null;
    }

    private static string ExtractUsefulScalar(JsonNode node, int depth)
    {
        if (node == null) return null;
        if (node is JsonValue)
        {
            string s = DecodeRepeated(NodeToString(node), 2);
            if (LooksLikeJson(s))
            {
                JsonNode parsed = TryParseNode(s);
                string url = FindFirstUrlRecursive(parsed, depth + 1);
                if (HasValue(url)) return url;
            }
            return s;
        }
        string firstUrl = FindFirstUrlRecursive(node, depth + 1);
        return firstUrl;
    }

    private static string FindFirstUrlRecursive(JsonNode node, int depth)
    {
        if (node == null || depth > 12) return null;
        if (node is JsonValue)
        {
            string s = DecodeRepeated(NodeToString(node), 2);
            if (Uri.TryCreate(s, UriKind.Absolute, out Uri uri) && (uri.Scheme == "http" || uri.Scheme == "https")) return s;
            if (LooksLikeJson(s)) return FindFirstUrlRecursive(TryParseNode(s), depth + 1);
            return null;
        }
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                string found = FindFirstUrlRecursive(kv.Value, depth + 1);
                if (HasValue(found)) return found;
            }
        }
        if (node is JsonArray arr)
        {
            foreach (JsonNode child in arr)
            {
                string found = FindFirstUrlRecursive(child, depth + 1);
                if (HasValue(found)) return found;
            }
        }
        return null;
    }

    private static void MergePairObjects(JsonNode node, JsonObject body, int depth)
    {
        if (node == null || depth > 10) return;
        if (node is JsonObject obj)
        {
            string keyName = FirstDirectString(obj, new[] { "key", "param", "parameter", "field", "property", "propertyName" });
            string value = FirstDirectString(obj, new[] { "value", "val", "text", "content" });
            if (HasValue(keyName) && HasValue(value)) MergeNamedValue(keyName, value, body);
            foreach (var kv in obj) MergePairObjects(kv.Value, body, depth + 1);
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode child in arr) MergePairObjects(child, body, depth + 1);
        }
        else if (node is JsonValue)
        {
            string s = DecodeRepeated(NodeToString(node), 2);
            if (LooksLikeJson(s)) MergePairObjects(TryParseNode(s), body, depth + 1);
        }
    }

    private static string FirstDirectString(JsonObject obj, string[] keys)
    {
        foreach (var kv in obj)
        {
            if (!keys.Any(k => KeysEqual(k, kv.Key))) continue;
            string value = NodeToString(kv.Value);
            if (HasValue(value)) return value;
        }
        return null;
    }

    private static void MergeNamedValue(string key, string value, JsonObject body)
    {
        if (!HasValue(key) || !HasValue(value)) return;
        string normalized = NormalizeKey(key);
        if (MatchesAlias(normalized, NameAliases)) SetIfMissing(body, "name", value);
        else if (MatchesAlias(normalized, AvatarAliases)) SetIfMissing(body, "avatarUrl", value);
        else if (MatchesAlias(normalized, HeadAliases)) SetIfMissing(body, "headAvatarUrl", value);
        else if (MatchesAlias(normalized, CountAliases)) SetIfMissing(body, "count", value);
        else if (MatchesAlias(normalized, CommandAliases) && LooksLikeCommand(value)) MergeKnownRawParameters(value, body);
    }

    private static string FindAnyEmbeddedCommand(JsonNode node, string rawBody, int depth)
    {
        if (depth > 12) return null;
        if (node is JsonValue)
        {
            string s = DecodeRepeated(NodeToString(node), 3);
            if (LooksLikeCommand(s)) return s;
            if (LooksLikeJson(s))
            {
                string nested = FindAnyEmbeddedCommand(TryParseNode(s), null, depth + 1);
                if (HasValue(nested)) return nested;
            }
        }
        else if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                string nested = FindAnyEmbeddedCommand(kv.Value, null, depth + 1);
                if (HasValue(nested)) return nested;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode child in arr)
            {
                string nested = FindAnyEmbeddedCommand(child, null, depth + 1);
                if (HasValue(nested)) return nested;
            }
        }

        if (depth == 0 && HasValue(rawBody))
        {
            string decoded = DecodeRepeated(rawBody.Trim().Trim('"'), 3);
            if (LooksLikeCommand(decoded)) return decoded;
            Match m = Regex.Match(decoded, @"(?:spawncustomer|spawnshoplifter|spawnnpc)[^\r\n\"']*", RegexOptions.IgnoreCase);
            if (m.Success) return m.Value;
        }
        return null;
    }

    private static void MergeLooseRawBody(string text, JsonObject body)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        ExtractLooseJsonString(text, NameAliases, "name", body);
        ExtractLooseJsonString(text, AvatarAliases, "avatarUrl", body);
        ExtractLooseJsonString(text, HeadAliases, "headAvatarUrl", body);

        string decoded = DecodeRepeated(text, 3);
        if (!string.Equals(decoded, text, StringComparison.Ordinal))
        {
            ExtractLooseJsonString(decoded, NameAliases, "name", body);
            ExtractLooseJsonString(decoded, AvatarAliases, "avatarUrl", body);
            ExtractLooseJsonString(decoded, HeadAliases, "headAvatarUrl", body);
            if (decoded.Contains("=")) MergeKnownRawParameters("?" + decoded, body);
        }
    }

    private static void ExtractLooseJsonString(string text, string[] aliases, string canonical, JsonObject body)
    {
        if (HasValue(GetStringCaseInsensitive(body, canonical))) return;
        foreach (string alias in aliases)
        {
            Match m = Regex.Match(text, "[\\\"']" + Regex.Escape(alias) + "[\\\"']\\s*:\\s*[\\\"'](?<v>(?:\\\\.|[^\\\"'])*)[\\\"']", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            string value = DecodeRepeated(m.Groups["v"].Value.Replace("\\/", "/").Replace("\\u0026", "&"), 2);
            if (HasValue(value)) { body[canonical] = value; return; }
        }
    }

    private static void MergeKnownRawParameters(string source, JsonObject body)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        source = DecodeRepeated(source, 2);
        int q = source.IndexOf('?');
        string query = q >= 0 ? source.Substring(q + 1) : source;
        if (string.IsNullOrWhiteSpace(query)) return;

        foreach (string pair in query.Split('&'))
        {
            if (string.IsNullOrWhiteSpace(pair)) continue;
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string key = DecodeRepeated(pair.Substring(0, eq), 2);
            string value = DecodeRepeated(pair.Substring(eq + 1), 2);
            MergeNamedValue(key, value, body);
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value) && GetStringCaseInsensitive(body, key) == null)
                body[key] = value;
        }

        string[] stopsForName = AvatarAliases.Concat(HeadAliases).Concat(CountAliases).Concat(CommandAliases).ToArray();
        string[] stopsForAvatar = HeadAliases.Concat(NameAliases).Concat(CountAliases).Concat(CommandAliases).ToArray();
        string[] stopsForHead = NameAliases.Concat(AvatarAliases).Concat(CountAliases).Concat(CommandAliases).ToArray();
        string[] stopsForCount = NameAliases.Concat(AvatarAliases).Concat(HeadAliases).Concat(CommandAliases).ToArray();
        string name = ExtractRawValue(query, NameAliases, stopsForName);
        string avatar = ExtractRawValue(query, AvatarAliases, stopsForAvatar);
        string head = ExtractRawValue(query, HeadAliases, stopsForHead);
        string count = ExtractRawValue(query, CountAliases, stopsForCount);
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
            string decoded = DecodeRepeated(raw, 2);
            if (HasValue(decoded)) return decoded;
        }
        return null;
    }

    private static void NormalizeAliases(JsonObject body)
    {
        SetCanonical(body, "name", NameAliases);
        SetCanonical(body, "avatarUrl", AvatarAliases);
        SetCanonical(body, "headAvatarUrl", HeadAliases);
        SetCanonical(body, "count", CountAliases);
    }

    private static void SetCanonical(JsonObject body, string canonical, string[] aliases)
    {
        string existing = GetStringCaseInsensitive(body, canonical);
        if (HasValue(existing)) { body[canonical] = existing; return; }
        foreach (string alias in aliases)
        {
            string value = GetStringCaseInsensitive(body, alias);
            if (HasValue(value)) { body[canonical] = value; return; }
        }
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
        string s = DecodeRepeated(value.Trim().Trim('"'), 2);
        Match routeMatch = Regex.Match(s, @"(?:^|/)(spawncustomer|spawnshoplifter|spawnnpc)(?:\?|$)", RegexOptions.IgnoreCase);
        if (routeMatch.Success) return "/" + routeMatch.Groups[1].Value;
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

    private static bool LooksLikeCommand(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string s = DecodeRepeated(value, 2).ToLowerInvariant();
        return s.Contains("spawncustomer") || s.Contains("spawnshoplifter") || s.Contains("spawnnpc");
    }

    private static bool LooksLikeJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string s = value.Trim();
        return (s.StartsWith("{", StringComparison.Ordinal) && s.EndsWith("}", StringComparison.Ordinal)) ||
               (s.StartsWith("[", StringComparison.Ordinal) && s.EndsWith("]", StringComparison.Ordinal));
    }

    private static string GetStringCaseInsensitive(JsonObject body, string key)
    {
        foreach (KeyValuePair<string, JsonNode> kv in body)
        {
            if (!KeysEqual(kv.Key, key)) continue;
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

    private static string DecodeRepeated(string value, int rounds)
    {
        string current = value ?? string.Empty;
        for (int i = 0; i < rounds; i++)
        {
            try
            {
                string next = WebUtility.UrlDecode(current);
                if (string.Equals(next, current, StringComparison.Ordinal)) break;
                current = next;
            }
            catch { break; }
        }
        return current;
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        string k = key.Trim();
        if (k.StartsWith("X-", StringComparison.OrdinalIgnoreCase)) k = k.Substring(2);
        return new string(k.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static bool KeysEqual(string a, string b) => NormalizeKey(a) == NormalizeKey(b);
    private static bool MatchesAlias(string normalizedKey, string[] aliases) => aliases.Any(a => NormalizeKey(a) == normalizedKey);

    private static void SetIfMissing(JsonObject body, string key, string value)
    {
        if (!HasValue(value)) return;
        if (!HasValue(GetStringCaseInsensitive(body, key))) body[key] = DecodeRepeated(value, 2);
    }

    private static string DescribeShape(JsonNode node, int depth)
    {
        if (node == null) return "null";
        if (depth > 4) return "…";
        try
        {
            if (node is JsonObject obj)
            {
                string s = "{" + string.Join(",", obj.Take(16).Select(kv => kv.Key + ":" + DescribeShape(kv.Value, depth + 1))) + "}";
                return Limit(s, 700);
            }
            if (node is JsonArray arr)
            {
                string inner = string.Join(",", arr.Take(5).Select(x => DescribeShape(x, depth + 1)));
                return Limit("[" + inner + (arr.Count > 5 ? ",…" : "") + "]", 700);
            }
            string v = NodeToString(node) ?? string.Empty;
            if (LooksLikeCommand(v)) return $"<command:{v.Length}>";
            if (Uri.TryCreate(v, UriKind.Absolute, out _)) return $"<url:{v.Length}>";
            return $"<scalar:{v.Length}>";
        }
        catch { return "<?>"; }
    }

    private static string RootKind(JsonNode node)
    {
        if (node == null) return "non-json";
        if (node is JsonObject) return "object";
        if (node is JsonArray) return "array";
        if (node is JsonValue) return "value";
        return node.GetType().Name;
    }

    private static string Limit(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
    private static bool HasValue(string value) => !string.IsNullOrWhiteSpace(value);
    private static string GetString(JsonObject body, string key) => GetStringCaseInsensitive(body, key);
    private static bool IsSpawnRoute(string route) => IsKnownRoute(route);
}
