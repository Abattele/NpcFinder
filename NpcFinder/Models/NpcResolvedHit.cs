namespace NpcFinder.Models
{
    public class NpcResolvedHit
    {
        // here i changed the data model but i didn't want to break existing UI/code that expects the old names
        // so i kept the old names as aliases to the new names ... i will comment the names accordingly

        // --- existing/common fields ---
        public string Title { get; set; } = "";
        public int MapId { get; set; }
        public string MapName { get; set; } = "";

        public int TargetContinentId { get; set; }
        public double TargetContinentX { get; set; }
        public double TargetContinentY { get; set; }

        // --- old naming (UI expects these) ---
        // these are ALIASES to the new naming
        public int ContinentId
        {
            get => TargetContinentId;
            set => TargetContinentId = value;
        }
        public double ContinentX
        {
            get => TargetContinentX;
            set => TargetContinentX = value;
        }

        public double ContinentY
        {
            get => TargetContinentY;
            set => TargetContinentY = value;
        }

        // --- extra fields old UI still expects ---
        public string Source { get; set; } = "";  // e.g. "wiki", "map", "poi", "waypoint"
        public string Debug { get; set; } = "";   
    }
}
