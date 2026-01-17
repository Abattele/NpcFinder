using NpcFinder.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NpcFinder.Services
{
    public class WikiNpcService
    {
        private readonly RateLimiter _rate;
        private readonly CacheStore _cache;

        public WikiNpcService(RateLimiter rate, CacheStore cache)
        {
            _rate = rate;
            _cache = cache;
        }

        public async Task<WikiLookupResult> ResolveByNpcNameAsync(string npcName, CancellationToken ct)
        {
            var key = "wiki-name-v2-" + npcName.Trim().ToLowerInvariant();

            // cache ok for name search (no wikitext needed here)
            WikiLookupResult cached;
            if (_cache.TryLoad(key, out cached) && cached != null) return cached;

            await _rate.WaitAsync(ct);
            var titles = await SearchTitlesAsync(npcName, ct);
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

        private async Task<List<string>> SearchTitlesAsync(string query, CancellationToken ct)
        {

            //limit first 20 results
            string url =
                "https://wiki.guildwars2.com/api.php" +
                "?action=query&list=search&srlimit=20" +
                "&srsearch=" + Uri.EscapeDataString(query) +
                "&format=json";

            string json = await DownloadStringAsync(url, ct);

            using (var doc = JsonDocument.Parse(json))
            {
                var arr = doc.RootElement.GetProperty("query").GetProperty("search");
                var list = new List<string>();

                foreach (var el in arr.EnumerateArray())
                {
                    var t = el.GetProperty("title").GetString();
                    if (!string.IsNullOrWhiteSpace(t)) list.Add(t);
                }

                return list.Distinct().ToList();
            }
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
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "NpcFinder-BlishHUD";
                ct.ThrowIfCancellationRequested();
                return await wc.DownloadStringTaskAsync(url);
            }
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

            var rxAny = new Regex(@"\[\s*(\d{1,5})\s*,\s*(\d{1,5})\s*\]");
            foreach (Match m in rxAny.Matches(wikitext))
            {
                int x, y;
                if (!int.TryParse(m.Groups[1].Value, out x)) continue;
                if (!int.TryParse(m.Groups[2].Value, out y)) continue;
                if (x < 0 || x > 40000 || y < 0 || y > 40000) continue;

                int idx = m.Index;
                int start = Math.Max(0, idx - 120);
                int end = Math.Min(wikitext.Length, idx + m.Length + 120);
                string near = wikitext.Substring(start, end - start);

                list.Add((x, y, near));
            }

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

