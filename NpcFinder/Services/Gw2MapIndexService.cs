using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gw2Sharp.WebApi.V2;

namespace NpcFinder.Services
{
    public class Gw2MapIndexService
    {
        private readonly IGw2WebApiV2Client _v2;
        private readonly CacheStore _cache;


        private const string CacheKey = "gw2-mapindex-v4";

        private Dictionary<string, int> _nameToId;
        private List<string> _allMapNamesOriginal;
        private List<MapRectEntry> _rectIndex;

        private static string Normalize(string s) => (s ?? "").Trim().ToLowerInvariant();


        // i no longer want to iterate through all maps, i will iterate once and save the rectangle relative positions of a continent and cache it so i can
        // do fast lookups later
        private sealed class MapRectEntry
        {
            public int Id { get; set; }
            public int ContinentId { get; set; }
            public double MinX { get; set; }
            public double MinY { get; set; }
            public double MaxX { get; set; }
            public double MaxY { get; set; }
        }


        // cache payload storing BOTH dict + names + rects
        private sealed class MapIndexCache
        {
            public Dictionary<string, int> NameToId { get; set; }
            public List<string> AllMapNamesOriginal { get; set; }
            public List<MapRectEntry> RectIndex { get; set; }
        }

        public Gw2MapIndexService(IGw2WebApiV2Client v2, CacheStore cache)
        {
            _v2 = v2;
            _cache = cache;
        }

        public async Task<List<int>> GetAllKnownMapIdsAsync(CancellationToken ct)
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            return _nameToId.Values.Distinct().ToList();
        }

        public async Task<int?> ResolveMapIdByNameAsync(string mapName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(mapName)) return null;

            await EnsureLoadedAsync(ct).ConfigureAwait(false);

            string k = Normalize(mapName);
            return _nameToId.TryGetValue(k, out int id) ? id : (int?)null;
        }

        public async Task<List<string>> SuggestMapNamesAsync(string prefix, int limit, CancellationToken ct)
        {
            prefix = (prefix ?? "").Trim();
            if (prefix.Length < 2) return new List<string>();

            limit = Math.Max(1, Math.Min(limit, 20));

            await EnsureLoadedAsync(ct).ConfigureAwait(false);

            string p = Normalize(prefix);

            return _allMapNamesOriginal
                .Where(n => Normalize(n).StartsWith(p))
                .Take(limit)
                .ToList();
        }


        // fast point -> mapId using cached continent rects
        public async Task<int?> FindMapIdByContinentPointAsync(int cx, int cy, int preferredContinentId, CancellationToken ct)
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (_rectIndex == null || _rectIndex.Count == 0) return null;

            bool Contains(MapRectEntry e) =>
                cx >= e.MinX && cx <= e.MaxX && cy >= e.MinY && cy <= e.MaxY;

            // 1/ preferred continent first
            if (preferredContinentId != 0)
            {
                foreach (var e in _rectIndex)
                {
                    ct.ThrowIfCancellationRequested();
                    if (e.ContinentId != preferredContinentId) continue;
                    if (Contains(e)) return e.Id;
                }
            }

            // 2/ any continent
            foreach (var e in _rectIndex)
            {
                ct.ThrowIfCancellationRequested();
                if (Contains(e)) return e.Id;
            }

            return null;
        }


        private static bool TryGetContinentRect(Gw2Sharp.WebApi.V2.Models.Rectangle r, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = maxX = maxY = 0;

            try
            {
                var tl = r.TopLeft;
                var br = r.BottomRight;

                minX = Math.Min(tl.X, br.X);
                maxX = Math.Max(tl.X, br.X);
                minY = Math.Min(tl.Y, br.Y);
                maxY = Math.Max(tl.Y, br.Y);

                if (double.IsNaN(minX) || double.IsNaN(minY) || double.IsNaN(maxX) || double.IsNaN(maxY))
                    return false;

                if (Math.Abs(maxX - minX) < 1e-6 || Math.Abs(maxY - minY) < 1e-6)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task EnsureLoadedAsync(CancellationToken ct)
        {
            if (_nameToId != null && _allMapNamesOriginal != null && _rectIndex != null)
                return;

            // 1/ cache first
            if (_cache.TryLoad(CacheKey, out MapIndexCache cached) &&
                cached != null &&
                cached.NameToId != null && cached.NameToId.Count > 0 &&
                cached.AllMapNamesOriginal != null && cached.AllMapNamesOriginal.Count > 0 &&
                cached.RectIndex != null && cached.RectIndex.Count > 0)
            {
                _nameToId = cached.NameToId;
                _allMapNamesOriginal = cached.AllMapNamesOriginal;
                _rectIndex = cached.RectIndex;
                return;
            }

            ct.ThrowIfCancellationRequested();

            // 2/ build from API
            var maps = await _v2.Maps.AllAsync().ConfigureAwait(false);
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            var rects = new List<MapRectEntry>();

            foreach (var m in maps)
            {
                if (string.IsNullOrWhiteSpace(m.Name)) continue;

                string norm = Normalize(m.Name);
                if (!dict.ContainsKey(norm)) dict[norm] = m.Id;
                names.Add(m.Name.Trim());

                if (TryGetContinentRect(m.ContinentRect, out var minX, out var minY, out var maxX, out var maxY))
                {
                    rects.Add(new MapRectEntry
                    {
                        Id = m.Id,
                        ContinentId = m.ContinentId,
                        MinX = minX,
                        MinY = minY,
                        MaxX = maxX,
                        MaxY = maxY
                    });
                }
            }

            _nameToId = dict;
            _allMapNamesOriginal = names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToList();

            _rectIndex = rects;

            // 3/ save
            _cache.Save(CacheKey, new MapIndexCache
            {
                NameToId = _nameToId,
                AllMapNamesOriginal = _allMapNamesOriginal,
                RectIndex = _rectIndex
            });
        }
    }
}