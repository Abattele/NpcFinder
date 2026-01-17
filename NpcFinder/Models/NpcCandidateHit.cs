namespace NpcFinder.Models
{
    public class NpcCandidateHit
    {
        public string Title { get; set; }      // wiki page title
        public string MapName { get; set; }    // may be null i think
        public int? MapId { get; set; }        // may be null until resolved
        public int X { get; set; }
        public int Y { get; set; }

        public override string ToString()
        {
            var map = string.IsNullOrWhiteSpace(MapName) ? (MapId != null ? $"Map {MapId}" : "Unknown map") : MapName;
            return $"{Title} — {map} — ({X},{Y})";
        }
    }
}
