using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Gw2Sharp.WebApi.V2;
using NpcFinder.Models;

namespace NpcFinder.Services
{
    public class Gw2ApiService
    {
        private readonly IGw2WebApiV2Client _v2;
        private readonly CacheStore _cache;

        public Gw2ApiService(IGw2WebApiV2Client v2, CacheStore cache)
        {
            _v2 = v2;
            _cache = cache;
        }

        private static bool IsValidRect(Rect2D r)
        {
            // adjust to Rect2D fields/properties
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

            // avoid RuntimeBinder: use reflection
            var floors = ReadIntArrayProp(map, "Floors");
            
            var log = Blish_HUD.Logger.GetLogger<Gw2ApiService>();

            try
            {
                
                log.Info($"MapRect type: {map.MapRect.GetType().FullName}");
                log.Info($"ContinentRect type: {map.ContinentRect.GetType().FullName}");
                log.Info($"Floors extracted: {(floors == null ? "null" : string.Join(",", floors))}");
            }
            catch { }

            var mapRect = ReadRectAny(map.MapRect);
            var contRect = ReadRectAny(map.ContinentRect);

           
            log.Warn($"[RectParse] mapRect=({mapRect.X1},{mapRect.Y1},{mapRect.X2},{mapRect.Y2}) " +
                     $"contRect=({contRect.X1},{contRect.Y1},{contRect.X2},{contRect.Y2}) " +
                     $"types: mapRectType={map.MapRect.GetType().FullName} contRectType={map.ContinentRect.GetType().FullName}");


            var info = new Gw2MapInfo
            {
                Id = map.Id,
                Name = map.Name,
                ContinentId = map.ContinentId,
                DefaultFloor = map.DefaultFloor,
                Floors = floors ?? Array.Empty<int>(),
                MapRect = mapRect,
                ContinentRect = contRect,
            };

            _cache.Save(key, info);
            return info;
        }

        // ---------- helpers ----------

        private static int[] ReadIntArrayProp(object obj, string propName)
        {
            if (obj == null) return null;

            var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return null;

            object v;
            try { v = p.GetValue(obj); }
            catch { return null; }

            if (v == null) return null;

            if (v is int[] ia) return ia;
            if (v is IEnumerable<int> ien) return ien.ToArray();

            // Fallback: non-generic IEnumerable (convert elements)
            if (v is IEnumerable en)
            {
                try
                {
                    return en.Cast<object>()
                             .Select(o => { try { return Convert.ToInt32(o); } catch { return (int?)null; } })
                             .Where(x => x.HasValue)
                             .Select(x => x.Value)
                             .ToArray();
                }
                catch { return null; }
            }

            return null;
        }


        private static Rect2D ReadRectAny(object rectObj)
        {
            if (rectObj == null) return new Rect2D(0, 0, 0, 0);

            if (TryParseRectFromNestedEnumerable(rectObj, out var r1))
                return r1;

            // --- some wrappers store the underlying array in a property/field ---
            if (TryGetObjAny(rectObj, new[] { "Value", "Coordinates", "Data", "Rect", "Rectangle" }, out var inner) && inner != null)
            {
                if (TryParseRectFromNestedEnumerable(inner, out var r2))
                    return r2;
            }


            // Case 1: X1,Y1,X2,Y2
            if (TryGetProp(rectObj, "X1", out double x1) &&
                TryGetProp(rectObj, "Y1", out double y1) &&
                TryGetProp(rectObj, "X2", out double x2) &&
                TryGetProp(rectObj, "Y2", out double y2))
                return new Rect2D(x1, y1, x2, y2);


            // Case 1b: Left,Top,Right,Bottom
            if (TryGetProp(rectObj, "Left", out double left) &&
                TryGetProp(rectObj, "Top", out double top) &&
                TryGetProp(rectObj, "Right", out double right) &&
                TryGetProp(rectObj, "Bottom", out double bottom))
                return new Rect2D(left, top, right, bottom);


            // Case 2: TopLeft/BottomRight with X/Y
            if (TryGetObj(rectObj, "TopLeft", out var tl) && TryGetObj(rectObj, "BottomRight", out var br))
            {
                if (TryGetProp(tl, "X", out double tlx) && TryGetProp(tl, "Y", out double tly) &&
                    TryGetProp(br, "X", out double brx) && TryGetProp(br, "Y", out double bry))
                    return new Rect2D(tlx, tly, brx, bry);
            }


            // Case 3: X,Y,Width,Height
            if (TryGetProp(rectObj, "X", out double rx) &&
                TryGetProp(rectObj, "Y", out double ry) &&
                TryGetProp(rectObj, "Width", out double rw) &&
                TryGetProp(rectObj, "Height", out double rh))
                return new Rect2D(rx, ry, rx + rw, ry + rh);


            return new Rect2D(0, 0, 0, 0);
        }

        private static bool TryParseRectFromNestedEnumerable(object obj, out Rect2D rect)
        {
            rect = new Rect2D(0, 0, 0, 0);
            if (obj == null) return false;

            // something like: [[x1,y1],[x2,y2]] (arrays/lists/enumerables)
            if (!(obj is System.Collections.IEnumerable outer)) return false;

            var pts = new System.Collections.Generic.List<object>();
            foreach (var it in outer)
            {
                if (it == null) continue;
                pts.Add(it);
                if (pts.Count > 2) break;
            }

            if (pts.Count != 2) return false;

            if (!TryGetTwoNumbers(pts[0], out double x1, out double y1)) return false;
            if (!TryGetTwoNumbers(pts[1], out double x2, out double y2)) return false;

            rect = new Rect2D(x1, y1, x2, y2);
            return true;
        }

        private static bool TryGetTwoNumbers(object pt, out double a, out double b)
        {
            a = b = 0;
            if (pt == null) return false;

            // if point is itself an enumerable with 2 numbers
            if (pt is System.Collections.IEnumerable en)
            {
                var nums = new System.Collections.Generic.List<object>();
                foreach (var it in en)
                {
                    if (it == null) continue;
                    nums.Add(it);
                    if (nums.Count > 2) break;
                }
                if (nums.Count != 2) return false;

                try { a = Convert.ToDouble(nums[0]); b = Convert.ToDouble(nums[1]); return true; }
                catch { return false; }
            }

            // or point could be an object with X/Y
            if (TryGetProp(pt, "X", out a) && TryGetProp(pt, "Y", out b))
                return true;

            return false;
        }

        private static bool TryGetObjAny(object o, string[] names, out object value)
        {
            value = null;
            if (o == null) return false;

            var t = o.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            for (int i = 0; i < names.Length; i++)
            {
                var p = t.GetProperty(names[i], flags);
                if (p != null)
                {
                    try { value = p.GetValue(o); return value != null; } catch { }
                }

                var f = t.GetField(names[i], flags);
                if (f != null)
                {
                    try { value = f.GetValue(o); return value != null; } catch { }
                }
            }

            return false;
        }


        private static bool TryGetProp(object o, string name, out double value)
        {
            value = 0;
            if (o == null) return false;
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return false;
            try
            {
                value = ToDouble(p.GetValue(o));
                return true;
            }
            catch { return false; }
        }

        private static bool TryGetObj(object o, string name, out object value)
        {
            value = null;
            if (o == null) return false;
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return false;
            try
            {
                value = p.GetValue(o);
                return value != null;
            }
            catch { return false; }
        }

        private static double ToDouble(object v)
        {
            try { return Convert.ToDouble(v); } catch { return 0.0; }
        }
    }
}
