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
        private static readonly Logger Logger = Logger.GetLogger<Gw2MapDetailsService>();
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

        // returns: (usedContinentId, usedFloorId, (pois, waypoints))
        public async Task<PoiWpFloorResult> GetPoisAndWaypointsWithFloorFallbackAsync(
            int continentId, int defaultFloorId, int[] preferredFloors, int mapId, CancellationToken ct)

        {
            // make a continent try-list: start with the given continent, then try all other continents dynamically.
            var continentsToTry = new List<int>();
            AddUniqueInt(continentsToTry, continentId);

            var allContinents = await GetAllContinentsAsync(ct).ConfigureAwait(false);
            for (int i = 0; i < allContinents.Count; i++)
                AddUniqueInt(continentsToTry, allContinents[i]);

            // try each continent, generate floor list each time.
            for (int ci = 0; ci < continentsToTry.Count; ci++)
            {
                int cTry = continentsToTry[ci];

                // generate prioritized floor list per continent.
                var floorsToTry = new List<int>();
                AddUniqueFloors(floorsToTry, preferredFloors);

                if (defaultFloorId >= 0)
                    AddUniqueInt(floorsToTry, defaultFloorId);

                // fallbacks (core Tyria uses 1 a lot; some endpoints use 0)
                AddUniqueInt(floorsToTry, 1);
                AddUniqueInt(floorsToTry, 0);

                // if still short, add a few discovered floors for THIS continent.
                if (floorsToTry.Count < 8)
                {
                    var discovered = await GetContinentFloorsAsync(cTry, ct).ConfigureAwait(false);
                    for (int i = 0; i < discovered.Count && floorsToTry.Count < 10; i++)
                        AddUniqueInt(floorsToTry, discovered[i]);
                }

                Logger.Debug($"[MapDetails] mapId={mapId} trying continent={cTry} floors=[{string.Join(",", floorsToTry)}]");

                // try each floor until find this map on that floor.
                for (int fi = 0; fi < floorsToTry.Count; fi++)
                {
                    int floor = floorsToTry[fi];

                    try
                    {
                        var data = await GetPoisAndWaypointsByRegionAsync(cTry, floor, mapId, ct).ConfigureAwait(false);
                        if (data != null)
                        {
                            // found it: return continent+floor
                            // success:
                            return new PoiWpFloorResult
                            {
                                UsedFloor = floor,
                                Pois = data.Item1,
                                Waypoints = data.Item2
                            };

                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[MapDetails] mapId={mapId} continent={cTry} floor={floor} failed: {ex.Message}");
                    }
                }
            }


            // failure:
            return new PoiWpFloorResult { UsedFloor = -1 };


        }

        // ---- resolve regionId then call /regions/{regionId}/maps/{mapId} ----
        private async Task<Tuple<List<Tuple<string, int, int>>, List<Tuple<string, int, int>>>> GetPoisAndWaypointsByRegionAsync(
            int continentId, int floorId, int mapId, CancellationToken ct)
        {
            // cache region lookup per map per floor+continent
            string regionCacheKey = $"regionForMap-c{continentId}-f{floorId}-m{mapId}-v2";

            int regionId;
            if (!_cache.TryLoad(regionCacheKey, out regionId) || regionId == 0)
            {
                regionId = await FindRegionContainingMapAsync(continentId, floorId, mapId, ct).ConfigureAwait(false);
                if (regionId == 0)
                {
                    Logger.Debug($"[MapDetails] mapId={mapId} not found on c={continentId} f={floorId}");
                    return null;
                }

                _cache.Save(regionCacheKey, regionId);
                Logger.Debug($"[MapDetails] resolved regionId={regionId} for mapId={mapId} on c={continentId} f={floorId}");
            }

            string cacheKey = $"mapdetails-c{continentId}-f{floorId}-r{regionId}-m{mapId}-v2";

            MapDetailsCache cached;
            if (_cache.TryLoad(cacheKey, out cached) && cached != null)
                return Tuple.Create(cached.Pois, cached.Waypoints);

            var url = $"https://api.guildwars2.com/v2/continents/{continentId}/floors/{floorId}/regions/{regionId}/maps/{mapId}";
            Logger.Debug($"[MapDetails] HTTP GET {url}");

            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                Logger.Debug($"[MapDetails] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                if (!resp.IsSuccessStatusCode) return null;

                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

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

                _cache.Save(cacheKey, new MapDetailsCache { Pois = pois, Waypoints = wps });
                Logger.Debug($"[MapDetails] parsed pois={pois.Count} wps={wps.Count}");

                return Tuple.Create(pois, wps);
            }
        }

        private async Task<int> FindRegionContainingMapAsync(int continentId, int floorId, int mapId, CancellationToken ct)
        {
            var regionsUrl = $"https://api.guildwars2.com/v2/continents/{continentId}/floors/{floorId}/regions";
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
