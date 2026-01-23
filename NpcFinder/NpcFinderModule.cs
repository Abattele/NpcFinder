using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD;
using Blish_HUD.Content;
using Blish_HUD.Controls;
using Blish_HUD.Modules;
using Blish_HUD.Modules.Managers;
using Blish_HUD.Settings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NpcFinder.Controls;
using NpcFinder.Models;
using NpcFinder.Services;
using NpcFinder.Util;
using Point = Microsoft.Xna.Framework.Point;

namespace NpcFinder
{

    [Export(typeof(Module))]
    public class NpcFinderModule : Module
    {

        // ---- imports ----
        internal SettingsManager SettingsManager => ModuleParameters.SettingsManager;
        internal ContentsManager ContentsManager => ModuleParameters.ContentsManager;
        internal DirectoriesManager DirectoriesManager => ModuleParameters.DirectoriesManager;
        internal Gw2ApiManager Gw2ApiManager => ModuleParameters.Gw2ApiManager;
        internal static NpcFinderModule ExampleModuleInstance;




        // ---- fields ----



        private static readonly bool DEBUG_LOGS = false; // instead of using property file i'm using constant for easier dev toggling in each class



        private ChangelogWindow _changelogWindow;


        private static readonly string[] CHANGELOG_PAGE_TITLES =
        {
            "Latest (v1.2.0)",
            "v1.1.0",
            "v1.0.0"
        };


        // i need to use @ for the text for the user so i can't make this identation uniform in the code...
        private static readonly string[] CHANGELOG_PAGE_TEXTS =
        {

@"
v1.2.0
- Added search by MAP feature (it is possible not all NPCs of that map will show)
- Optimized NPC search (now takes around 5-10 seconds max to find most NPCs)
- Fixed marker jitter
- Safety improvements
- UI improvements (+changelog)
- Increase cache size limits (stored up to 25 days)
",

@"
v1.1.0
- Huge performance improvements
- Much better precision (works for most of the NPCs now)
- Added Suggestions panel
- Added a marker that displays on the corner if it's off-screen
- Stopped it from opening by itself.
- Added changelog window
- Improved NPC title suggestions (prefix + search + scoring)
- Anchors fallback restored when no coordinates are parsed
- Better caching system
- UI improvements

! Some NPCs may take a bit longer to resolve the position the first time 
(due to caching) -> be patient (around max 2-3 minutes)
            
** For the next version (v1.2.0) I'm planning to add a feature to search by MAP 
and to display all the NPCs on that map ** 
            
** Also I will try to fix the small offset of the marker when moving the map 
in the next version **
",
            
            
@"
v1.0.0
- Initial release
- Basic NPC search + marker
"

        };





        private static readonly Logger Logger = Logger.GetLogger<NpcFinderModule>();
        private string _cacheDirPath;
        private string _merchantCacheDirPath;
        private Texture2D _cornerIconTexture;
        private CornerIcon _cornerIcon;
        private ContextMenuStrip _contextMenuStrip;
        private CancellationTokenSource _cts;
        private CacheStore _cache;
        private RateLimiter _rate;
        private WikiNpcService _wiki;
        private Gw2MapIndexService _mapIndex;
        private Gw2ApiService _gw2;
        private Gw2MapDetailsService _details;
        private NpcMerchantResolverService _merchantResolver;
        private NpcFinderWindow _npcWindow;
       // private BigMapOverlayControl _overlay; // minimap overlay (not used yet) TODO
        private BigMapOverlayControl _bigMapOverlay;
        private NpcTarget _currentTarget;
        private int _currentContinentId;
        private int _lastMapId;
        private double _pollMs;

        [ImportingConstructor]
        public NpcFinderModule([Import("ModuleParameters")] ModuleParameters moduleParameters) : base(moduleParameters)
        {
            ExampleModuleInstance = this;
        }

        protected override void DefineSettings(SettingCollection settings)
        {

        }

        private void ClearCurrentMarker()
        {
            _currentTarget = null;

            // force repaint if map is open (and even if it’s not)
            _bigMapOverlay?.Invalidate();

            if (DEBUG_LOGS) {
                Logger.Warn("[Target] CLEARED (currentTarget=null)");
            }

        }

        private void DeleteAllNpcFinderCache()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_cacheDirPath))
                {
                    Logger.Warn("[Cache] Delete requested but _cacheDirPath is null/empty.");
                    return;
                }

                // safety: only delete folder name
                var folderName = Path.GetFileName(_cacheDirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.Equals(folderName, "NpcFinderCache", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn("[Cache] Refusing to delete unexpected folder: " + _cacheDirPath);
                    return;
                }

                // delete root cache
                if (Directory.Exists(_cacheDirPath))
                    Directory.Delete(_cacheDirPath, recursive: true);

                // recreate root cache folder
                Directory.CreateDirectory(_cacheDirPath);

                // recreate merchant cache folder too (otherwise resolver can't write cache anymore)
                if (!string.IsNullOrWhiteSpace(_merchantCacheDirPath))
                    Directory.CreateDirectory(_merchantCacheDirPath);

                if (DEBUG_LOGS)
                {
                    Logger.Warn("[Cache] Deleted and recreated: " + _cacheDirPath +
                            " (merchant=" + (_merchantCacheDirPath ?? "null") + ")");
                }

            }
            catch (Exception ex)
            {
                Logger.Warn("Exception [Cache] Delete failed: " + ex);
            }
        }


        private void EnsureChangelogWindow()
        {
            if (_changelogWindow != null) return;

            var bg = AsyncTexture2D.FromAssetId(155997);
            _changelogWindow = new ChangelogWindow(bg, CHANGELOG_PAGE_TEXTS, CHANGELOG_PAGE_TITLES)
            {
                Parent = GameService.Graphics.SpriteScreen,
                Location = new Point(340, 240),
                Id = $"{nameof(NpcFinderModule)}_ChangelogWindow",
                SavesPosition = true

            };
        }

        protected override async Task LoadAsync()
        {

            MumbleReader.ResetDiscovery();

            // textures
            _cornerIconTexture = ContentsManager.GetTexture("assets/cornerIconTexture.png");
            var windowBackgroundTexture = AsyncTexture2D.FromAssetId(155997);

            _cts = new CancellationTokenSource();

            // cache folder
            string rootDir = null;
            foreach (var d in DirectoriesManager.RegisteredDirectories)
            {
                var p = DirectoriesManager.GetFullDirectoryPath(d);
                if (!string.IsNullOrWhiteSpace(p)) { rootDir = p; break; }
            }

            _cacheDirPath = Path.Combine(rootDir ?? Path.GetTempPath(), "NpcFinderCache");
            _cache = new CacheStore(_cacheDirPath);


            if (DEBUG_LOGS)
            {
                Logger.Info($"[Cache] rootDir='{rootDir ?? "(null)"}'");
                Logger.Info($"[Cache] cachePath='{System.IO.Path.Combine(rootDir ?? Path.GetTempPath(), "NpcFinderCache")}'");
            }

            // services
            _rate = new RateLimiter(250);
            _wiki = new WikiNpcService(_rate, _cache);

            _mapIndex = new Gw2MapIndexService(Gw2ApiManager.Gw2ApiClient.V2, _cache);
            _gw2 = new Gw2ApiService(Gw2ApiManager.Gw2ApiClient.V2, _cache);

            // map details (POIs / waypoints)
            _details = new Gw2MapDetailsService(_cache);

            // merchant resolver cache folder INSIDE the same cache root already computed
            _merchantCacheDirPath = Path.Combine(_cacheDirPath, "merchant");
            try { Directory.CreateDirectory(_merchantCacheDirPath); } catch { }

            // merchant resolver (no direct coords -> anchor via POI/WP) + caching
            _merchantResolver = new NpcMerchantResolverService(_wiki, _mapIndex, _gw2, _details, _merchantCacheDirPath);


            // window
            _npcWindow = new NpcFinderWindow(
                windowBackgroundTexture,
                _wiki,
                _mapIndex,
                _gw2,
                _merchantResolver,
                _cts,
                () => _currentContinentId,
                (t) => {
                    _currentTarget = t;

                    _bigMapOverlay?.Invalidate(); // helps immediate feedback
                    if (DEBUG_LOGS)
                    {
                        Logger.Warn($"[Target] SET: {(t == null ? "null" : $"{t.MapName} cont={t.TargetContinentId} cx={t.TargetContinentX} cy={t.TargetContinentY}")}");
                    }
                },

                () => {
                    ClearCurrentMarker();
                },

                () => { DeleteAllNpcFinderCache(); }

            )
            {
                Parent = GameService.Graphics.SpriteScreen,
                Location = new Point(300, 300),
                Id = $"{nameof(NpcFinderModule)}_NpcFinderWindow",
                SavesPosition = true
            };



            // create overlay once
            _bigMapOverlay = new BigMapOverlayControl
            {
                Parent = GameService.Graphics.SpriteScreen,
                Location = new Point(0, 0),
                Size = GameService.Graphics.SpriteScreen.Size,
                Visible = true,
                ZIndex = int.MaxValue,    
                ClipsBounds = false,
                TargetProvider = () => _currentTarget,
                CurrentContinentIdProvider = () => _currentContinentId,

            };


            GameService.Graphics.SpriteScreen.Resized += (_, __) =>
            {
                _bigMapOverlay.Size = GameService.Graphics.SpriteScreen.Size;
            };


            CreateCornerIconWithContextMenu();

            await Task.CompletedTask;
        }


        protected override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            if (GameService.Gw2Mumble?.UI?.IsMapOpen ?? false)
            {
                _bigMapOverlay?.Invalidate();
            }

            // poll current map -> continent (for warning + overlay gating)
            _pollMs += gameTime.ElapsedGameTime.TotalMilliseconds;
            if (_pollMs < 1000) return;
            _pollMs = 0;

            MumbleReader.DumpUiOncePerSecond(requireMapOpen: true);

            int mapId;
            if (!MumbleReader.TryGetMapId(out mapId)) return;
            if (mapId == _lastMapId) return;
            _lastMapId = mapId;

            Task.Run(async () => {
                try
                {
                    var mi = await _gw2.GetMapInfoAsync(mapId, _cts.Token).ConfigureAwait(false);
                    _currentContinentId = mi != null ? mi.ContinentId : 0;
                }
                catch
                {
                    _currentContinentId = 0;
                }
            });
        }

        protected override void Unload()
        {
            try { _cts?.Cancel(); } catch { }

            _npcWindow?.Dispose();
            _changelogWindow?.Dispose();
            _bigMapOverlay?.Dispose();

            _cornerIcon?.Dispose();
            _contextMenuStrip?.Dispose();

            _cornerIconTexture?.Dispose();

            try { _cts?.Dispose(); } catch { }

            ExampleModuleInstance = null;
        }

        private void CreateCornerIconWithContextMenu()
        {
            _cornerIcon = new CornerIcon()
            {
                Icon = _cornerIconTexture,
                BasicTooltipText = "NPC Finder",
                Priority = 1645843523,
                Parent = GameService.Graphics.SpriteScreen
            };

            _cornerIcon.Click += (s, e) => {
                if (_npcWindow == null) return;
                if (!_npcWindow.Visible) _npcWindow.Show();
                else _npcWindow.ToggleWindow();
            };

            _contextMenuStrip = new ContextMenuStrip();
            _contextMenuStrip.AddMenuItem("Changelog / Patch notes").Click += (s, e) =>
            {
                try
                {
                    EnsureChangelogWindow();

                    // parent is important or it may not render on some setups
                    if (_changelogWindow.Parent == null)
                        _changelogWindow.Parent = GameService.Graphics.SpriteScreen;

                    if (!_changelogWindow.Visible) _changelogWindow.Show();
                    else _changelogWindow.ToggleWindow();
                }
                catch (Exception ex)
                {
                    Logger.Warn("[Changelog] Failed to open: " + ex);
                }
            };

            _cornerIcon.Menu = _contextMenuStrip;
        }

    }

}
