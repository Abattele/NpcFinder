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

        private const string CacheKey = "gw2-mapindex-v3"; // bump version because cache shape changed

        private Dictionary<string, int> _nameToId;
        private List<string> _allMapNamesOriginal;

        private static string Normalize(string s) => (s ?? "").Trim().ToLowerInvariant();


        // cache payload storing BOTH dict + names
        private sealed class MapIndexCache
        {
            public Dictionary<string, int> NameToId { get; set; }
            public List<string> AllMapNamesOriginal { get; set; }
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

        private async Task EnsureLoadedAsync(CancellationToken ct)
        {

            if (_nameToId != null && _allMapNamesOriginal != null) return;

            // 1/ try cache FIRST
            if (_cache.TryLoad(CacheKey, out MapIndexCache cached) &&
                cached != null &&
                cached.NameToId != null && cached.NameToId.Count > 0 &&
                cached.AllMapNamesOriginal != null && cached.AllMapNamesOriginal.Count > 0)
            {
                _nameToId = cached.NameToId;
                _allMapNamesOriginal = cached.AllMapNamesOriginal;
                return;
            }

            ct.ThrowIfCancellationRequested();

            // 2/ build from API
            var maps = await _v2.Maps.AllAsync();

            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();

            foreach (var m in maps)
            {
                if (string.IsNullOrWhiteSpace(m.Name)) continue;

                string norm = Normalize(m.Name);
                if (!dict.ContainsKey(norm)) dict[norm] = m.Id;

                names.Add(m.Name.Trim());
            }

            _nameToId = dict;
            _allMapNamesOriginal = names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToList();

            // 3/ save both
            _cache.Save(CacheKey, new MapIndexCache
            {
                NameToId = _nameToId,
                AllMapNamesOriginal = _allMapNamesOriginal
            });
        }
    }
}
