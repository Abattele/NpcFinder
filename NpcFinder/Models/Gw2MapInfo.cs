using System;

namespace NpcFinder.Models
{

    public class Gw2MapInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }

        public int ContinentId { get; set; }
        public int DefaultFloor { get; set; }

        public int RegionId { get; set; }

        public int[] Floors { get; set; } = Array.Empty<int>();

        public Rect2D MapRect { get; set; }
        public Rect2D ContinentRect { get; set; }
    }

    public struct Rect2D
    {
        public double X1, Y1, X2, Y2;
        public Rect2D(double x1, double y1, double x2, double y2)
        {
            X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        }
    }



}
