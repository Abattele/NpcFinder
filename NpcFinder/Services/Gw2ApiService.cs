using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gw2Sharp.WebApi.V2;
using NpcFinder.Models;

namespace NpcFinder.Services
{
    public class Gw2ApiService
    {
        private static readonly bool DEBUG_LOGS = false;

        private readonly IGw2WebApiV2Client _v2;
        private readonly CacheStore _cache;

        public Gw2ApiService(IGw2WebApiV2Client v2, CacheStore cache)
        {
            _v2 = v2;
            _cache = cache;
        }

        private static bool IsValidRect(Rect2D r)
        {
            return !(r.X1 == 0 && r.Y1 == 0 && r.X2 == 0 && r.Y2 == 0)
                   && r.X2 != r.X1 && r.Y2 != r.Y1;
        }

        public async Task<Gw2MapInfo> GetMapInfoAsync(int mapId, CancellationToken ct)
        {
            string key = "gw2-map-" + mapId;

            if (_cache.TryLoad(key, out Gw2MapInfo cached) && cached != null)
            {
                if (IsValidRect(cached.MapRect) && IsValidRect(cached.ContinentRect))
                    return cached;
                // cached entry is garbage/old schema -> ignore it and refetch
            }

            ct.ThrowIfCancellationRequested();

            var map = await _v2.Maps.GetAsync(mapId).ConfigureAwait(false);
            if (map == null) return null;

            int regionId = map.RegionId;

            int[] floors = null;
            if (map.Floors != null)
                floors = map.Floors.ToArray();

            var mapRect = RectFromRectangle(map.MapRect);
            var contRect = RectFromRectangle(map.ContinentRect);

            var log = Blish_HUD.Logger.GetLogger<Gw2ApiService>();

            if (DEBUG_LOGS)
            {
                try
                {
                    log.Info($"MapRect type: {map.MapRect.GetType().FullName}");
                    log.Info($"ContinentRect type: {map.ContinentRect.GetType().FullName}");
                    log.Info($"Floors extracted: {(floors == null ? "null" : string.Join(",", floors))}");

                    log.Warn($"[RectParse] mapRect=({mapRect.X1},{mapRect.Y1},{mapRect.X2},{mapRect.Y2}) " +
                             $"contRect=({contRect.X1},{contRect.Y1},{contRect.X2},{contRect.Y2}) " +
                             $"types: mapRectType={map.MapRect.GetType().FullName} contRectType={map.ContinentRect.GetType().FullName}");
                }
                catch { }
            }

            var info = new Gw2MapInfo
            {
                Id = map.Id,
                Name = map.Name,
                ContinentId = map.ContinentId,
                DefaultFloor = map.DefaultFloor,
                RegionId = regionId,
                Floors = floors ?? Array.Empty<int>(),
                MapRect = mapRect,
                ContinentRect = contRect,
            };

            _cache.Save(key, info);
            return info;
        }


        private static Rect2D RectFromRectangle(Gw2Sharp.WebApi.V2.Models.Rectangle r)
        {

            double x1, y1, x2, y2;

            // try TopLeft + BottomRight
            if (TryGetXY(r.TopLeft, out x1, out y1) && TryGetXY(r.BottomRight, out x2, out y2))
                return new Rect2D(x1, y1, x2, y2);

            // try BottomLeft + TopRight
            if (TryGetXY(r.BottomLeft, out x1, out y1) && TryGetXY(r.TopRight, out x2, out y2))
                return new Rect2D(x1, y1, x2, y2);

            // try TopRight + BottomLeft (swap if needed)
            if (TryGetXY(r.TopRight, out var trX, out var trY) && TryGetXY(r.BottomLeft, out var blX, out var blY))
                return new Rect2D(blX, trY, trX, blY);

            // last resort: if only one corner exists + width/height
            // assume we have TopRight and width/height: top-right means x2,y1
            if (TryGetXY(r.TopRight, out var xTR, out var yTR) && r.Width != 0 && r.Height != 0)
            {
                x2 = xTR;
                y1 = yTR;
                x1 = x2 - r.Width;
                y2 = y1 + r.Height;
                return new Rect2D(x1, y1, x2, y2);
            }

            if (TryGetXY(r.BottomRight, out var xBR, out var yBR) && r.Width != 0 && r.Height != 0)
            {
                x2 = xBR;
                y2 = yBR;
                x1 = x2 - r.Width;
                y1 = y2 - r.Height;
                return new Rect2D(x1, y1, x2, y2);
            }

            return new Rect2D(0, 0, 0, 0);
        }

        private static bool TryGetXY(Gw2Sharp.Models.Coordinates2 c, out double x, out double y)
        {
            x = y = 0;
            try
            {
                x = c.X;
                y = c.Y;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
