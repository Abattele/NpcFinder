using Blish_HUD;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NpcFinder.Models;

namespace NpcFinder.Services
{
    public class Gw2MapDetailsService
    {


        private static readonly bool DEBUG_LOGS = false;


        private static readonly Logger Logger = Logger.GetLogger<Gw2MapDetailsService>();
        private const string CACHE_VER = "v5";
        private static readonly HttpClient Http = new HttpClient();
        private readonly CacheStore _cache;

        private class MapDetailsCache
        {
            public List<Tuple<string, int, int>> Pois { get; set; } = new List<Tuple<string, int, int>>();
            public List<Tuple<string, int, int>> Waypoints { get; set; } = new List<Tuple<string, int, int>>();
        }

        public Gw2MapDetailsService(CacheStore cache)
        {
            _cache = cache;

            try
            {
                if (!Http.DefaultRequestHeaders.UserAgent.ToString().Contains("NpcFinder-BlishHUD"))
                    Http.DefaultRequestHeaders.UserAgent.ParseAdd("NpcFinder-BlishHUD");
            }
            catch { }
        }

        private async Task<PoiWpFloorResult> TryContinentFloorsAsync(int continentId, int defaultFloorId, int[] preferredFloors, int mapId, int regionIdHint, CancellationToken ct)
        {

            var floorsToTry = new List<int>();
            AddUniqueFloors(floorsToTry, preferredFloors);

            if (defaultFloorId >= 0) AddUniqueInt(floorsToTry, defaultFloorId);

            AddUniqueInt(floorsToTry, 1);
            AddUniqueInt(floorsToTry, 0);

            if (floorsToTry.Count < 6)
            {
                var discovered = await GetContinentFloorsAsync(continentId, ct).ConfigureAwait(false);

                for (int i = 0; i < discovered.Count && floorsToTry.Count < 6; i++)
                {
                    int f = discovered[i];

                    // allow negative floors (e.g. -2), and floors 0..3 only
                    if (f <= 3 || f < 0)
                        AddUniqueInt(floorsToTry, f);
                }
            }


            if (DEBUG_LOGS)
                Logger.Debug($"[MapDetails] mapId={mapId} trying continent={continentId} floors=[{string.Join(",", floorsToTry)}] regionIdHint={regionIdHint}");

            // 1/ FAST ONLY (no scanning).
            for (int fi = 0; fi < floorsToTry.Count; fi++)
            {
                int floor = floorsToTry[fi];
                ct.ThrowIfCancellationRequested();

                var data = await GetPoisAndWaypointsByRegionAsync(
                    continentId, floor, mapId, regionIdHint, allowScan: false, ct).ConfigureAwait(false);

                if (data != null)
                {
                    return new PoiWpFloorResult
                    {
                        UsedFloor = floor,
                        Pois = data.Item1,
                        Waypoints = data.Item2
                    };
                }
            }

            // 2/ SLOW (scan if needed). only happens if 1/ couldn't find anything
            for (int fi = 0; fi < floorsToTry.Count; fi++)
            {
                int floor = floorsToTry[fi];
                ct.ThrowIfCancellationRequested();

                var data = await GetPoisAndWaypointsByRegionAsync(
                    continentId, floor, mapId, regionIdHint, allowScan: true, ct).ConfigureAwait(false);

                if (data != null)
                {
                    return new PoiWpFloorResult
                    {
                        UsedFloor = floor,
                        Pois = data.Item1,
                        Waypoints = data.Item2
                    };
                }
            }

            return null;
        }
        public async Task<PoiWpFloorResult> GetPoisAndWaypointsWithFloorFallbackAsync(int continentId, int defaultFloorId, int[] preferredFloors, int mapId, int regionId, CancellationToken ct)
        {

            // 1/ try ONLY the provided continent first
            var primary = await TryContinentFloorsAsync(continentId, defaultFloorId, preferredFloors, mapId, regionId, ct)
                .ConfigureAwait(false);

            if (primary != null)
                return primary;

            // 2/ only if that failed completely, then try other continents as a fallback
            var allContinents = await GetAllContinentsAsync(ct).ConfigureAwait(false);

            for (int i = 0; i < allContinents.Count; i++)
            {
                int cTry = allContinents[i];
                if (cTry == continentId) continue;

                var fallback = await TryContinentFloorsAsync(cTry, defaultFloorId, preferredFloors, mapId, regionId, ct)
                    .ConfigureAwait(false);

                if (fallback != null)
                    return fallback;
            }

            // failure:
            return new PoiWpFloorResult { UsedFloor = -1 };
        }


        private async Task<Tuple<List<Tuple<string, int, int>>, List<Tuple<string, int, int>>>> GetPoisAndWaypointsByRegionAsync(
    int continentId, int floorId, int mapId, int regionIdFromMapInfo, bool allowScan, CancellationToken ct)
        {

            int regionId = regionIdFromMapInfo;

            // try mapdetails cache first for the hinted region
            if (regionId > 0)
            {
                string hintedMapDetailsKey = $"mapdetails-c{continentId}-f{floorId}-r{regionId}-m{mapId}-{CACHE_VER}";
                MapDetailsCache hintedCached;
                if (_cache.TryLoad(hintedMapDetailsKey, out hintedCached) && hintedCached != null)
                {
                    return Tuple.Create(hintedCached.Pois, hintedCached.Waypoints);
                }
            }

            // if we have a hint, try it first
            if (regionId > 0)
            {
                var url = $"https://api.guildwars2.com/v2/continents/{continentId}/floors/{floorId}/regions/{regionId}/maps/{mapId}";

                if (DEBUG_LOGS) Logger.Debug($"[MapDetails] HTTP GET {url}");

                using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    if (DEBUG_LOGS) Logger.Debug($"[MapDetails] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

                    if (resp.IsSuccessStatusCode)
                    {
                        string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return ParseAndCache(continentId, floorId, regionId, mapId, json);
                    }

                    // if we're in fast-only mode, don't scan.
                    if (!allowScan)
                        return null;

                    if ((int)resp.StatusCode != 404)
                        return null;

                }
            }
            else
            {
                if (!allowScan) return null;
            }


            // slow path: scan regions for THIS floor
            string regionCacheKey = $"regionForMap-c{continentId}-f{floorId}-m{mapId}-{CACHE_VER}";

            int scannedRegionId;
            if (!_cache.TryLoad(regionCacheKey, out scannedRegionId) || scannedRegionId == 0)
            {
                scannedRegionId = await FindRegionContainingMapAsync(continentId, floorId, mapId, ct).ConfigureAwait(false);
                if (scannedRegionId == 0)
                {
                    if (DEBUG_LOGS) Logger.Debug($"[MapDetails] mapId={mapId} not found on c={continentId} f={floorId}");
                    return null;
                }

                _cache.Save(regionCacheKey, scannedRegionId);
                if (DEBUG_LOGS) Logger.Debug($"[MapDetails] resolved regionId={scannedRegionId} for mapId={mapId} on c={continentId} f={floorId}");
            }


            // try mapdetails cache first for the scanned region
            {
                string scannedMapDetailsKey = $"mapdetails-c{continentId}-f{floorId}-r{scannedRegionId}-m{mapId}-{CACHE_VER}";
                MapDetailsCache scannedCached;
                if (_cache.TryLoad(scannedMapDetailsKey, out scannedCached) && scannedCached != null)
                {
                    return Tuple.Create(scannedCached.Pois, scannedCached.Waypoints);
                }
            }

            // retry once with scanned region
            var retryUrl = $"https://api.guildwars2.com/v2/continents/{continentId}/floors/{floorId}/regions/{scannedRegionId}/maps/{mapId}";

            if (DEBUG_LOGS) Logger.Debug($"[MapDetails] retry -> {retryUrl}");

            using (var retryResp = await Http.GetAsync(retryUrl, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                if (!retryResp.IsSuccessStatusCode) return null;

                string retryJson = await retryResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseAndCache(continentId, floorId, scannedRegionId, mapId, retryJson);
            }
        }



        private Tuple<List<Tuple<string, int, int>>, List<Tuple<string, int, int>>> ParseAndCache(
            int continentId, int floorId, int regionId, int mapId, string json)
        {
            var pois = new List<Tuple<string, int, int>>();
            var wps = new List<Tuple<string, int, int>>();

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("points_of_interest", out var poiObj) && poiObj.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in poiObj.EnumerateObject())
                    {
                        var poi = prop.Value;

                        string name = "";
                        if (poi.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                            name = n.GetString() ?? "";

                        if (!poi.TryGetProperty("coord", out var coord) ||
                            coord.ValueKind != JsonValueKind.Array ||
                            coord.GetArrayLength() != 2)
                            continue;

                        int x = (int)coord[0].GetDouble();
                        int y = (int)coord[1].GetDouble();

                        string type = "";
                        if (poi.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                            type = t.GetString() ?? "";

                        if (string.Equals(type, "waypoint", StringComparison.OrdinalIgnoreCase))
                            wps.Add(Tuple.Create(name, x, y));
                        else
                            pois.Add(Tuple.Create(name, x, y));
                    }
                }
            }

            string cacheKey = $"mapdetails-c{continentId}-f{floorId}-r{regionId}-m{mapId}-{CACHE_VER}";

            _cache.Save(cacheKey, new MapDetailsCache { Pois = pois, Waypoints = wps });

            if (DEBUG_LOGS)
                Logger.Debug($"[MapDetails] parsed pois={pois.Count} wps={wps.Count}");

            return Tuple.Create(pois, wps);
        }


        private async Task<int> FindRegionContainingMapAsync(int continentId, int floorId, int mapId, CancellationToken ct)
        {
            var regionsUrl = $"https://api.guildwars2.com/v2/continents/{continentId}/floors/{floorId}/regions";

            if (DEBUG_LOGS)
                Logger.Debug($"[MapDetails] HTTP GET {regionsUrl}");

            using (var resp = await Http.GetAsync(regionsUrl, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) return 0;

                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var regionIds = new List<int>();

                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int rid))
                                regionIds.Add(rid);
                        }
                    }
                }

                for (int i = 0; i < regionIds.Count; i++)
                {
                    int rid = regionIds[i];
                    var testUrl = $"https://api.guildwars2.com/v2/continents/{continentId}/floors/{floorId}/regions/{rid}/maps/{mapId}";

                    using (var testResp = await Http.GetAsync(testUrl, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                    {
                       
                        if (testResp.IsSuccessStatusCode)
                            return rid;
                    }
                }
            }

            return 0;
        }

        private async Task<List<int>> GetAllContinentsAsync(CancellationToken ct)
        {
            string cacheKey = "continents-all-v1";

            List<int> cached;
            if (_cache.TryLoad(cacheKey, out cached) && cached != null && cached.Count > 0)
                return cached;

            var url = "https://api.guildwars2.com/v2/continents";

            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) return new List<int>();

                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var ids = new List<int>();

                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int id))
                                ids.Add(id);
                    }
                }

                _cache.Save(cacheKey, ids);
                return ids;
            }
        }

        private async Task<List<int>> GetContinentFloorsAsync(int continentId, CancellationToken ct)
        {
            string cacheKey = "continentfloors-" + continentId + "-v2";

            List<int> cached;
            if (_cache.TryLoad(cacheKey, out cached) && cached != null && cached.Count > 0)
                return cached;

            var url = $"https://api.guildwars2.com/v2/continents/{continentId}/floors";

            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) return new List<int>();

                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var floors = new List<int>();

                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int f))
                                floors.Add(f);
                    }
                }

                _cache.Save(cacheKey, floors);
                return floors;
            }
        }

        private static void AddUniqueFloors(List<int> list, int[] floors)
        {
            if (floors == null) return;
            for (int i = 0; i < floors.Length; i++)
                AddUniqueInt(list, floors[i]);
        }

        private static void AddUniqueInt(List<int> list, int value)
        {
            if (list == null) return;
            if (list.Contains(value)) return;
            list.Add(value);
        }
    }
}
