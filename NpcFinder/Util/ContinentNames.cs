namespace NpcFinder.Util
{
    public static class ContinentNames
    {
        public static string Name(int id)
        {
            if (id == 1) return "Tyria (Central Tyria / Kryta)";
            if (id == 2) return "The Mists";
            if (id == 3) return "Elona";
            if (id == 4) return "Cantha";
            return "Continent " + id;
        }
    }
}
