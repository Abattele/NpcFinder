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
        private BigMapOverlayControl _overlay; // minimap overlay (not used yet) TODO
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
            // not needed for now
        }

        private void ClearCurrentMarker()
        {
            _currentTarget = null;

            // force repaint if map is open (and even if it’s not)
            _bigMapOverlay?.Invalidate();

            Logger.Warn("[Target] CLEARED (currentTarget=null)");
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

                // Safety: only delete folder name
                var folderName = Path.GetFileName(_cacheDirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.Equals(folderName, "NpcFinderCache", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn("[Cache] Refusing to delete unexpected folder: " + _cacheDirPath);
                    return;
                }

                // Delete root cache
                if (Directory.Exists(_cacheDirPath))
                    Directory.Delete(_cacheDirPath, recursive: true);

                // Recreate root cache folder
                Directory.CreateDirectory(_cacheDirPath);

                // Recreate merchant cache folder too (otherwise resolver can't write cache anymore)
                if (!string.IsNullOrWhiteSpace(_merchantCacheDirPath))
                    Directory.CreateDirectory(_merchantCacheDirPath);

                Logger.Warn("[Cache] Deleted and recreated: " + _cacheDirPath +
                            " (merchant=" + (_merchantCacheDirPath ?? "null") + ")");
            }
            catch (Exception ex)
            {
                Logger.Warn("[Cache] Delete failed: " + ex);
            }
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


            Logger.Info($"[Cache] rootDir='{rootDir ?? "(null)"}'");
            Logger.Info($"[Cache] cachePath='{System.IO.Path.Combine(rootDir ?? Path.GetTempPath(), "NpcFinderCache")}'");


            // services
            _rate = new RateLimiter(250);
            _wiki = new WikiNpcService(_rate, _cache);

            _mapIndex = new Gw2MapIndexService(Gw2ApiManager.Gw2ApiClient.V2, _cache);
            _gw2 = new Gw2ApiService(Gw2ApiManager.Gw2ApiClient.V2, _cache);

            // map details (POIs / waypoints)
            _details = new Gw2MapDetailsService(_cache);

            // merchant resolver cache folder INSIDE the same cache root already computed
            _merchantCacheDirPath = Path.Combine(_cacheDirPath, "merchant");
            try { Directory.CreateDirectory(_merchantCacheDirPath); } catch { /* ignore */ }

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
                    Logger.Warn($"[Target] SET: {(t == null ? "null" : $"{t.MapName} cont={t.TargetContinentId} cx={t.TargetContinentX} cy={t.TargetContinentY}")}");
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



            // Create overlay once
            _bigMapOverlay = new BigMapOverlayControl
            {
                Parent = GameService.Graphics.SpriteScreen,
                Location = new Point(0, 0),
                Size = GameService.Graphics.SpriteScreen.Size,
                Visible = true,
                ZIndex = int.MaxValue,     // drawn on top of the world map
                ClipsBounds = false,
                TargetProvider = () => _currentTarget,
                CurrentContinentIdProvider = () => _currentContinentId,

            };


            GameService.Graphics.SpriteScreen.Resized += (_, __) =>
            {
                _bigMapOverlay.Size = GameService.Graphics.SpriteScreen.Size;
            };


            CreateCornerIconWithContextMenu();

            // show once by default
            _npcWindow.Show();

            await Task.CompletedTask;
        }

        protected override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            // poll current map -> continent (for warning + overlay gating)
            _pollMs += gameTime.ElapsedGameTime.TotalMilliseconds;
            if (_pollMs < 1000) return;
            _pollMs = 0;


            // Only repaint when map is open (otherwise it's wasted)
            if (GameService.Gw2Mumble.UI.IsMapOpen)
            {
                _bigMapOverlay?.Invalidate();
            }

            MumbleReader.DumpUiOncePerSecond(requireMapOpen: true);



            _overlay?.Invalidate();

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
            _overlay?.Dispose();
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
            _contextMenuStrip.AddMenuItem("NPC Finder (toggle)").Click += (s, e) => {
                if (_npcWindow == null) return;
                if (!_npcWindow.Visible) _npcWindow.Show();
                else _npcWindow.ToggleWindow();
            };

            _cornerIcon.Menu = _contextMenuStrip;
        }

    }
}
