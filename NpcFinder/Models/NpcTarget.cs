namespace NpcFinder.Models
{
    public class NpcTarget
    {
        public string WikiTitle { get; set; }
        public string DisplayName { get; set; }

        public int MapId { get; set; }
        public string MapName { get; set; }

        public int TargetMapX { get; set; }
        public int TargetMapY { get; set; }

        public double TargetContinentX { get; set; }
        public double TargetContinentY { get; set; }

        public int TargetContinentId { get; set; }

        public Gw2MapInfo MapInfo { get; set; }
    }
}
