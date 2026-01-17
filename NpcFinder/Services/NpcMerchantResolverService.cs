using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD;
using NpcFinder.Models;

namespace NpcFinder.Services
{
    public class NpcMerchantResolverService
    {

        private static readonly Logger Logger = Logger.GetLogger<NpcMerchantResolverService>();
        private readonly WikiNpcService _wiki;
        private readonly Gw2MapIndexService _mapIndex;
        private readonly Gw2ApiService _gw2;
        private readonly Gw2MapDetailsService _details;
        private readonly string _cacheDir;

        // tweak if you want: how long to trust cached results
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(14);


        // inline classes
        private class CacheWrapper
        {
            public string Title { get; set; }
            public DateTime UtcSaved { get; set; }
            public List<NpcResolvedHit> Hits { get; set; }
        }

        private class ScoredCandidate
        {
            public string Kind;
            public string Name;
            public int X;
            public int Y;
            public int Score;

            public ScoredCandidate(string kind, string name, int x, int y, int score)
            {
                Kind = kind;
                Name = name;
                X = x;
                Y = y;
                Score = score;
            }
        }

        // constructor
        public NpcMerchantResolverService(
            WikiNpcService wiki,
            Gw2MapIndexService mapIndex,
            Gw2ApiService gw2,
            Gw2MapDetailsService details,
            string cacheDir)
        {
            _wiki = wiki;
            _mapIndex = mapIndex;
            _gw2 = gw2;
            _details = details;
            _cacheDir = cacheDir;

            try
            {
                if (!string.IsNullOrWhiteSpace(_cacheDir))
                    Directory.CreateDirectory(_cacheDir);
            }
            catch { }
        }

        // ---------------------- PUBLIC ENTRY ----------------------

        public async Task<List<NpcResolvedHit>> ResolveMerchantAsync(string npcTitle, CancellationToken ct)
        {

            // CACHE FIRST
            List<NpcResolvedHit> cached;
            if (TryLoadCachedResolvedHits(npcTitle, out cached))
            {
                Logger.Debug("[MerchantResolve] CACHE HIT title='" + npcTitle + "' hits=" + cached.Count);
                return cached;
            }

            // NORMAL FLOW
            var wikiRes = await _wiki.ResolveByTitleAsync(npcTitle, ct).ConfigureAwait(false);
            if (wikiRes == null) return new List<NpcResolvedHit>();

            string wikitext = wikiRes.Wikitext ?? "";
            var hints = BuildLocationHints(npcTitle, wikitext);

            Logger.Debug("[MerchantResolve] title='" + npcTitle + "' hints=(" + hints.Count + ") " +
                         string.Join(" | ", hints.Take(12)));


            // Resolve ALL possible mapIds
            var mapIds = await ResolveAllMapIdsFromHintsAsync(hints, ct).ConfigureAwait(false);
            if (mapIds.Count == 0)
            {
                Logger.Warn("[MerchantResolve] could not resolve any mapId from hints.");
                return new List<NpcResolvedHit>();
            }


            // Extract NPC coordinates from wikitext
            var coordHints = ExtractNpcCoordinates(wikitext);
            if (coordHints.Count > 0)
                Logger.Warn("[MerchantResolve] extracted " + coordHints.Count + " coord hint(s): " +
                            string.Join(" | ", coordHints.Take(5).Select(c => "[" + c.x + "," + c.y + "] map='" + (c.mapName ?? "") + "'")));


            var hits = new List<NpcResolvedHit>();
            for (int mi = 0; mi < mapIds.Count; mi++)
            {
                int mapId = mapIds[mi];
                ct.ThrowIfCancellationRequested();

                var mapInfo = await _gw2.GetMapInfoAsync(mapId, ct).ConfigureAwait(false);
                if (mapInfo == null)
                {
                    Logger.Warn("[MerchantResolve] mapInfo null for mapId=" + mapId);
                    continue;
                }

                Logger.Info("[MapInfo] mapId=" + mapId + " name='" + mapInfo.Name + "' continentId=" + mapInfo.ContinentId + " defaultFloor=" + mapInfo.DefaultFloor);

                PoiWpFloorResult anchors = await _details.GetPoisAndWaypointsWithFloorFallbackAsync(
                    mapInfo.ContinentId,
                    mapInfo.DefaultFloor,
                    mapInfo.Floors,
                    mapInfo.Id,
                    ct
                ).ConfigureAwait(false);

                Logger.Debug("[MerchantResolve] mapId=" + mapInfo.Id + " usedFloor=" + anchors.UsedFloor + " pois=" + anchors.Pois.Count + " wps=" + anchors.Waypoints.Count);

                if (anchors.Pois.Count == 0 && anchors.Waypoints.Count == 0)
                {
                    hits.Add(new NpcResolvedHit
                    {
                        Title = npcTitle,
                        MapId = mapInfo.Id,
                        MapName = mapInfo.Name,
                        ContinentId = mapInfo.ContinentId,
                        ContinentX = 0,
                        ContinentY = 0,
                        Source = "MapOnly:" + mapInfo.Name,
                        Debug = "NO_ANCHORS floor=" + anchors.UsedFloor + " continentId=" + mapInfo.ContinentId + " defaultFloor=" + mapInfo.DefaultFloor
                    });
                    continue;
                }

                // If we have coords for this map, use them directly (NPC pin), otherwise fallback to WP/POI scoring
                // Basically the algorithm searches first for NPC location from wikitext if available (the interractive map)
                // then, if it has no coords then search for nearest WP/POI, otherwise fallback to name scoring. 
                // Priority has WP over POI. If an NPC is on 2 maps (like Farmer Arlo) then get the coords from wikitext
                // and use them for the respective map -> attention, on wikitext there is only 1 interractive map with 1 coord set,
                // so for the other map we will fallback to name scoring. -> identify which map has the coords via a rectangle check matching alg.
                // and use that coord only for that map.

                var maybeNpcPos = PickBestCoordForMap(coordHints, mapInfo.Name, mapInfo, mapIds.Count);

                ScoredCandidate best = null;

                if (maybeNpcPos.HasValue)
                {
                    int npcX = maybeNpcPos.Value.x;
                    int npcY = maybeNpcPos.Value.y;

                    best = new ScoredCandidate("NPC", npcTitle, npcX, npcY, 10000);

                    // guard for nonsense values
                    if (npcX <= 0 || npcY <= 0)
                    {
                        best = FindNearest("Waypoint", anchors.Waypoints, npcX, npcY)
                               ?? FindNearest("POI", anchors.Pois, npcX, npcY);

                        if (best != null) best.Score = 9999;
                    }
                }

                // Fallback: name scoring algorithm
                if (best == null)
                {
                    var candidates = new List<ScoredCandidate>();

                    for (int i = 0; i < anchors.Waypoints.Count; i++)
                    {
                        var w = anchors.Waypoints[i];
                        int s = ScoreCandidate(w.Item1, hints);
                        if (s > 0) candidates.Add(new ScoredCandidate("Waypoint", w.Item1, w.Item2, w.Item3, s));
                    }

                    for (int i = 0; i < anchors.Pois.Count; i++)
                    {
                        var p = anchors.Pois[i];
                        int s = ScoreCandidate(p.Item1, hints);
                        if (s > 0) candidates.Add(new ScoredCandidate("POI", p.Item1, p.Item2, p.Item3, s));
                    }

                    if (candidates.Count == 0)
                    {
                        hits.Add(new NpcResolvedHit
                        {
                            Title = npcTitle,
                            MapId = mapInfo.Id,
                            MapName = mapInfo.Name,
                            ContinentId = mapInfo.ContinentId,
                            ContinentX = 0,
                            ContinentY = 0,
                            Source = "MapOnly:" + mapInfo.Name,
                            Debug = "NO_SCORED_CANDIDATES floor=" + anchors.UsedFloor
                        });
                        continue;
                    }

                    best = candidates
                        .OrderByDescending(c => c.Score)
                        .ThenBy(c => c.Kind) // Waypoint before POI
                        .First();
                }

                if (best == null)
                {
                    hits.Add(new NpcResolvedHit
                    {
                        Title = npcTitle,
                        MapId = mapInfo.Id,
                        MapName = mapInfo.Name,
                        ContinentId = mapInfo.ContinentId,
                        ContinentX = 0,
                        ContinentY = 0,
                        Source = "MapOnly:" + mapInfo.Name,
                        Debug = "BEST_NULL floor=" + anchors.UsedFloor
                    });
                    continue;
                }

                // for /continents/.../maps/{mapId} points, these are continent coords already
                double cx = best.X;
                double cy = best.Y;

                Logger.Warn("[MerchantResolve] HIT map=" + mapInfo.Name + " cont=" + mapInfo.ContinentId +
                            " best=" + best.Kind + ":" + best.Name + " @" + best.X + "," + best.Y +
                            " -> continent=(" + cx + "," + cy + ")");

                hits.Add(new NpcResolvedHit
                {
                    Title = npcTitle,
                    MapId = mapInfo.Id,
                    MapName = mapInfo.Name,
                    ContinentId = mapInfo.ContinentId,
                    ContinentX = cx,
                    ContinentY = cy,
                    Source = best.Kind + ":" + best.Name,
                    Debug = "floor=" + anchors.UsedFloor + ", coord=" + (maybeNpcPos.HasValue ? (maybeNpcPos.Value.x + "," + maybeNpcPos.Value.y) : "none")
                });
            }

            hits = hits
                .OrderBy(h => h.MapName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Source.StartsWith("Waypoint:", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();

            if (hits.Count == 0)
                Logger.Warn("[MerchantResolve] No hits produced.");

            // SAVE CACHE (even if empty we can cache to avoid repeated work)
            SaveCachedResolvedHits(npcTitle, hits);

            return hits;
        }

        // ---------------------- DISK CACHE ----------------------

        private void EnsureCacheDir()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_cacheDir))
                    Directory.CreateDirectory(_cacheDir);
            }
            catch
            {
                
            }
        }

        private bool TryLoadCachedResolvedHits(string title, out List<NpcResolvedHit> hits)
        {
            hits = null;

            try
            {
                if (string.IsNullOrWhiteSpace(_cacheDir)) return false;

                EnsureCacheDir();

                string path = GetCacheFilePath(title);
                if (!File.Exists(path)) return false;

                var fi = new FileInfo(path);
                if (fi.Length <= 2) return false;

                // TTL 14 days
                if (DateTime.UtcNow - fi.LastWriteTimeUtc > CacheTtl)
                    return false;

                string json = File.ReadAllText(path, Encoding.UTF8);
                var wrapper = JsonSerializer.Deserialize<CacheWrapper>(json);

                if (wrapper == null || wrapper.Hits == null) return false;

                hits = wrapper.Hits;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("[MerchantResolve] cache read failed: " + ex.Message);
                return false;
            }
        }

        private void SaveCachedResolvedHits(string title, List<NpcResolvedHit> hits)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_cacheDir)) return;

                EnsureCacheDir();

                string path = GetCacheFilePath(title);

                var wrapper = new CacheWrapper
                {
                    Title = title,
                    UtcSaved = DateTime.UtcNow,
                    Hits = hits ?? new List<NpcResolvedHit>()
                };

                string json = JsonSerializer.Serialize(wrapper);
                File.WriteAllText(path, json, Encoding.UTF8);

                Logger.Debug("[MerchantResolve] cache write OK path=" + path);
            }
            catch (Exception ex)
            {
                Logger.Warn("[MerchantResolve] cache write failed: " + ex.Message);
            }
        }


        private string GetCacheFilePath(string title)
        {
            // stable filename: sha1(title)
            string safe = Sha1Hex(title ?? "");
            return Path.Combine(_cacheDir, "merchant_" + safe + ".json");
        }

        private static string Sha1Hex(string s)
        {
            try
            {
                using (var sha1 = SHA1.Create())
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(s);
                    byte[] hash = sha1.ComputeHash(bytes);
                    var sb = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++)
                        sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return (s ?? "").GetHashCode().ToString("x8");
            }
        }

        // ---------------------- MAPID RESOLUTION ----------------------

        private async Task<List<int>> ResolveAllMapIdsFromHintsAsync(List<string> hints, CancellationToken ct)
        {
            var ids = new HashSet<int>();
            if (hints == null || hints.Count == 0) return ids.ToList();

            var expanded = new List<string>();

            for (int i = 0; i < hints.Count; i++)
            {
                var c = CleanHint(hints[i]);
                if (string.IsNullOrWhiteSpace(c)) continue;

                var pipeParts = c.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (int p = 0; p < pipeParts.Length; p++)
                {
                    var s = pipeParts[p].Trim();
                    if (s.Length >= 3) expanded.Add(s);

                    var more = SplitMapLikeString(s);
                    for (int k = 0; k < more.Count; k++)
                        expanded.Add(more[k]);
                }
            }

            expanded = expanded
                .Select(s => s.Trim())
                .Where(s => s.Length >= 3 && s.Length <= 60)
                .Where(s => !s.Contains(":"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < expanded.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                string name = expanded[i];

                if (Regex.IsMatch(name, @"\b(the|a|an|merchant|npc|bandit|scout|animal|farmer|watchman)\b",
                                  RegexOptions.IgnoreCase))
                    continue;

                int? id = await _mapIndex.ResolveMapIdByNameAsync(name, ct).ConfigureAwait(false);
                Logger.Debug("[MerchantResolve] try mapName='" + name + "' => mapId=" + (id.HasValue ? id.Value.ToString() : "null"));

                if (id.HasValue)
                    ids.Add(id.Value);

                if (ids.Count >= 15) break;
            }

            return ids.ToList();
        }

        // ---------------------- COORD EXTRACTION ----------------------

        private List<(string mapName, int x, int y)> ExtractNpcCoordinates(string wikitext)
        {
            var list = new List<(string mapName, int x, int y)>();
            if (string.IsNullOrWhiteSpace(wikitext)) return list;

            // NPC infobox: | coordinates = [43696, 28628] for example
            foreach (Match m in Regex.Matches(
                         wikitext,
                         @"\bcoordinates\s*=\s*\[\s*(?<x>-?\d+)\s*,\s*(?<y>-?\d+)\s*\]",
                         RegexOptions.IgnoreCase))
            {
                int x, y;
                if (int.TryParse(SafeDigitsSigned(m.Groups["x"].Value), out x) &&
                    int.TryParse(SafeDigitsSigned(m.Groups["y"].Value), out y))
                {
                    list.Add((null, x, y));
                }
            }

            // {{Interactive map | map=... | x=... | y=... }}
            foreach (Match m in Regex.Matches(wikitext, @"\{\{\s*Interactive map\b.*?\}\}",
                                             RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var block = m.Value;

                string mapName = TryGetTemplateParam(block, "map")
                              ?? TryGetTemplateParam(block, "location")
                              ?? TryGetTemplateParam(block, "region");

                var xStr = TryGetTemplateParam(block, "x");
                var yStr = TryGetTemplateParam(block, "y");

                int x, y;
                if (int.TryParse(SafeDigitsSigned(xStr), out x) && int.TryParse(SafeDigitsSigned(yStr), out y))
                    list.Add((mapName, x, y));
            }

            // {{coord|x|y...}}
            foreach (Match m in Regex.Matches(wikitext, @"\{\{\s*coord\s*\|\s*(?<x>\d+)\s*\|\s*(?<y>\d+)[^}]*\}\}",
                                             RegexOptions.IgnoreCase))
            {
                int x = int.Parse(m.Groups["x"].Value);
                int y = int.Parse(m.Groups["y"].Value);
                list.Add((null, x, y));
            }

            return list;

            string TryGetTemplateParam(string tpl, string key)
            {
                var rx = new Regex(@"\|\s*" + Regex.Escape(key) + @"\s*=\s*(?<v>[^|\}]+)", RegexOptions.IgnoreCase);
                var mm = rx.Match(tpl);
                if (!mm.Success) return null;
                return mm.Groups["v"].Value.Trim();
            }

            string SafeDigitsSigned(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "";
                s = s.Trim();
                var m = Regex.Match(s, @"-?\d+");
                return m.Success ? m.Value : "";
            }
        }

        private (int x, int y)? PickBestCoordForMap(
            List<(string mapName, int x, int y)> coords,
            string mapName,
            Gw2MapInfo mapInfo,
            int totalResolvedMapCount)
        {
            if (coords == null || coords.Count == 0) return null;

            // exact mapName match
            for (int i = 0; i < coords.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(coords[i].mapName) &&
                    string.Equals(coords[i].mapName.Trim(), mapName, StringComparison.OrdinalIgnoreCase))
                {
                    return (coords[i].x, coords[i].y);
                }
            }

            // if coord has no mapName, accept it only if inside this map's continent rect
            for (int i = 0; i < coords.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(coords[i].mapName))
                {
                    int x = coords[i].x;
                    int y = coords[i].y;

                    double minX = Math.Min(mapInfo.ContinentRect.X1, mapInfo.ContinentRect.X2);
                    double maxX = Math.Max(mapInfo.ContinentRect.X1, mapInfo.ContinentRect.X2);
                    double minY = Math.Min(mapInfo.ContinentRect.Y1, mapInfo.ContinentRect.Y2);
                    double maxY = Math.Max(mapInfo.ContinentRect.Y1, mapInfo.ContinentRect.Y2);

                    if (x >= minX && x <= maxX && y >= minY && y <= maxY)
                        return (x, y);
                }
            }

            // single coord fallback only if single resolved map
            if (coords.Count == 1 && totalResolvedMapCount == 1)
                return (coords[0].x, coords[0].y);

            return null;
        }

        private ScoredCandidate FindNearest(string kind, List<Tuple<string, int, int>> pts, int x, int y)
        {
            if (pts == null || pts.Count == 0) return null;

            long bestD = long.MaxValue;
            Tuple<string, int, int> best = null;

            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                long dx = (long)p.Item2 - x;
                long dy = (long)p.Item3 - y;
                long d2 = dx * dx + dy * dy;

                if (d2 < bestD)
                {
                    bestD = d2;
                    best = p;
                }
            }

            if (best == null) return null;
            return new ScoredCandidate(kind, best.Item1, best.Item2, best.Item3, 0);
        }

        // ---------------------- HINT PIPELINE ----------------------

        private List<string> BuildLocationHints(string title, string wikitext)
        {
            var ordered = new List<string>();

            var infobox = ExtractInfoboxMap(wikitext);
            if (!string.IsNullOrWhiteSpace(infobox)) ordered.Add(infobox);

            var locLinks = ExtractLinksFromSection(wikitext, "Location");
            for (int i = 0; i < locLinks.Count; i++) ordered.Add(locLinks[i]);

            var lead = ExtractLeadSentenceLocation(wikitext);
            for (int i = 0; i < lead.Count; i++) ordered.Add(lead[i]);

            var phrases = ExtractPlainLocationPhrases(wikitext);
            for (int i = 0; i < phrases.Count; i++) ordered.Add(phrases[i]);

            var allLinks = ExtractAllLinks(wikitext);
            for (int i = 0; i < allLinks.Count; i++) ordered.Add(allLinks[i]);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var final = new List<string>();

            for (int i = 0; i < ordered.Count; i++)
            {
                var s = CleanHint(ordered[i]);
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (seen.Add(s)) final.Add(s);
            }

            return final;
        }

        private List<string> ExtractAllLinks(string wikitext)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(wikitext)) return list;

            foreach (Match link in Regex.Matches(wikitext, @"\[\[([^\]\|]+)(\|[^\]]+)?\]\]"))
            {
                var s = link.Groups[1].Value.Trim();
                if (s.Length >= 3 && s.Length <= 60) list.Add(s);
            }

            return list;
        }

        private List<string> ExtractLeadSentenceLocation(string wikitext)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(wikitext)) return list;

            var lead = wikitext;
            if (lead.Length > 600) lead = lead.Substring(0, 600);

            lead = Regex.Replace(lead, @"<!--.*?-->", "", RegexOptions.Singleline);
            lead = Regex.Replace(lead, @"<ref.*?>.*?</ref>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            lead = Regex.Replace(lead, @"<.*?>", "", RegexOptions.Singleline);
            lead = Regex.Replace(lead, @"\{\{.*?\}\}", "", RegexOptions.Singleline);
            lead = Regex.Replace(lead, @"\[\[([^\]\|]+)(\|[^\]]+)?\]\]", "$1");

            foreach (Match m in Regex.Matches(lead, @"\bfound in\b\s*(the\s+)?(?<loc>[A-Z][A-Za-z'\- ]{3,60})",
                                             RegexOptions.IgnoreCase))
            {
                var s = m.Groups["loc"].Value.Trim().TrimEnd('.', ',', ';');
                if (s.Length >= 3 && s.Length <= 60) list.Add(s);
            }

            return list;
        }

        private List<string> ExtractLinksFromSection(string wikitext, string sectionName)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(wikitext)) return list;

            var rx = new Regex(@"==\s*" + Regex.Escape(sectionName) + @"\s*==(.+?)(==|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            var m = rx.Match(wikitext);
            if (!m.Success) return list;

            string block = m.Groups[1].Value;

            foreach (Match link in Regex.Matches(block, @"\[\[([^\]\|]+)(\|[^\]]+)?\]\]"))
            {
                var s = link.Groups[1].Value.Trim();
                if (s.Length >= 3 && s.Length <= 60) list.Add(s);
            }

            return list;
        }

        private List<string> ExtractPlainLocationPhrases(string wikitext)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(wikitext)) return list;

            foreach (Match m in Regex.Matches(wikitext, @"found in (the )?([A-Z][A-Za-z' \-]{3,60})",
                                             RegexOptions.IgnoreCase))
            {
                var s = m.Groups[2].Value.Trim();
                if (s.Length >= 3 && s.Length <= 60) list.Add(s);
            }

            return list;
        }

        private string ExtractInfoboxMap(string wikitext)
        {
            if (string.IsNullOrWhiteSpace(wikitext)) return null;

            string TryField(params string[] keys)
            {
                foreach (var k in keys)
                {
                    var rx = new Regex(@"\|\s*" + Regex.Escape(k) + @"\s*=\s*([^\r\n\|]+)", RegexOptions.IgnoreCase);
                    var m = rx.Match(wikitext);
                    if (!m.Success) continue;

                    var raw = m.Groups[1].Value.Trim();
                    raw = Regex.Replace(raw, @"\[\[([^\]\|]+)(\|[^\]]+)?\]\]", "$1");
                    raw = Regex.Replace(raw, @"<.*?>", "");
                    raw = Regex.Replace(raw, @"\{\{.*?\}\}", "", RegexOptions.Singleline);

                    raw = raw.Split(new[] { "<br", "\n" }, StringSplitOptions.None)[0].Trim();

                    if (!string.IsNullOrWhiteSpace(raw))
                        return raw.Trim();
                }
                return null;
            }

            return TryField("map", "location", "zone", "region", "area");
        }

        private List<string> SplitMapLikeString(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(s)) return list;

            char[] seps = new[] { '(', ')', ',', ';', '/', '\\', '-', '|' };
            var parts = s.Split(seps, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                if (p.Length >= 3 && p.Length <= 60) list.Add(p);
            }

            return list;
        }

        private string CleanHint(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (Regex.IsMatch(s, @"^[a-z]{2}:", RegexOptions.IgnoreCase)) return null;

            s = s.Trim();
            s = Regex.Replace(s, @"\[\[([^\]\|]+)(\|[^\]]+)?\]\]", "$1").Trim();
            s = Regex.Replace(s, @"\{\{[^}]+\}\}", "").Trim();
            s = s.Replace(";", "|");
            return s;
        }

        // ---------------------- SCORING (fallback only) ----------------------

        private int ScoreCandidate(string candidateName, List<string> hints)
        {
            if (string.IsNullOrWhiteSpace(candidateName) || hints == null || hints.Count == 0) return 0;

            string c = candidateName.Trim();
            string cNorm = Normalize(c);

            int score = 0;
            if (cNorm.Contains("waypoint")) score += 60;

            string[] localityKeywords = { "village of", "town of", "city of", "hamlet of", "outpost of", "settlement of" };

            for (int i = 0; i < hints.Count; i++)
            {
                string hRaw = hints[i];
                if (string.IsNullOrWhiteSpace(hRaw)) continue;

                string h = hRaw.Trim();
                if (h.Length < 3) continue;

                int colon = h.IndexOf(':');
                if (colon > 0 && colon <= 3) continue;

                string hNorm = Normalize(h);

                if (hNorm == "the" || hNorm == "merchant" || hNorm == "npc") continue;

                if (string.Equals(cNorm, hNorm, StringComparison.OrdinalIgnoreCase))
                {
                    score += 220;
                    continue;
                }

                if (cNorm.Contains(hNorm) || hNorm.Contains(cNorm))
                    score += 120;

                for (int k = 0; k < localityKeywords.Length; k++)
                {
                    string key = localityKeywords[k];
                    if (hNorm.StartsWith(key))
                    {
                        string core = hNorm.Substring(key.Length).Trim();
                        if (core.Length >= 3 && (cNorm.Contains(core) || core.Contains(cNorm)))
                            score += 220;
                    }
                }

                var cTokens = Tokenize(cNorm);
                var hTokens = Tokenize(hNorm);

                int overlap = 0;
                foreach (var token in hTokens)
                    if (cTokens.Contains(token)) overlap++;

                score += overlap * 25;
            }

            if (c.IndexOf("Waypoint", StringComparison.OrdinalIgnoreCase) >= 0) score += 20;
            return score;

            string Normalize(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "";
                s = s.Trim().ToLowerInvariant();
                s = Regex.Replace(s, @"[^\p{L}\p{Nd}\s]+", " ");
                s = Regex.Replace(s, @"\s+", " ").Trim();
                return s;
            }

            HashSet<string> Tokenize(string s)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(s)) return set;

                var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    var t = parts[i].Trim();
                    if (t.Length < 3) continue;
                    if (t == "the" || t == "of" || t == "and" || t == "in" || t == "on") continue;
                    set.Add(t);
                }
                return set;
            }
        }
    }
}