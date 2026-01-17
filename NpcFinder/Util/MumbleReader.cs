using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Blish_HUD;
using System.Globalization;
using Microsoft.Xna.Framework;


namespace NpcFinder.Util
{
    public static class MumbleReader
    {
        private static readonly Logger Log = Logger.GetLogger(typeof(MumbleReader));
        private static object _uiObj;
        private static object _mapIdObj;
        private static bool _discovered;
        private static DateTime _lastWarn = DateTime.MinValue;
        private static DateTime _lastDumpUtc = DateTime.MinValue;
        private static bool _didHeaderDump = false;
        private static bool _dumped;


        public static void ResetDiscovery()
        {
            _discovered = false;
            _uiObj = null;
            _mapIdObj = null;
        }

        public static bool TryGetMapId(out int mapId)
        {
            mapId = 0;
            EnsureDiscovered();

            object src = _mapIdObj ?? GameService.Gw2Mumble;
            if (src == null) return false;

            return TryGetInt(src, new[] { "MapId", "CurrentMapId", "mapId" }, out mapId) && mapId > 0;
        }

        public static bool TryGetUiState(out uint uiState)
        {
            uiState = 0;
            EnsureDiscovered();

            object src = _uiObj;
            if (src == null) return false;

            if (!TryGetUInt(src, new[] { "UiState", "UIState", "uiState" }, out uiState))
            {
                WarnOccasionally("[MumbleReader] UiState missing on discovered uiObj.");
                return false;
            }
            return true;
        }

        public static void DumpUiOnce()
        {
            if (_dumped) return;
            _dumped = true;

            try
            {
                // real UI struct used by Blish
                var ui = GameService.Gw2Mumble?.UI;

                if (ui == null)
                {
                    Log.Warn("[MumbleUI] GameService.Gw2Mumble.UI is null");
                    return;
                }

                Log.Warn($"[MumbleUI] UI type={ui.GetType().FullName}");

                // debugging
                DumpProp(ui, "IsMapOpen");
                DumpProp(ui, "IsCompassTopRight");   // sometimes exists
                DumpProp(ui, "MapCenter");           // Vector2 or similar
                DumpProp(ui, "MapScale");            // float
                DumpProp(ui, "MapRotation");         // float sometimes
                DumpProp(ui, "CompassRotation");     // float sometimes

                // Also dump *any* property that's like map/compass/scale/center/zoom
                var t = ui.GetType();
                var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public);
                foreach (var p in props)
                {
                    var n = p.Name.ToLowerInvariant();
                    if (n.Contains("map") || n.Contains("compass") || n.Contains("scale") || n.Contains("center") || n.Contains("zoom") || n.Contains("rotation"))
                    {
                        object val = null;
                        try { val = p.GetValue(ui); } catch { }
                        Log.Warn($"[MumbleUI] {p.Name} = {Fmt(val)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[MumbleUI] Dump failed: " + ex);
            }
        }

        private static void DumpProp(object obj, string propName)
        {
            var p = obj.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
            if (p == null)
            {
                Log.Warn($"[MumbleUI] {propName} = <missing>");
                return;
            }
            object val = null;
            try { val = p.GetValue(obj); } catch { val = "<error reading>"; }
            Log.Warn($"[MumbleUI] {propName} = {Fmt(val)}");
        }

        private static string Fmt(object v)
        {
            if (v == null) return "null";
            if (v is Microsoft.Xna.Framework.Vector2  vv) return $"({vv.X},{vv.Y})";
            return v.ToString();
        }

        public static void DumpUiOncePerSecond(bool requireMapOpen = true)
        {
            try
            {
                var ui = GameService.Gw2Mumble?.UI;
                if (ui == null)
                {
                    WarnOccasionally("[MumbleUI] GameService.Gw2Mumble.UI is null");
                    return;
                }

                // gating: only dump when big map is open
                bool mapOpen = false;
                try { mapOpen = ui.IsMapOpen; } catch { }

                if (requireMapOpen && !mapOpen)
                    return;

                var now = DateTime.UtcNow;
                if ((now - _lastDumpUtc).TotalSeconds < 1.0)
                    return;
                _lastDumpUtc = now;

                // Print type + property list (helps discover names like MapPosition)
                if (!_didHeaderDump)
                {
                    _didHeaderDump = true;

                    Log.Warn($"[MumbleUI] UI type={ui.GetType().FullName}");
                    var t = ui.GetType();
                    var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public);
                    foreach (var p in props)
                    {
                        var n = p.Name.ToLowerInvariant();
                        if (n.Contains("map") || n.Contains("compass") || n.Contains("scale") ||
                            n.Contains("center") || n.Contains("zoom") || n.Contains("rotation") ||
                            n.Contains("position"))
                        {
                            object val = null;
                            try { val = p.GetValue(ui); } catch { }
                            Log.Warn($"[MumbleUI] {p.Name} = {Fmt(val)}");
                        }
                    }
                }

                Log.Warn($"MumbleUI open={SafeBool(() => ui.IsMapOpen)}" + $"scale={SafeObj(() => ui.MapScale)} " + $"center={Fmt(SafeObj(() => ui.MapCenter))} " + $"pos={Fmt(SafeObj(() => GetAnyProp(ui,"MapPosition")))}");


            }
            catch (Exception ex)
            {

                WarnOccasionally("[MumbleUI] Dump failed: " + ex.Message);

            }

        }

        // helpers used above
        private static object SafeObj(Func<object> f) { try { return f(); } catch { return null; } }
        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

        private static object GetAnyProp(object obj, string propName)
        {
            if (obj == null) return null;
            var p = obj.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
            if (p == null) return null;
            try { return p.GetValue(obj); } catch { return null; }
        }

        // useful for the last algorithm, but i changed data model and i dont need these anymore
        static bool TryGetVec2(object obj, string[] names, out float x, out float y)
        {
            x = y = 0;
            object vec;
            if (!TryGetObj(obj, names, out vec) || vec == null) return false;
            return TryGetFloat(vec, new[] { "X", "x" }, out x) &&
                   TryGetFloat(vec, new[] { "Y", "y" }, out y);
        }

        static bool TryGetBool(object obj, string[] names, out bool val)
        {
            val = false;
            foreach (var n in names)
            {
                var p = obj.GetType().GetProperty(n);
                if (p == null) continue;
                var o = p.GetValue(obj);
                if (o is bool b) { val = b; return true; }
            }
            return false;
        }

        // ---------------- discovery ----------------

        private static void EnsureDiscovered()
        {
            if (_discovered) return;
            _discovered = true;

            try
            {
                // use the SERVICE as the root, not Info.
                var deepRoot = GameService.Gw2Mumble;
                if (deepRoot == null) return;

                _uiObj = FindBestContainer(deepRoot,
                    mustHaveAny: new[] { "UiState", "UIState", "uiState" },
                    alsoNeed: new[] { "MapScale", "mapScale", "WorldMapScale" },
                    andOneOfGroups: new[] {
                        new[] { "MapCenter", "mapCenter", "WorldMapCenter" },
                        new[] { "MapCenterX", "mapCenterX" }
                    }
                );

                _mapIdObj = FindBestContainer(deepRoot,
                    mustHaveAny: new[] { "MapId", "CurrentMapId", "mapId" },
                    alsoNeed: null,
                    andOneOfGroups: null
                );

                Log.Debug($"[MumbleReader] discovery: root={deepRoot.GetType().FullName} uiObj={_uiObj?.GetType().FullName ?? "null"} mapIdObj={_mapIdObj?.GetType().FullName ?? "null"}");
            }
            catch (Exception ex)
            {
                WarnOccasionally("[MumbleReader] discovery failed: " + ex.Message);
            }
        }

        private static object FindBestContainer(object root, string[] mustHaveAny, string[] alsoNeed, string[][] andOneOfGroups)
        {
            var q = new Queue<(object obj, int depth)>();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

            q.Enqueue((root, 0));
            seen.Add(root);

            while (q.Count > 0)
            {
                var (obj, depth) = q.Dequeue();
                if (obj == null) continue;

                var typeName = obj.GetType().FullName ?? "";

                // avoid latching onto Info

                if (typeName.EndsWith(".Info", StringComparison.OrdinalIgnoreCase))
                {
                    // still traverse children, but don't accept it as a container
                }
                else
                {
                    if (HasAnyMember(obj, mustHaveAny) &&
                        (alsoNeed == null || HasAnyMember(obj, alsoNeed)) &&
                        (andOneOfGroups == null || HasOneOfGroups(obj, andOneOfGroups)))
                    {
                        return obj;
                    }
                }

                if (depth >= 5) continue;

                foreach (var child in EnumerateChildren(obj))
                {
                    if (child == null) continue;
                    if (seen.Contains(child)) continue;
                    seen.Add(child);
                    q.Enqueue((child, depth + 1));
                }
            }

            return null;
        }


        private static bool HasOneOfGroups(object obj, string[][] groups)
        {
            for (int i = 0; i < groups.Length; i++)
                if (HasAnyMember(obj, groups[i])) return true;
            return false;
        }

        private static bool HasAnyMember(object obj, string[] names)
        {
            if (obj == null || names == null || names.Length == 0) return false;
            var t = obj.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            for (int i = 0; i < names.Length; i++)
            {
                if (t.GetProperty(names[i], flags) != null) return true;
                if (t.GetField(names[i], flags) != null) return true;
            }
            return false;
        }

        private static IEnumerable<object> EnumerateChildren(object obj)
        {
            var t = obj.GetType();
            if (t == typeof(string) || t.IsPrimitive) yield break;

            if (obj is IEnumerable && !(obj is IDictionary)) yield break;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var p in t.GetProperties(flags))
            {
                if (!p.CanRead) continue;
                if (p.GetIndexParameters().Length != 0) continue;

                object v = null;
                try { v = p.GetValue(obj); } catch { }
                if (IsWalkable(v)) yield return v;
            }

            foreach (var f in t.GetFields(flags))
            {
                object v = null;
                try { v = f.GetValue(obj); } catch { }
                if (IsWalkable(v)) yield return v;
            }
        }

        private static bool IsWalkable(object v)
        {
            if (v == null) return false;
            var t = v.GetType();
            if (t == typeof(string) || t.IsPrimitive) return false;
            return true;
        }

        // ---------------- getters ----------------

        private static bool TryGetObj(object obj, string[] names, out object value)
        {
            value = null;
            if (obj == null) return false;

            var t = obj.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            for (int i = 0; i < names.Length; i++)
            {
                var p = t.GetProperty(names[i], flags);
                if (p != null) { try { value = p.GetValue(obj); return value != null; } catch { } }

                var f = t.GetField(names[i], flags);
                if (f != null) { try { value = f.GetValue(obj); return value != null; } catch { } }
            }
            return false;
        }

        private static bool TryGetFloat(object obj, string[] names, out float value)
        {
            value = 0;
            if (!TryGetObj(obj, names, out var v)) return false;
            try { value = Convert.ToSingle(v); return true; } catch { return false; }
        }

        private static bool TryGetInt(object obj, string[] names, out int value)
        {
            value = 0;
            if (!TryGetObj(obj, names, out var v)) return false;
            try { value = Convert.ToInt32(v); return true; } catch { return false; }
        }

        private static bool TryGetUInt(object obj, string[] names, out uint value)
        {
            value = 0;
            if (!TryGetObj(obj, names, out var v)) return false;
            try { value = Convert.ToUInt32(v); return true; } catch { return false; }
        }

        private static void WarnOccasionally(string msg)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastWarn).TotalSeconds < 2) return;
            _lastWarn = now;
            Log.Warn(msg);
        }



        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }



        public static bool TryGetWorldMapUi(out float centerX, out float centerY, out float scale)
        {
            centerX = centerY = 0f;
            scale = 1f;

            var ui = GameService.Gw2Mumble?.UI;
            if (ui == null) return false;

            // ---- helper: get prop by trying multiple names
            object GetProp(string[] names)
            {
                var t = ui.GetType();
                foreach (var n in names)
                {
                    var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public);
                    if (p != null) return p.GetValue(ui);
                }
                return null;
            }

            // Scale
            var scaleObj = GetProp(new[] { "MapScale", "WorldMapScale" });
            if (!TryToFloat(scaleObj, out scale) || Math.Abs(scale) < 1e-6f)
            {
                return false;
            }

            // Center (must be 2D)
            var centerObj = GetProp(new[] { "MapCenter", "WorldMapCenter" });

            // If we have a Vector2, use it
            if (centerObj is Vector2 v2)
            {
                centerX = v2.X; centerY = v2.Y;
                return true;
            }

            // Sometimes it’s a point-like object (X,Y) —> ok, but reject rectangles (X,Y,Width,Height)
            if (centerObj != null)
            {
                var t = centerObj.GetType();
                bool looksLikeRect =
                    t.GetProperty("Width") != null ||
                    t.GetProperty("Height") != null ||
                    t.GetProperty("X1") != null || t.GetProperty("X2") != null ||
                    t.GetProperty("Y1") != null || t.GetProperty("Y2") != null;

                if (!looksLikeRect && TryGetXY(centerObj, out centerX, out centerY))
                    return true;
            }


            // if only MapPosition exists (Rectangle), use its CENTER as pixel-center anchor,
            // this is NOT continent center. i'm using this as a last resort because it’s less accurate.
            // Return false so the overlay conversion does not use garbage.
            return false;
        }

        private static bool TryToFloat(object o, out float v)
        {
            v = 0f;
            if (o == null) return false;

            try
            {
                if (o is float f) { v = f; return true; }
                if (o is double d) { v = (float)d; return true; }
                if (o is int i) { v = i; return true; }
                if (o is string s)
                {
                    return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                        || float.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v);
                }

                v = Convert.ToSingle(o, CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        private static bool TryGetXY(object obj, out float x, out float y)
        {
            x = y = 0f;
            if (obj == null) return false;

            try
            {
                var t = obj.GetType();
                var px = t.GetProperty("X", BindingFlags.Instance | BindingFlags.Public);
                var py = t.GetProperty("Y", BindingFlags.Instance | BindingFlags.Public);
                if (px == null || py == null) return false;

                var ox = px.GetValue(obj);
                var oy = py.GetValue(obj);

                return TryToFloat(ox, out x) && TryToFloat(oy, out y);
            }
            catch { return false; }
        }

    }
}
