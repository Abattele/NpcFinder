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


        private static readonly bool DEBUG_LOGS = false;




        private static readonly Logger Logger = Logger.GetLogger<NpcMerchantResolverService>();
        private readonly WikiNpcService _wiki;
        private readonly Gw2MapIndexService _mapIndex;
        private readonly Gw2ApiService _gw2;
        private readonly Gw2MapDetailsService _details;
        private readonly string _cacheDir;

        // waypointName -> mapId cache (avoid rescanning maps every time)
        private readonly Dictionary<string, int> _waypointToMapIdCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);


        // tweak how long to trust cached results
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(14);

        // ---------------------- inline classes ----------------------

        private sealed class LocationHint
        {
            public string Text;
            public int Weight;
            public string ScopeMap;
            public string Source;

            public override string ToString()
            {
                return $"{Text}(w={Weight},scope={(ScopeMap ?? "-")},src={(Source ?? "-")})";
            }
        }

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

        private static string TryGetSection(string all, string header)
        {
            if (string.IsNullOrWhiteSpace(all) || string.IsNullOrWhiteSpace(header)) return null;

            var rx = new Regex(
                @"(?is)^\s*==+\s*" + Regex.Escape(header) + @"\s*==+\s*(?<body>.*?)(^\s*==+|\z)",
                RegexOptions.Multiline);

            var m = rx.Match(all);
            if (!m.Success) return null;
            return m.Groups["body"].Value;
        }

        private IEnumerable<string> ExtractWaypointLinksFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;

            foreach (Match m in Regex.Matches(text, @"\[\[(?<t>[^\]|#]+)(?:#[^\]|]+)?(?:\|(?<d>[^\]]+))?\]\]"))
            {
                var d = m.Groups["d"].Success ? m.Groups["d"].Value : null;
                var t = m.Groups["t"].Success ? m.Groups["t"].Value : null;
                var s = (d ?? t ?? "").Trim();

                if (s.EndsWith(" Waypoint", StringComparison.OrdinalIgnoreCase) ||
                    s.EndsWith(" WP", StringComparison.OrdinalIgnoreCase))
                    yield return s;
            }
        }

        public async Task<List<NpcResolvedHit>> ResolveMerchantAsync(string npcTitle, CancellationToken ct)
        {
            // CACHE FIRST
            List<NpcResolvedHit> cached;
            if (TryLoadCachedResolvedHits(npcTitle, out cached))
            {
                if (DEBUG_LOGS)
                    Logger.Debug("[MerchantResolve] CACHE HIT title='" + npcTitle + "' hits=" + cached.Count);

                return cached;
            }

            // NORMAL FLOW
            var wikiRes = await _wiki.ResolveByTitleAsync(npcTitle, ct).ConfigureAwait(false);
            if (wikiRes == null) return new List<NpcResolvedHit>();

            string wikitext = wikiRes.Wikitext ?? "";

            // weighted + scoped hints
            var hints = BuildLocationHintsWeighted(npcTitle, wikitext);

            if (DEBUG_LOGS)
            {
                Logger.Debug("[MerchantResolve] title='" + npcTitle + "' weightedHints=(" + hints.Count + ") " +
                             string.Join(" | ", hints.Take(12)));
            }

            // resolve ALL possible mapIds (use best map-name candidates from hints)
            var mapNameCandidates = BuildMapNameCandidatesFromHints(hints);
            var mapIds = await ResolveAllMapIdsFromHintsAsync(mapNameCandidates, ct).ConfigureAwait(false);


            if (mapIds.Count == 0)
            {
                // RARE FALLBACK: only if map-name resolution failed completely.
                var wpCandidates = BuildWaypointCandidatesFromHints(hints);

                for (int i = 0; i < wpCandidates.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    int? wpMapId = await ResolveMapIdByWaypointNameAsync(wpCandidates[i], ct).ConfigureAwait(false);
                    if (wpMapId.HasValue)
                        mapIds.Add(wpMapId.Value);
                }

                // still nothing -> then can't locate it.
                if (mapIds.Count == 0)
                {
                    if (DEBUG_LOGS)
                        Logger.Warn("[MerchantResolve] could not resolve any mapId from hints (including waypoint fallback).");

                    return new List<NpcResolvedHit>();
                }
            }


            // extract NPC coordinates from wikitext
            var coordHints = ExtractNpcCoordinates(wikitext);

            if (DEBUG_LOGS && coordHints.Count > 0)
            {
                Logger.Debug("[MerchantResolve] extracted " + coordHints.Count + " coord hint(s): " +
                             string.Join(" | ", coordHints.Take(5).Select(c => "[" + c.x + "," + c.y + "] map='" + (c.mapName ?? "") + "'")));
            }

            var hits = new List<NpcResolvedHit>();

            for (int mi = 0; mi < mapIds.Count; mi++)
            {
                int mapId = mapIds[mi];
                ct.ThrowIfCancellationRequested();

                var mapInfo = await _gw2.GetMapInfoAsync(mapId, ct).ConfigureAwait(false);
                if (mapInfo == null)
                {
                    if (DEBUG_LOGS)
                        Logger.Warn("[MerchantResolve] mapInfo null for mapId=" + mapId);
                    continue;
                }

                if (DEBUG_LOGS)
                    Logger.Info("[MapInfo] mapId=" + mapId + " name='" + mapInfo.Name + "' continentId=" + mapInfo.ContinentId + " defaultFloor=" + mapInfo.DefaultFloor);

                PoiWpFloorResult anchors = await _details.GetPoisAndWaypointsWithFloorFallbackAsync(
                    mapInfo.ContinentId,
                    mapInfo.DefaultFloor,
                    mapInfo.Floors,
                    mapInfo.Id,
                    mapInfo.RegionId,
                    ct
                ).ConfigureAwait(false);

                if (DEBUG_LOGS)
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

                // try NPC coords first, else fallback to WP/POI scoring.
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

                // fallback: weighted name scoring algorithm
                if (best == null)
                {
                    var candidates = new List<ScoredCandidate>();

                    // build strong "place terms" for THIS map.
                    

                    // 1/ prefer scoped leaf hints (Place/SynthWaypoint) under this map.
                    // 2/ If none exist, promote LocationsTree:Map terms (scope-less) into place terms.
                    // this fixes cases where a sub-location appears as a Map node.

                    var strongPlaceTerms = new List<string>();

                    // 0/ If we have scoped high-weight hints for THIS map, use them as strongPlaceTerms (best signal)
                    strongPlaceTerms.AddRange(
                        hints.Where(h => h != null
                                         && !string.IsNullOrWhiteSpace(h.ScopeMap)
                                         && string.Equals(h.ScopeMap, mapInfo.Name, StringComparison.OrdinalIgnoreCase)
                                         && h.Weight >= 400)
                             .Select(h => h.Text)
                    );


                    strongPlaceTerms.AddRange(
                        hints.Where(h => h != null
                                         && !string.IsNullOrWhiteSpace(h.ScopeMap)
                                         && string.Equals(h.ScopeMap, mapInfo.Name, StringComparison.OrdinalIgnoreCase)
                                         && h.Source != null
                                         && (h.Source.StartsWith("LocationsTree:Place", StringComparison.OrdinalIgnoreCase)
                                             || h.Source.StartsWith("LocationsTree:SynthWaypoint", StringComparison.OrdinalIgnoreCase))
                                         && h.Weight >= 400)
                             .Select(h => h.Text)
                    );


                    if (strongPlaceTerms.Count == 0)
                    {
                        var mapNodes = hints
                            .Where(h => h != null
                                        && h.Source != null
                                        && h.Source.StartsWith("LocationsTree:Map", StringComparison.OrdinalIgnoreCase)
                                        && h.Weight >= 200
                                        && !string.IsNullOrWhiteSpace(h.Text)
                                        && !string.Equals(h.Text, mapInfo.Name, StringComparison.OrdinalIgnoreCase))
                            .Select(h => h.Text.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        
                        var evidence = hints
                            .Where(h => h != null
                                        && !string.IsNullOrWhiteSpace(h.Text)
                                        && (h.Source == null || !h.Source.StartsWith("LocationsTree:", StringComparison.OrdinalIgnoreCase)))
                            .Select(h => h.Text)
                            .ToList();

                        bool isMemoryMap = IsMemoryMapName(mapInfo.Name);

                        var memoryTagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        for (int k = 0; k < mapNodes.Count; k++)
                        {
                            string node = mapNodes[k];
                            for (int e = 0; e < evidence.Count; e++)
                            {
                                var ev = evidence[e];
                                if (ev.IndexOf(node, StringComparison.OrdinalIgnoreCase) < 0) continue;

                                if (ev.IndexOf("Memory of", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    ev.IndexOf("(", StringComparison.OrdinalIgnoreCase) >= 0 && ev.IndexOf("Memory of", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    memoryTagged.Add(node);
                                    break;
                                }
                            }
                        }

                        if (isMemoryMap)
                        {
                            // on memory maps, prefer memory-tagged nodes
                            for (int k = 0; k < mapNodes.Count; k++)
                            {
                                string node = mapNodes[k];
                                if (memoryTagged.Contains(node))
                                    strongPlaceTerms.Add(node);
                            }
                        }
                        else
                        {
                            // on non-memory maps:

                            // if exactly one node is memory-tagged, prefer the other node.
                            if (memoryTagged.Count == 1 && mapNodes.Count >= 1)
                            {
                                for (int k = 0; k < mapNodes.Count; k++)
                                {
                                    string node = mapNodes[k];
                                    if (!memoryTagged.Contains(node))
                                        strongPlaceTerms.Add(node);
                                }
                            }
                            else
                            {
                                // fallback: do nothing (avoid cross-map contamination !!! very important here)
                            }
                        }
                    }


                    strongPlaceTerms = strongPlaceTerms
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();


                    if (DEBUG_LOGS)
                        Logger.Debug("[MerchantResolve] strongPlaceTerms map=" + mapInfo.Name + " => " + string.Join(", ", strongPlaceTerms));


                    // -------- WAYPOINTS -------- 

                    for (int i = 0; i < anchors.Waypoints.Count; i++)
                    {
                        var w = anchors.Waypoints[i];

                        int s = ScoreCandidateWeighted(w.Item1, hints, mapInfo.Name);


                        // deterministic boost if wp name matches strong place terms for this map.
                        if (strongPlaceTerms.Count > 0 && ContainsAny(w.Item1, strongPlaceTerms))
                            s += 900;

                        if (s > 0)
                            candidates.Add(new ScoredCandidate("Waypoint", w.Item1, w.Item2, w.Item3, s));
                    }

                    // -------- POIS --------
                    for (int i = 0; i < anchors.Pois.Count; i++)
                    {
                        var p = anchors.Pois[i];

                        int s = ScoreCandidateWeighted(p.Item1, hints, mapInfo.Name);

                        // don't allow "Old ..." POIs to hijack real maps.
                        // if we're on a non-memory map and the POI starts with "Old ", crush it even if strongPlaceTerms is empty.
                        // had this problem with Old Lion's Arch where it would send me under water instead of prefering a wp for NPC Alainn
                        if (!IsMemoryMapName(mapInfo.Name) && IsOldPrefixed(p.Item1))
                            s = (int)(s * 0.05);
                        else if (strongPlaceTerms.Count > 0 && IsOldPrefixed(p.Item1))
                            s = (int)(s * 0.10);


                        // tiny bonus if POI matches strong place terms (keeps POIs relevant but not dominant)
                        if (strongPlaceTerms.Count > 0 && ContainsAny(p.Item1, strongPlaceTerms))
                            s += 80;

                        if (s > 0)
                            candidates.Add(new ScoredCandidate("POI", p.Item1, p.Item2, p.Item3, s));
                    }

                    if (DEBUG_LOGS)
                    {
                        var top = candidates.OrderByDescending(x => x.Score).Take(5).ToList();
                        Logger.Warn("[MerchantResolve] TOP candidates map=" + mapInfo.Name + " => " +
                            string.Join(" | ", top.Select(x => x.Kind + ":" + x.Name + "(s=" + x.Score + ")")));
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
                        .ThenBy(c => KindRank(c.Kind))
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

                // these are already continent coords from /continents/.../maps/{mapId}
                double cx = best.X;
                double cy = best.Y;

                if (DEBUG_LOGS)
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

            // prefer real map over "Memory of ...", then alpha, then Waypoint before POI
            // had this for an instance/story map
            hits = hits
                .OrderBy(h => IsMemoryMapName(h.MapName) ? 1 : 0)
                .ThenBy(h => h.MapName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Source.StartsWith("Waypoint:", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();

            if (hits.Count == 0 && DEBUG_LOGS)
                Logger.Warn("[MerchantResolve] No hits produced.");

            // SAVE CACHE
            SaveCachedResolvedHits(npcTitle, hits);

            return hits;
        }

        private static int KindRank(string kind)
        {
            // lower is better
            if (kind.Equals("Waypoint", StringComparison.OrdinalIgnoreCase)) return 0;
            if (kind.Equals("POI", StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }

        // ---------------------- DISK CACHE ----------------------

        private void EnsureCacheDir()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_cacheDir))
                    Directory.CreateDirectory(_cacheDir);
            }
            catch { }
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

                // TTL
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
                Logger.Warn("Exception [MerchantResolve] cache read failed: " + ex.Message);
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

                if (DEBUG_LOGS)
                    Logger.Debug("[MerchantResolve] cache write OK path=" + path);
            }
            catch (Exception ex)
            {
                Logger.Warn("Exception [MerchantResolve] cache write failed: " + ex.Message);
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

                string nTrim = (name ?? "").Trim();

                if (string.Equals(nTrim, "the", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nTrim, "a", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nTrim, "an", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nTrim, "merchant", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nTrim, "npc", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nTrim, "bandit", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nTrim, "scout", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nTrim, "animal", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nTrim, "farmer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nTrim, "watchman", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }


                int? id = await _mapIndex.ResolveMapIdByNameAsync(name, ct).ConfigureAwait(false);

                if (!id.HasValue)
                {
                    // WAYPOINT -> MAPID fallback (only if the candidate really looks like a waypoint)
                    bool looksLikeWaypoint =
                        name.EndsWith(" Waypoint", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(" WP", StringComparison.OrdinalIgnoreCase);

                    if (looksLikeWaypoint)
                        id = await ResolveMapIdByWaypointNameAsync(name, ct).ConfigureAwait(false);
                }

                if (DEBUG_LOGS)
                    Logger.Debug("[MerchantResolve] try mapName='" + name + "' => mapId=" + (id.HasValue ? id.Value.ToString() : "null"));

                if (id.HasValue)
                    ids.Add(id.Value);


                if (ids.Count >= 15) break;
            }

            return ids.ToList();
        }

        private List<string> BuildMapNameCandidatesFromHints(List<LocationHint> hints)
        {
            if (hints == null || hints.Count == 0) return new List<string>();

            // prefer high-weight items first; include ScopeMap too
            var items = new List<(string text, int weight)>();

            for (int i = 0; i < hints.Count; i++)
            {
                var h = hints[i];
                if (h == null) continue;

                if (!string.IsNullOrWhiteSpace(h.ScopeMap))
                    items.Add((h.ScopeMap, h.Weight + 80));

                if (!string.IsNullOrWhiteSpace(h.Text))
                    items.Add((h.Text, h.Weight));
            }

            items = items
                .OrderByDescending(x => x.weight)
                .ToList();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            for (int i = 0; i < items.Count; i++)
            {
                var s = CleanHint(items[i].text);
                if (string.IsNullOrWhiteSpace(s)) continue;

                if (s.Length < 3 || s.Length > 60) continue;

                // map-name candidates must be map-like.
                // if we allow "Waypoint" strings in here, the fallback may trigger a lot of expensive scanning.
                if (s.IndexOf("Waypoint", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (s.EndsWith(" WP", StringComparison.OrdinalIgnoreCase)) continue;
                if (s.IndexOf("POI", StringComparison.OrdinalIgnoreCase) >= 0) continue;


                if (seen.Add(s))
                    result.Add(s);

                if (result.Count >= 80) break;
            }

            return result;
        }

        // ---------------------- COORD EXTRACTION ----------------------
        private async Task<int?> ResolveMapIdByWaypointNameAsync(string waypointName, CancellationToken ct)
        {
            waypointName = CleanHint(waypointName);
            if (string.IsNullOrWhiteSpace(waypointName)) return null;

            int cached;
            if (_waypointToMapIdCache.TryGetValue(waypointName, out cached) && cached > 0)
                return cached;

            // get all map ids from the index (cached by Gw2MapIndexService)
            var allMapIds = await _mapIndex.GetAllKnownMapIdsAsync(ct).ConfigureAwait(false);
            if (allMapIds == null || allMapIds.Count == 0) return null;

            // safety cap
            int scanCap = Math.Min(allMapIds.Count, 120); // from testing i saw 120 is OK for optimal seek and results

            for (int i = 0; i < scanCap; i++)
            {
                ct.ThrowIfCancellationRequested();

                int mapId = allMapIds[i];

                var mapInfo = await _gw2.GetMapInfoAsync(mapId, ct).ConfigureAwait(false);
                if (mapInfo == null) continue;

                var anchors = await _details.GetPoisAndWaypointsWithFloorFallbackAsync(
                    mapInfo.ContinentId,
                    mapInfo.DefaultFloor,
                    mapInfo.Floors,
                    mapInfo.Id,
                    mapInfo.RegionId,
                    ct
                ).ConfigureAwait(false);

                for (int w = 0; w < anchors.Waypoints.Count; w++)
                {
                    if (string.Equals(anchors.Waypoints[w].Item1, waypointName, StringComparison.OrdinalIgnoreCase))
                    {
                        _waypointToMapIdCache[waypointName] = mapId;

                        if (DEBUG_LOGS)
                            Logger.Debug("[MerchantResolve] waypoint->mapId '" + waypointName + "' => " + mapId);

                        return mapId;
                    }
                }
            }

            if (DEBUG_LOGS)
                Logger.Debug("[MerchantResolve] waypoint->mapId NOT FOUND '" + waypointName + "' (scanned " + scanCap + " maps)");

            return null;
        }



        private List<string> BuildWaypointCandidatesFromHints(List<LocationHint> hints)
        {
            if (hints == null || hints.Count == 0) return new List<string>();

            // prefer the synthetic waypoints we created from the Locations tree (best signal)
            // then anything else that already looks like a waypoint
            var items = new List<(string text, int weight)>();

            for (int i = 0; i < hints.Count; i++)
            {
                var h = hints[i];
                if (h == null) continue;
                if (string.IsNullOrWhiteSpace(h.Text)) continue;

                bool isSynthWp = h.Source != null &&
                                 h.Source.StartsWith("LocationsTree:SynthWaypoint", StringComparison.OrdinalIgnoreCase);

                bool looksLikeWp =
                    h.Text.EndsWith(" Waypoint", StringComparison.OrdinalIgnoreCase) ||
                    h.Text.EndsWith(" WP", StringComparison.OrdinalIgnoreCase);

                if (isSynthWp || looksLikeWp)
                    items.Add((h.Text, h.Weight));
            }

            return items
                .OrderByDescending(x => x.weight)
                .Select(x => CleanHint(x.text))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)   // keep small
                .ToList();
        }



        private List<(string mapName, int x, int y)> ExtractNpcCoordinates(string wikitext)
        {
            var list = new List<(string mapName, int x, int y)>();
            if (string.IsNullOrWhiteSpace(wikitext)) return list;

            // NPC infobox: | coordinates = [43696, 28628]
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

        // ---------------------- HINT PIPELINE (weighted + scoped) ----------------------

        // if infobox location is : "Fort Marriner| Sanctum Harbor (Memory of Old Lion's Arch)"
        // interpret as: Fort Marriner @ Lion's Arch, Sanctum Harbor @ Memory of Old Lion's Arch
        private void AddInfoboxSplitMemoryHints(List<LocationHint> hints, string infoboxLocationRaw)
        {
            if (string.IsNullOrWhiteSpace(infoboxLocationRaw)) return;

            // normalize
            string raw = infoboxLocationRaw.Trim();

            // must contain "|" and "(Memory of"
            int pipe = raw.IndexOf('|');
            int memIdx = raw.IndexOf("(Memory of", StringComparison.OrdinalIgnoreCase);
            if (pipe < 0 || memIdx < 0) return;

            string left = raw.Substring(0, pipe).Trim(); // Fort Marriner
            string rightPlus = raw.Substring(pipe + 1).Trim(); // Sanctum Harbor (Memory of Old Lion's Arch)

            // extract memory map name inside parentheses
            int open = rightPlus.IndexOf('(');
            int close = rightPlus.LastIndexOf(')');
            if (open < 0 || close <= open) return;

            string right = rightPlus.Substring(0, open).Trim(); // Sanctum Harbor
            string paren = rightPlus.Substring(open + 1, close - open - 1).Trim(); // "Memory of Old Lion's Arch"

            // paren might be "Memory of Old Lion's Arch"
            string memoryMap = paren;


            // big optimisation : 
            // if we already have a resolved map "Lion's Arch" in the page hints, that’s the default.
            // otherwise leave scope null and it won't dominate.
            // try to infer the "main map" from the LocationsTree:Map nodes already collected.
            // prefer a non-memory map node if present.
            string mainMap = null;

            for (int i = 0; i < hints.Count; i++)
            {
                var h = hints[i];
                if (h == null) continue;
                if (h.Source == null) continue;
                if (!h.Source.StartsWith("LocationsTree:Map", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(h.Text)) continue;

                // prefer non-memory map nodes as main map
                if (!IsMemoryMapName(h.Text))
                {
                    mainMap = h.Text.Trim();
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(left))
                hints.Add(new LocationHint { Text = left, Weight = 500, ScopeMap = mainMap, Source = "Infobox:Scoped" });

            if (!string.IsNullOrWhiteSpace(right))
                hints.Add(new LocationHint { Text = right, Weight = 500, ScopeMap = memoryMap, Source = "Infobox:Scoped" });
        }

        private List<LocationHint> BuildLocationHintsWeighted(string title, string wikitext)
        {
            var hints = new List<LocationHint>();
            if (string.IsNullOrWhiteSpace(wikitext)) return hints;

            // strip historical section
            string pruned = StripSection(wikitext, "Historical locations");
            pruned = StripSection(pruned, "Historic locations");

            // 1/ locations tree (strongest signal)
            var tree = ExtractLocationsTree(pruned);

            for (int i = 0; i < tree.Count; i++)
            {
                var map = tree[i].map;
                var place = tree[i].place;

                // IMPORTANT:
                // map hints should not be scoped to themselves.
                if (!string.IsNullOrWhiteSpace(map))
                    hints.Add(new LocationHint { Text = map, Weight = 240, ScopeMap = null, Source = "LocationsTree:Map" });

                // place hints are scoped to their parent map.
                if (!string.IsNullOrWhiteSpace(place))
                {
                    hints.Add(new LocationHint { Text = place, Weight = 520, ScopeMap = map, Source = "LocationsTree:Place" });

                    // synthetic: helps match actual candidates like "Fort Marriner Waypoint"
                    hints.Add(new LocationHint { Text = place + " Waypoint", Weight = 430, ScopeMap = map, Source = "LocationsTree:SynthWaypoint" });
                    hints.Add(new LocationHint { Text = place + " WP", Weight = 320, ScopeMap = map, Source = "LocationsTree:SynthWaypoint" });
                }
            }

            // 2/ Infobox map/location fields
            var infobox = ExtractInfoboxMap(pruned);
            if (!string.IsNullOrWhiteSpace(infobox))
            {
                // first: add scoped hints if it matches the special split format
                AddInfoboxSplitMemoryHints(hints, infobox);

                // after: keep the raw infobox hint (low weight) as a general map-name hint
                hints.Add(new LocationHint { Text = infobox, Weight = 120, ScopeMap = null, Source = "Infobox" });
            }


            // 3/ location section links (moderate)
            var locLinks = ExtractLinksFromSection(pruned, "Location");
            for (int i = 0; i < locLinks.Count; i++)
                hints.Add(new LocationHint { Text = locLinks[i], Weight = 170, ScopeMap = null, Source = "Section:Location" });


            // 3b/ strong waypoint links explicitly mentioned inside the location section

            // some pages recommend a specific waypoint ("Rally Waypoint") but it may not end up
            // as a strong hint unless we extract and boost it directly.
            var locSection = TryGetSection(pruned, "Location");
            foreach (var wp in ExtractWaypointLinksFromText(locSection))
            {
                hints.Add(new LocationHint
                {
                    Text = wp,
                    Weight = 420,
                    ScopeMap = null,
                    Source = "Section:Location:WaypointLink"
                });
            }


            // 4/ lead "found in ..." phrases (moderate)
            var lead = ExtractLeadSentenceLocation(pruned);
            for (int i = 0; i < lead.Count; i++)
                hints.Add(new LocationHint { Text = lead[i], Weight = 150, ScopeMap = null, Source = "Lead" });

            // 5/ plain phrases (weak)
            var phrases = ExtractPlainLocationPhrases(pruned);
            for (int i = 0; i < phrases.Count; i++)
                hints.Add(new LocationHint { Text = phrases[i], Weight = 120, ScopeMap = null, Source = "Phrases" });

            // 6/ all links (weakest)
            var allLinks = ExtractAllLinks(pruned);
            for (int i = 0; i < allLinks.Count; i++)
                hints.Add(new LocationHint { Text = allLinks[i], Weight = 40, ScopeMap = null, Source = "AllLinks" });

            // clean + dedupe (keep strongest weight)
            var dict = new Dictionary<string, LocationHint>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < hints.Count; i++)
            {
                var h = hints[i];
                if (h == null) continue;

                h.Text = CleanHint(h.Text);
                h.ScopeMap = CleanHint(h.ScopeMap);

                if (string.IsNullOrWhiteSpace(h.Text)) continue;
                if (h.Text.Length < 3 || h.Text.Length > 80) continue;

                // drop language links etc
                if (Regex.IsMatch(h.Text, @"^[a-z]{2}:", RegexOptions.IgnoreCase)) continue;

                // key should include scope (since "Fort Marriner" under one map differs from another map)
                string k = (h.ScopeMap ?? "") + "||" + h.Text;

                if (!dict.TryGetValue(k, out var existing))
                {
                    dict[k] = h;
                }
                else
                {
                    if (h.Weight > existing.Weight)
                        dict[k] = h;
                }
            }

            var final = dict.Values
                .OrderByDescending(x => x.Weight)
                .ThenBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
                .Take(220)
                .ToList();

            return final;
        }

        private static string StripSection(string wikitext, string sectionTitle)
        {
            if (string.IsNullOrWhiteSpace(wikitext) || string.IsNullOrWhiteSpace(sectionTitle))
                return wikitext;

            // remove "== Historical locations ==" ... until next "=="
            var rx = new Regex(@"==\s*" + Regex.Escape(sectionTitle) + @"\s*==(.+?)(?:(\r?\n)==|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            return rx.Replace(wikitext, "\n");
        }


        private List<(string map, string place)> ExtractLocationsTree(string wikitext)
        {
            var res = new List<(string map, string place)>();
            if (string.IsNullOrWhiteSpace(wikitext)) return res;

            string section = TryGetSection(wikitext, "Locations") ?? wikitext;
            var lines = section.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // detect max bullet depth used
            int maxDepth = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                var mm = Regex.Match(lines[i] ?? "", @"^\s*(?<bul>[\*\#]+)\s*(?<rest>.+)$");
                if (!mm.Success) continue;
                maxDepth = Math.Max(maxDepth, mm.Groups["bul"].Value.Length);
            }

            bool regionMode = maxDepth >= 3;

            string region = null;
            string map = null;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var mm = Regex.Match(line, @"^\s*(?<bul>[\*\#]+)\s*(?<rest>.+)$");
                if (!mm.Success) continue;

                int depth = mm.Groups["bul"].Value.Length;
                string content = mm.Groups["rest"].Value;

                string text = ExtractFirstLinkText(content);
                if (string.IsNullOrWhiteSpace(text))
                    text = StripWikiMarkup(content);

                text = CleanHint(text);
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (!regionMode)
                {
                    // 2-level mode:
                    // * map
                    // ** place
                    if (depth == 1)
                    {
                        map = text;
                        res.Add((map, null));
                    }
                    else if (depth >= 2)
                    {
                        if (!string.IsNullOrWhiteSpace(map))
                            res.Add((map, text));
                    }
                }
                else
                {
                    // 3-level mode:
                    // * region
                    // ** map
                    // *** place
                    if (depth == 1)
                    {
                        region = text;
                        map = null;
                    }
                    else if (depth == 2)
                    {
                        map = text;
                        res.Add((map, null));
                    }
                    else if (depth >= 3)
                    {
                        if (!string.IsNullOrWhiteSpace(map))
                            res.Add((map, text));
                        else if (!string.IsNullOrWhiteSpace(region))
                            res.Add((region, text));
                    }
                }
            }

            return Dedup(res);

            string ExtractFirstLinkText(string s)
            {
                var m = Regex.Match(s, @"\[\[(?<t>[^\]|#]+)(?:#[^\]|]+)?(?:\|(?<d>[^\]]+))?\]\]");
                if (!m.Success) return null;
                var d = m.Groups["d"].Success ? m.Groups["d"].Value : null;
                var t = m.Groups["t"].Success ? m.Groups["t"].Value : null;
                return (d ?? t ?? "").Trim();
            }

            string StripWikiMarkup(string s)
            {
                s = Regex.Replace(s, @"\{\{.*?\}\}", "", RegexOptions.Singleline);
                s = Regex.Replace(s, @"\[\[|\]\]", "");
                return s.Trim();
            }

            string TryGetSection(string all, string header)
            {
                var rx = new Regex(
                    @"(?is)^\s*==+\s*" + Regex.Escape(header) + @"\s*==+\s*(?<body>.*?)(^\s*==+|\z)",
                    RegexOptions.Multiline);

                var m = rx.Match(all);
                if (!m.Success) return null;
                return m.Groups["body"].Value;
            }

            List<(string map, string place)> Dedup(List<(string map, string place)> list)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var outList = new List<(string map, string place)>();
                for (int k = 0; k < list.Count; k++)
                {
                    string m = CleanHint(list[k].map);
                    string p = CleanHint(list[k].place);
                    string key = (m ?? "") + "||" + (p ?? "");
                    if (seen.Add(key))
                        outList.Add((m, p));
                }
                return outList;
            }
        }



        // ---------------------- i kept the older helpers ----------------------

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
                    var rx = new Regex(@"\|\s*" + Regex.Escape(k) + @"\s*=\s*(?<v>[^\r\n]+)",
                                       RegexOptions.IgnoreCase);
                    var m = rx.Match(wikitext);
                    if (!m.Success) continue;

                    var raw = m.Groups["v"].Value.Trim();

                    // keep '|' intact (we NEED it for split), but clean markup
                    raw = Regex.Replace(raw, @"\[\[([^\]\|]+)(\|[^\]]+)?\]\]", "$1");
                    raw = Regex.Replace(raw, @"<.*?>", "");
                    raw = Regex.Replace(raw, @"\{\{.*?\}\}", "", RegexOptions.Singleline);

                    // IMPORTANT: do NOT truncate at <br> — we need "(Crystal Oasis)" etc.
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
            s = Regex.Replace(s, @"<.*?>", "").Trim();
            s = s.Replace(";", "|");
            return s.Trim();
        }

        // ---------------------- WEIGHTED SCORING ----------------------

        private int ScoreCandidateWeighted(string candidateName, List<LocationHint> hints, string currentMapName)
        {
            if (string.IsNullOrWhiteSpace(candidateName) || hints == null || hints.Count == 0) return 0;

            string cand = candidateName.Trim();
            string candNorm = Normalize(cand);

            int score = 0;

            // waypoint bias
            if (candNorm.Contains("waypoint")) score += 80;

            // slight penalty for overly-generic candidates
            // if (candNorm.StartsWith("old ")) score -= 30;

            for (int i = 0; i < hints.Count; i++)
            {
                var h = hints[i];
                if (h == null || string.IsNullOrWhiteSpace(h.Text)) continue;

                // scope: if hint is scoped to a map and it doesn't match this map -> downweight hard
                bool scopeMismatch = !string.IsNullOrWhiteSpace(h.ScopeMap)
                                     && !string.Equals(h.ScopeMap, currentMapName, StringComparison.OrdinalIgnoreCase);

                int w = h.Weight;
                if (scopeMismatch) w = (int)(w * 0.15);

                string hNorm = Normalize(h.Text);

                if (hNorm.Length < 3) continue;
                if (hNorm == "the" || hNorm == "merchant" || hNorm == "npc") continue;

                // exact / contains matches
                if (string.Equals(candNorm, hNorm, StringComparison.OrdinalIgnoreCase))
                {
                    score += w;
                    continue;
                }

                if (candNorm.Contains(hNorm) || hNorm.Contains(candNorm))
                    score += (int)(w * 0.55);

                // token overlap
                var cTokens = Tokenize(candNorm);
                var hTokens = Tokenize(hNorm);

                int overlap = 0;
                foreach (var token in hTokens)
                    if (cTokens.Contains(token)) overlap++;

                if (overlap > 0)
                    score += overlap * Math.Max(12, (int)(w * 0.06));
            }

            // ensure non-negative
            if (score < 0) score = 0;
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


        private static bool ContainsAny(string text, List<string> terms)
        {
            if (string.IsNullOrWhiteSpace(text) || terms == null || terms.Count == 0) return false;

            string norm = NormalizeForContains(text);

            for (int i = 0; i < terms.Count; i++)
            {
                var t = terms[i];
                if (string.IsNullOrWhiteSpace(t)) continue;

                string tn = NormalizeForContains(t);
                if (tn.Length < 3) continue;

                if (norm.Contains(tn))
                    return true;
            }

            return false;
        }

        private static string NormalizeForContains(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"[^\p{L}\p{Nd}\s]+", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }



        private static bool IsOldPrefixed(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.TrimStart().StartsWith("Old ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMemoryMapName(string mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName)) return false;
            return mapName.TrimStart().StartsWith("Memory of", StringComparison.OrdinalIgnoreCase);
        }
    }
}
