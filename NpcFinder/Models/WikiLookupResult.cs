using System.Collections.Generic;

namespace NpcFinder.Models
{
    public class WikiLookupResult
    {
        public string Title { get; set; }
        public string DisplayName { get; set; }
        public string Wikitext { get; set; } 

        public List<string> CandidateTitles { get; set; } = new List<string>();
        public List<NpcCandidateHit> Hits { get; set; } = new List<NpcCandidateHit>();

    }
}
