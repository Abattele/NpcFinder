using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NpcFinder.Models;

namespace NpcFinder.Services
{
    public class WikiNpcService
    {


        private readonly RateLimiter _rate;
        private readonly CacheStore _cache;
        private readonly HttpClient _http;

        private sealed class WikiSearchResponse
        {
            public WikiSearchQuery query { get; set; }
        }
        private sealed class WikiSearchQuery
        {
            public List<WikiSearchItem> search { get; set; }
        }
        private sealed class WikiSearchItem
        {
            public string title { get; set; }
        }


        public WikiNpcService(RateLimiter rate, CacheStore cache)
        {
            _rate = rate;
            _cache = cache;
            _http = new HttpClient();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("NpcFinder-BlishHUD");

        }


        public async Task<WikiLookupResult> ResolveByNpcNameAsync(string npcName, CancellationToken ct)
        {
            var key = "wiki-name-v2-" + npcName.Trim().ToLowerInvariant();

            // cache ok for name search (no wikitext needed here)
            WikiLookupResult cached;
            if (_cache.TryLoad(key, out cached) && cached != null) return cached;

            await _rate.WaitAsync(ct);
            var titles = await SearchTitlesAsync(npcName,20, ct);
            if (titles.Count == 0) return null;

            if (titles.Count > 1)
            {
                var res = new WikiLookupResult { CandidateTitles = titles };
                _cache.Save(key, res);
                return res;
            }

            var single = await ResolveByTitleAsync(titles[0], ct);
            if (single != null) _cache.Save(key, single);
            return single;
        }


        public async Task<List<string>> SearchTitlesAsync(string query, int limit, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<string>();

            limit = Math.Max(1, Math.Min(limit, 20));

            // use intitle: so "Arlo" finds "Farmer Arlo"
            string sr = "intitle:" + query.Trim();

            string url =
                "https://wiki.guildwars2.com/api.php" +
                "?action=query" +
                "&list=search" +
                "&srnamespace=0" +
                "&srlimit=" + limit +
                "&format=json" +
                "&srsearch=" + Uri.EscapeDataString(sr);

            string json = await DownloadStringAsync(url, ct);

            var result = new List<string>();

            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("query", out var q) &&
                    q.TryGetProperty("search", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (el.TryGetProperty("title", out var tEl))
                        {
                            var t = tEl.GetString();
                            if (!string.IsNullOrWhiteSpace(t))
                                result.Add(t);
                        }
                    }
                }
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }


        public async Task<WikiLookupResult> ResolveByTitleAsync(string title, CancellationToken ct)
        {
            var key = "wiki-title-v2-" + title.Trim().ToLowerInvariant();

            // old cached objects (v1) didn't have Wikitext; even in v2,
            // so if Wikitext is missing we treat it as a cache miss and refetch.

            WikiLookupResult cached;
            if (_cache.TryLoad(key, out cached) && cached != null)
            {
                if (!string.IsNullOrWhiteSpace(cached.Wikitext))
                    return cached;
            }

            await _rate.WaitAsync(ct);
            var wikitext = await GetWikitextAsync(title, ct);
            if (string.IsNullOrWhiteSpace(wikitext)) return null;

            var pageMapName = ParseMapNameBestEffort(wikitext);
            var coords = ParseAllCoordinates(wikitext);

            var hits = new List<NpcCandidateHit>();
            foreach (var c in coords)
            {
                string mapName = ParseMapNameNearText(c.nearText) ?? pageMapName;

                hits.Add(new NpcCandidateHit
                {
                    Title = title,
                    MapName = mapName,
                    MapId = null,
                    X = c.x,
                    Y = c.y
                });
            }

            hits = hits
                .GroupBy(h => (h.MapName ?? "") + "|" + h.X + "|" + h.Y)
                .Select(g => g.First())
                .ToList();

            var res = new WikiLookupResult
            {
                Title = title,
                DisplayName = title,
                Wikitext = wikitext,
                Hits = hits
            };

            _cache.Save(key, res);
            return res;
        }

        private async Task<string> GetWikitextAsync(string title, CancellationToken ct)
        {
            string url =
                "https://wiki.guildwars2.com/api.php" +
                "?action=parse&redirects=1&prop=wikitext&format=json&formatversion=2" +
                "&page=" + Uri.EscapeDataString(title);

            string json = await DownloadStringAsync(url, ct);

            using (var doc = JsonDocument.Parse(json))
            {
                var parse = doc.RootElement.GetProperty("parse");

                if (parse.TryGetProperty("wikitext", out var wt))
                {
                    if (wt.ValueKind == JsonValueKind.String) return wt.GetString();
                    if (wt.ValueKind == JsonValueKind.Object && wt.TryGetProperty("*", out var star)) return star.GetString();
                }

                return null;
            }
        }

        private async Task<string> DownloadStringAsync(string url, CancellationToken ct)
        {

            const int maxAttempts = 4;
            int backoffMs = 900;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "NpcFinder-BlishHUD";

                        var dlTask = wc.DownloadStringTaskAsync(url);

                        var completed = await Task.WhenAny(dlTask, Task.Delay(TimeSpan.FromSeconds(12), ct))
                                                  .ConfigureAwait(false);

                        if (completed != dlTask)
                            throw new WebException("Wiki request timed out.");

                        return await dlTask.ConfigureAwait(false);
                    }
                }
                catch (WebException ex)
                {
                    int code = 0;

                    try
                    {
                        if (ex.Response is HttpWebResponse resp)
                            code = (int)resp.StatusCode;
                    }
                    catch { }

                    // retry only for rate limit / transient
                    bool retry =
                        code == 429 || code == 503 || code == 502 || code == 504 ||
                        ex.Status == WebExceptionStatus.Timeout ||
                        ex.Status == WebExceptionStatus.ConnectFailure ||
                        ex.Status == WebExceptionStatus.NameResolutionFailure;

                    if (!retry || attempt == maxAttempts)
                        throw;

                    // backoff (with a bit of jitter)
                    int jitter = new Random().Next(0, 250);
                    int wait = backoffMs + jitter;
                    backoffMs = Math.Min(backoffMs * 2, 6000);

                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
            }

            // should never get here
            throw new WebException("Wiki request failed after retries.");
        }

        private static string ParseMapNameBestEffort(string wikitext)
        {
            string TryMatch(string pattern)
            {
                var rx = new Regex(pattern, RegexOptions.IgnoreCase);
                var m = rx.Match(wikitext);
                if (!m.Success) return null;

                var raw = m.Groups[1].Value.Trim();
                raw = Regex.Replace(raw, @"\[\[([^\]\|]+)(\|[^\]]+)?\]\]", "$1");
                raw = Regex.Replace(raw, @"\{\{[^}]+\}\}", "");
                raw = Regex.Replace(raw, @"<!--.*?-->", "");
                raw = raw.Trim();

                if (raw.Length < 3) return null;
                raw = raw.Split(new[] { "<br", "\n" }, StringSplitOptions.None)[0].Trim();
                raw = raw.Trim().TrimEnd('.', ',', ';');

                return string.IsNullOrWhiteSpace(raw) ? null : raw;
            }

            return
                TryMatch(@"\|\s*map\s*=\s*([^\r\n]+)") ??
                TryMatch(@"\|\s*location\s*=\s*([^\r\n]+)") ??
                TryMatch(@"\|\s*zone\s*=\s*([^\r\n]+)") ??
                null;
        }

        private static List<(int x, int y, string nearText)> ParseAllCoordinates(string wikitext)
        {
            var list = new List<(int, int, string)>();
            if (string.IsNullOrWhiteSpace(wikitext)) return list;

            bool IsPlausible(int x, int y)
            {
                // allow 0..300k
                if (x < -1000 || y < -1000) return false;
                if (x > 300000 || y > 300000) return false;
                return true;
            }

            void Add(double xd, double yd, int idx, int len)
            {
                int x = (int)Math.Round(xd);
                int y = (int)Math.Round(yd);

                if (!IsPlausible(x, y)) return;

                int start = Math.Max(0, idx - 140);
                int end = Math.Min(wikitext.Length, idx + len + 140);
                string near = wikitext.Substring(start, end - start);

                list.Add((x, y, near));
            }

            // 1/ bracket form: [43696, 28628] (allow 1-6 digits + optional decimals/sign)
            foreach (Match m in Regex.Matches(
                         wikitext,
                         @"\[\s*(?<x>-?\d{1,6}(?:\.\d+)?)\s*,\s*(?<y>-?\d{1,6}(?:\.\d+)?)\s*\]"))
            {
                if (double.TryParse(m.Groups["x"].Value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var xd) &&
                    double.TryParse(m.Groups["y"].Value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var yd))
                {
                    Add(xd, yd, m.Index, m.Length);
                }
            }

            // 2/ infobox-ish form: coordinates = 43696, 28628  (with or without brackets)
            foreach (Match m in Regex.Matches(
                         wikitext,
                         @"(?im)\bcoordinates\s*=\s*(?:\[\s*)?(?<x>-?\d{1,6}(?:\.\d+)?)\s*,\s*(?<y>-?\d{1,6}(?:\.\d+)?)"))
            {
                if (double.TryParse(m.Groups["x"].Value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var xd) &&
                    double.TryParse(m.Groups["y"].Value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var yd))
                {
                    Add(xd, yd, m.Index, m.Length);
                }
            }

            // 3/ template param form inside blocks: |x=... |y=...
            // (covers a bunch of map templates that aren't exactly "[x,y]" in plain text)
            foreach (Match m in Regex.Matches(
                         wikitext,
                         @"(?is)\|\s*x\s*=\s*(?<x>-?\d{1,6}(?:\.\d+)?)\s*\|\s*y\s*=\s*(?<y>-?\d{1,6}(?:\.\d+)?)"))
            {
                if (double.TryParse(m.Groups["x"].Value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var xd) &&
                    double.TryParse(m.Groups["y"].Value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var yd))
                {
                    Add(xd, yd, m.Index, m.Length);
                }
            }

            // dedupe
            return list
                .GroupBy(t => t.Item1 + "|" + t.Item2)
                .Select(g => g.First())
                .ToList();
        }


        public async Task<List<string>> SuggestTitlesAsync(string prefix, int limit, CancellationToken ct)
        {
            prefix = (prefix ?? "").Trim();
            if (prefix.Length < 2) return new List<string>();

            limit = Math.Max(1, Math.Min(limit, 20));

            var key = $"wiki-suggest-v1-{prefix.ToLowerInvariant()}-{limit}";

            List<string> cached;
            if (_cache.TryLoad(key, out cached) && cached != null && cached.Count > 0)
                return cached;

            await _rate.WaitAsync(ct);

            // MediaWiki prefixsearch is perfect for "Quee" -> "Queensdale" style suggestions.
            string url =
                "https://wiki.guildwars2.com/api.php" +
                "?action=query&list=prefixsearch" +
                "&pslimit=" + limit +
                "&pssearch=" + Uri.EscapeDataString(prefix) +
                "&format=json";

            string json = await DownloadStringAsync(url, ct);

            var list = new List<string>();

            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("query", out var q) &&
                    q.TryGetProperty("prefixsearch", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (el.TryGetProperty("title", out var tEl))
                        {
                            var t = tEl.GetString();
                            if (!string.IsNullOrWhiteSpace(t)) list.Add(t);
                        }
                    }
                }
            }

            list = list.Distinct().Take(limit).ToList();
            if (list.Count > 0)
                _cache.Save(key, list);

            return list;
        }

        private static string ParseMapNameNearText(string nearText)
        {
            string TryMatch(string pattern)
            {
                var rx = new Regex(pattern, RegexOptions.IgnoreCase);
                var m = rx.Match(nearText);
                if (!m.Success) return null;

                var raw = m.Groups[1].Value.Trim();
                raw = Regex.Replace(raw, @"\[\[([^\]\|]+)(\|[^\]]+)?\]\]", "$1");
                raw = Regex.Replace(raw, @"\{\{[^}]+\}\}", "");
                raw = Regex.Replace(raw, @"<!--.*?-->", "");
                raw = raw.Trim();

                if (raw.Length < 3) return null;
                raw = raw.Split(new[] { "<br", "\n", "|", "}" }, StringSplitOptions.None)[0].Trim();
                raw = raw.Trim().TrimEnd('.', ',', ';');

                return string.IsNullOrWhiteSpace(raw) ? null : raw;
            }

            return
                TryMatch(@"\|\s*map\s*=\s*([^\r\n]+)") ??
                TryMatch(@"\|\s*location\s*=\s*([^\r\n]+)") ??
                TryMatch(@"\|\s*zone\s*=\s*([^\r\n]+)") ??
                null;
        }
    }
}