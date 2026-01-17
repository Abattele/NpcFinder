using System;
using System.Collections.Generic;

namespace NpcFinder.Models
{
    public class PoiWpFloorResult
    {
        public int UsedFloor { get; set; }

        // name, x, y  (these x/y are CONTINENT coords from /continents/... endpoint)
        public List<Tuple<string, int, int>> Pois { get; set; } = new List<Tuple<string, int, int>>();
        public List<Tuple<string, int, int>> Waypoints { get; set; } = new List<Tuple<string, int, int>>();
    }
}
