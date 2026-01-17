using Gw2Sharp.WebApi.V2;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NpcFinder.Services
{

    public class Gw2MapIndexService
    {
        private readonly IGw2WebApiV2Client _v2;
        private readonly CacheStore _cache;

        private const string CacheKey = "gw2-mapindex-v2";
        private Dictionary<string, int> _nameToId;
        private static string Normalize(string s) => s.Trim().ToLowerInvariant();

        public Gw2MapIndexService(IGw2WebApiV2Client v2, CacheStore cache)
        {
            _v2 = v2;
            _cache = cache;
        }

        public async Task<int?> ResolveMapIdByNameAsync(string mapName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(mapName)) return null;

            await EnsureLoadedAsync(ct);

            string k = Normalize(mapName);
            return _nameToId.TryGetValue(k, out int id) ? id : (int?)null;
        }

        private async Task EnsureLoadedAsync(CancellationToken ct)
        {
            if (_nameToId != null) return;

            if (_cache.TryLoad(CacheKey, out Dictionary<string, int> cached) && cached != null && cached.Count > 0)
            {
                _nameToId = cached;
                return;
            }

            ct.ThrowIfCancellationRequested();

            // In Gw2Sharp this returns all map objects (ids=all).
            var maps = await _v2.Maps.AllAsync();

            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in maps)
            {
                if (string.IsNullOrWhiteSpace(m.Name)) continue;
                string k = Normalize(m.Name);
                if (!dict.ContainsKey(k)) dict[k] = m.Id;
            }

            _nameToId = dict;
            _cache.Save(CacheKey, dict);
        }


    }
}
