# NPC Finder (Blish HUD)

**NPC Finder** is a Blish HUD module for *Guild Wars 2* that lets you search for NPCs (and other wiki objects) and displays their location directly on the in-game world map.

## V1.1.0
- Huge performance increase (worst case scenario takes around 3 minutes)
- Huge precision increase (now works for most NPCs)
- A lot of bug fixes
- Basically changed pretty much everything... Gave up on my first data model, now i'm using weighted decisional trees to optimally traverse and score the predictions.

## V1.0.0
- Initial release

## Features
- Search NPCs by name using the GW2 Wiki
- Place a marker on the world map
- Cached results for fast repeat searches (14 Day TTL + option to manually clear it)
- Supports merchants and multi-map NPCs (and other objects, like various monsters, etc...)
- Lightweight, no API key required
- Uses GW2 Wiki APIs (you don't need a key for this)
- Dynamically computes the location if the wiki doesn't have the coordinates (or the interractive map) by using a scoring-prediction algorithm (nearest Waypoint then nearest PointOfInterest) - so no matter what, you'd still get results for the query you looked up.
- No hardcoded / predefined dataset used. Everything is dynamic and contextual (contextual to the Wiki and to the in-game location).
   
## Future plans
- Add support for the minimap
- Add support for dynamic path finding on the world map, though that would be very difficult because on-map coordinates != real world coordinates -> would need to apply a cartesian transform then to treat it as graphs for shortest achievable path (maybe use Dijkstra algorithm for that?)... so this feature won't be implemented/available anytime soon...

## Installation
1. Install **Blish HUD**
2. Open Blish HUD → **Module Repo**
3. Search for **NPC Finder**
4. Enable the module

*(Or download the '.bhm' file from the Releases page and drop it into your Blish HUD modules folder.)*

## Usage
1. Open the NPC Finder window in-game by clicking the marker icon.
2. Enter an NPC name and press Search (retrieves first 20 most relevant searches).
3. Select the correct result and wait for an Anchor to be generated (retrieves first 15 relevant anchors)
4. The NPC location will be highlighted on the map - open the map by pressing M and the marker should be there
5. You can remove the marker if you don't want it anymore by pressing the remove button
6. The brown main window doesn't need to be 'shown' for the marker to appear. The marker will persist even if you close the window. To remove the marker you have to press the remove button.

## Notes
- Results are cached locally to reduce wiki/API calls
- Some NPCs may have multiple possible locations - if that's the case, I've implemented various fallbacks and I only retrieve the wiki coordinates for the right map. For any other maps that don't have wiki coordinates, it looks for the nearest Waypoint first.

## Development
This project is built with:
- C#
- .NET
- Blish HUD SDK

'Properties/launchSettings.json' is used only for local development and debugging.

## License
MIT License

## Feedback
Suggestions, and contributions are welcome! Issues can be signaled as well. Unfortunately I don't really have a lot of spare time, but I will do my best to fix / improve the addon.

- GitHub Issues: https://github.com/Abattele/NpcFinder/issues
- In-game: **Abattele.8973**
