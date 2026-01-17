using System;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD.Controls;
using Blish_HUD.Content;
using Microsoft.Xna.Framework;
using NpcFinder.Models;
using NpcFinder.Services;
using NpcFinder.Util;

namespace NpcFinder.Controls
{
    public class NpcFinderWindow : StandardWindow
    {
        private readonly WikiNpcService _wiki;
        private readonly Gw2MapIndexService _mapIndex;
        private readonly Gw2ApiService _gw2;
        private readonly NpcMerchantResolverService _merchantResolver;
        private readonly CancellationTokenSource _cts;

        private readonly Func<int> _currentContinentIdProvider;
        private readonly Action<NpcTarget> _setTarget;

        private Panel _contentRoot;
        private Panel _resultsViewport;

        private TextBox _searchBox;
        private StandardButton _searchBtn;
        private FlowPanel _resultsPanel;
        private Label _status;

        private StandardButton _clearBtn;
        private readonly Action _clearTarget;

        private readonly Action _clearCache;
        private StandardButton _clearCacheBtn;

        public NpcFinderWindow(
            AsyncTexture2D background,
            WikiNpcService wiki,
            Gw2MapIndexService mapIndex,
            Gw2ApiService gw2,
            NpcMerchantResolverService merchantResolver,
            CancellationTokenSource cts,
            Func<int> currentContinentIdProvider,
            Action<NpcTarget> setTarget,
            Action clearTarget,
            Action clearCache            
        )
        : base(
              background,
              new Rectangle(5, 60, 600, 550),
              new Rectangle(40, 70, 520, 310)
          )
        {
            _wiki = wiki;
            _mapIndex = mapIndex;
            _gw2 = gw2;
            _merchantResolver = merchantResolver;
            _cts = cts;

            _currentContinentIdProvider = currentContinentIdProvider;
            _setTarget = setTarget;

            _clearTarget = clearTarget;
            _clearCache = clearCache;

            Title = "Abattele's NPC Finder";
            CanResize = false;
            SavesPosition = true;

            BuildUi();
        }

        private void BuildUi()
        {
            _contentRoot = new Panel()
            {
                Parent = this,
                Location = new Point(40, 40),
                Size = new Point(520, 340),
                ClipsBounds = false
            };

            _searchBox = new TextBox()
            {
                Parent = _contentRoot,
                Location = new Point(0, 0),
                Width = 360,
                PlaceholderText = "NPC name..."
            };

            _searchBtn = new StandardButton()
            {
                Parent = _contentRoot,
                Location = new Point(370, -2),
                Size = new Point(110, 34),
                Text = "Search"
            };
            _searchBtn.Click += async (s, e) => await DoSearchAsync();

            _status = new Label()
            {
                Parent = _contentRoot,
                Location = new Point(0, 40),
                AutoSizeWidth = true,
                Text = "Awaiting NPC selection from the list below..."
            };

            _resultsViewport = new Panel()
            {
                Parent = _contentRoot,
                Location = new Point(0, 70),
                Size = new Point(520, 230),
                ClipsBounds = true
            };

            _resultsPanel = new FlowPanel()
            {
                Parent = _resultsViewport,
                Location = new Point(0, 0),
                Size = _resultsViewport.Size,
                CanScroll = true,
                FlowDirection = ControlFlowDirection.SingleTopToBottom,
                ControlPadding = new Vector2(0, 4)
            };

            _clearBtn = new StandardButton()
            {
                Parent = _contentRoot,
                Location = new Point(0, 306),
                Size = new Point(220, 34),
                Text = "Remove current marker"
            };
            _clearBtn.Click += (s, e) =>
            {
                _clearTarget?.Invoke();
                _status.Text = "Marker removed.";
                _resultsPanel.ClearChildren();
            };

            _clearCacheBtn = new StandardButton()
            {
                Parent = _contentRoot,
                Location = new Point(230, 306),
                Size = new Point(140, 34),
                Text = "Delete cache"
            };
            _clearCacheBtn.Click += (s, e) =>
            {
                try
                {
                    _clearCache?.Invoke();
                    _status.Text = "Cache deleted.";
                    _resultsPanel.ClearChildren();
                }
                catch (Exception ex)
                {
                    _status.Text = "Cache delete failed: " + ex.Message;
                }
            };
        }

        private async Task DoSearchAsync()
        {
            string q = (_searchBox.Text ?? "").Trim();
            if (q.Length == 0) return;

            _resultsPanel.ClearChildren();
            _status.Text = "Searching wiki...";

            try
            {
                var res = await _wiki.ResolveByNpcNameAsync(q, _cts.Token);

                if (res == null) { _status.Text = "No results."; return; }

                if (res.CandidateTitles != null && res.CandidateTitles.Count > 1 &&
                    (res.Hits == null || res.Hits.Count == 0))
                {
                    _status.Text = "Multiple pages found. Pick one:";
                    foreach (var t in res.CandidateTitles) AddTitleChoice(t);
                    return;
                }

                // direct hits
                if (res.Hits != null && res.Hits.Count > 0)
                {
                    _status.Text = $"Found {res.Hits.Count} hit(s). Resolving maps...";
                    foreach (var h in res.Hits)
                        await AddResolvedHitAsync(h);

                    _status.Text = "Done. Click a hit to set marker.";
                    return;
                }

                _status.Text = "Found page, but no coordinates parsed.";
            }
            catch (OperationCanceledException)
            {
                _status.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                _status.Text = "Error: " + ex.Message;
            }
        }

        private void AddTitleChoice(string title)
        {
            var btn = new StandardButton()
            {
                Parent = _resultsPanel,
                Size = new Point(_resultsViewport.Width - 25, 34),
                Text = title
            };

            btn.Click += async (s, e) =>
            {
                _resultsPanel.ClearChildren();
                _status.Text = "Loading page...";

                try
                {
                    var res = await _wiki.ResolveByTitleAsync(title, _cts.Token);

                    // if wiki has no [x,y], try merchant resolver
                    if (res == null || res.Hits == null || res.Hits.Count == 0)
                    {
                        if (_merchantResolver == null)
                        {
                            _status.Text = "Merchant resolver not initialized.";
                            return;
                        }

                        _status.Text = "No direct coordinates. Resolving via GW2 API...";
                        var resolved = await _merchantResolver.ResolveMerchantAsync(title, _cts.Token);

                        if (resolved == null || resolved.Count == 0)
                        {
                            _status.Text = "No anchor found via GW2 API.";
                            return;
                        }

                        _status.Text = "Pick an anchor location:";
                        foreach (var r in resolved) AddResolvedRow(r);
                        return;
                    }

                    _status.Text = $"Found {res.Hits.Count} hit(s). Resolving maps...";
                    foreach (var h in res.Hits) await AddResolvedHitAsync(h);
                    _status.Text = "Done. Click a hit to set marker.";
                }
                catch (Exception ex)
                {
                    _status.Text = "Error: " + ex.Message;
                }
            };
        }

        private void AddResolvedRow(NpcResolvedHit r)
        {
            var btn = new StandardButton
            {
                Parent = _resultsPanel,
                Size = new Point(_resultsViewport.Width - 25, 34),
                Text = $"{r.MapName} | {r.Source}"
            };

            btn.Click += async (s, e) =>
            {
                _status.Text = "Setting marker...";

                var mapInfo = await _gw2.GetMapInfoAsync(r.MapId, _cts.Token);

                var target = new NpcTarget
                {
                    WikiTitle = r.Title,
                    DisplayName = r.Title,
                    MapId = r.MapId,
                    MapName = r.MapName,
                    TargetContinentId = r.ContinentId,
                    TargetContinentX = r.ContinentX,
                    TargetContinentY = r.ContinentY,
                    MapInfo = mapInfo
                };

                Blish_HUD.Logger.GetLogger<NpcFinderWindow>()
                    .Warn($"[UI] Row clicked -> setting target: map={target.MapName} cont={target.TargetContinentId}");

                MumbleReader.DumpUiOnce();

                _setTarget?.Invoke(target);

                int cur = _currentContinentIdProvider != null ? _currentContinentIdProvider() : 0;
                if (cur != 0 && target.TargetContinentId != 0 && cur != target.TargetContinentId)
                    _status.Text = $"You are on {ContinentNames.Name(cur)}; target is on {ContinentNames.Name(target.TargetContinentId)}.";
                else
                    _status.Text = $"Marker set ({r.Source}). Open world map.";
            };
        }

        private async Task AddResolvedHitAsync(NpcCandidateHit h)
        {
            int? mapId = h.MapId;
            if (mapId == null && !string.IsNullOrWhiteSpace(h.MapName))
            {
                mapId = await _mapIndex.ResolveMapIdByNameAsync(h.MapName, _cts.Token);
            }

            Gw2MapInfo mapInfo = null;
            double cx = 0, cy = 0;

            if (mapId != null)
            {
                mapInfo = await _gw2.GetMapInfoAsync(mapId.Value, _cts.Token);
                if (mapInfo != null)
                {
                    var cont = CoordConverter.MapToContinent(h.X, h.Y, mapInfo.MapRect, mapInfo.ContinentRect);
                    cx = cont.cx; cy = cont.cy;
                }
            }

            int curCont = _currentContinentIdProvider != null ? _currentContinentIdProvider() : 0;
            int hitCont = mapInfo != null ? mapInfo.ContinentId : 0;

            string mapLabel = mapInfo != null ? mapInfo.Name : (h.MapName ?? "(unknown map)");
            string contLabel = (hitCont != 0) ? ContinentNames.Name(hitCont) : "Unknown continent";
            string warn = (curCont != 0 && hitCont != 0 && curCont != hitCont) ? " !!! different continent" : "";

            var btn = new StandardButton()
            {
                Parent = _resultsPanel,
                Size = new Point(_resultsViewport.Width - 25, 34),
                Text = $"{mapLabel} | [{h.X},{h.Y}] | {contLabel}{warn}"
            };

            btn.Click += (s, e) =>
            {
                if (mapInfo == null)
                {
                    _status.Text = "Can't place marker: map unresolved.";
                    return;
                }

                var target = new NpcTarget
                {
                    WikiTitle = h.Title,
                    DisplayName = h.Title,
                    MapId = mapInfo.Id,
                    MapName = mapInfo.Name,
                    TargetMapX = h.X,
                    TargetMapY = h.Y,
                    TargetContinentId = mapInfo.ContinentId,
                    TargetContinentX = cx,
                    TargetContinentY = cy,
                    MapInfo = mapInfo
                };

                if (curCont != 0 && target.TargetContinentId != 0 && curCont != target.TargetContinentId)
                    _status.Text = $"You are on {ContinentNames.Name(curCont)}. Target is on {ContinentNames.Name(target.TargetContinentId)}. Teleport there, then open map.";
                else
                    _status.Text = $"Marker set: {target.MapName} [{target.TargetMapX},{target.TargetMapY}]";

                _setTarget?.Invoke(target);
            };
        }
    }
}
