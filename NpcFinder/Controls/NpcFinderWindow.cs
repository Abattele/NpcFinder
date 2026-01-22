using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD.Content;
using Blish_HUD.Controls;
using Microsoft.Xna.Framework;
using NpcFinder.Models;
using NpcFinder.Services;
using NpcFinder.Util;

namespace NpcFinder.Controls
{

    public class NpcFinderWindow : StandardWindow
    {


        private static readonly bool DEBUG_LOGS = false;



        private CancellationToken _activeSearchToken;
        private FlowPanel _suggestPanel;
        private int _suggestReqId = 0;

        private readonly SemaphoreSlim _suggestGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _suggestCts;

        private const int MaxSuggestButtons = 12;
        private StandardButton[] _suggestBtns;
        private string[] _suggestValues;

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

        //will implement peterson algorithm for critical section for concurrent searches...
        private readonly SemaphoreSlim _searchGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _searchCts;

        private readonly Dictionary<long, Gw2MapInfo> _continentPointMemo = new Dictionary<long, Gw2MapInfo>();


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

                  new Rectangle(5, 60, 580, 590),     // was 550
                  new Rectangle(40, 70, 480, 350)     // was 310
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



        private static void MakeNonFocusable(object control)
        {
            if (control == null) return;

            try
            {
                var t = control.GetType();
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;

                // try common focus properties across Blish builds
                foreach (
                    var propName in new[] {"CanFocus", "CanReceiveFocus", "IsFocusable", "Focusable","CanBeFocused", "CanTakeFocus"}
                )
                {
                    var p = t.GetProperty(propName, flags);
                    if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
                    {
                        p.SetValue(control, false, null);
                        break;
                    }
                }

                // some builds use fields instead of properties
                foreach (
                    var fieldName in new[]{"CanFocus", "IsFocusable", "Focusable"}
                )
                {
                    var f = t.GetField(fieldName, flags);
                    if (f != null && f.FieldType == typeof(bool))
                    {
                        f.SetValue(control, false);
                        break;
                    }
                }
            }
            catch { }
        }

        private static string Norm(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim().ToLowerInvariant();

            // lightweight normalize (enough for ranking)
            return s.Replace("_", " ");
        }

        private static int ScoreTitle(string title, string q)
        {
            // higher score = better
            // goal: exact > startswith > word-start > contains
            var t = Norm(title);
            var qq = Norm(q);

            if (t == qq) return 1000;

            // starts with query (best for prefix)
            if (t.StartsWith(qq)) return 900;

            // starts with "the query" after punctuation/space: "farmer arlo" contains word-start "arlo"
            if (t.Contains(" " + qq)) return 780;

            // contains query anywhere
            if (t.Contains(qq)) return 650;

            // i'm sure there are other ways to improve this... TODO later

            // fallback
            return 0;
        }



        private void RenderSuggestions(List<(string label, string value)> merged)
        {
            if (_suggestBtns == null || _suggestValues == null) return;

            int n = Math.Min(MaxSuggestButtons, merged?.Count ?? 0);

            for (int i = 0; i < MaxSuggestButtons; i++)
            {
                var b = _suggestBtns[i];
                if (b == null) continue;

                if (i < n)
                {
                    b.Text = merged[i].label;
                    _suggestValues[i] = merged[i].value;
                    b.Visible = true;
                }
                else
                {
                    b.Visible = false;
                    _suggestValues[i] = null;
                }
            }

            _suggestPanel.Visible = (n > 0);

            ForceFlowPanelLayout(_suggestPanel);
        }

        private void HideSuggestions()
        {
            if (_suggestPanel == null) return;

            if (_suggestBtns != null)
            {
                for (int i = 0; i < _suggestBtns.Length; i++)
                {
                    if (_suggestBtns[i] != null) _suggestBtns[i].Visible = false;
                    if (_suggestValues != null && i < _suggestValues.Length) _suggestValues[i] = null;
                }
            }

            _suggestPanel.Visible = false;

            // keep layout sane for next show
            ForceFlowPanelLayout(_suggestPanel);
        }


        private void CancelSuggest()
        {
            try { _suggestCts?.Cancel(); } catch { }
            try { _suggestCts?.Dispose(); } catch { }
            _suggestCts = null;
        }

        private CancellationToken BeginSuggestToken()
        {
            CancelSuggest();
            _suggestCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            return _suggestCts.Token;
        }

        private async Task<List<(string label, string value)>> BuildMergedSuggestionsAsync(string text, CancellationToken ct)
        {
            // 1/ maps
            var mapTask = _mapIndex.SuggestMapNamesAsync(text, 6, ct);

            // 2/ wiki prefix suggestions 
            var sugTask = _wiki.SuggestTitlesAsync(text, 10, ct);

            // 3/ wiki search (contains match) — only when input is long enough
            Task<List<string>> searchTask = Task.FromResult(new List<string>());
            if ((text?.Trim().Length ?? 0) >= 3)
                searchTask = _wiki.SearchTitlesAsync(text, 20, ct); // do 20 to have more to pick from

            await Task.WhenAll(mapTask, sugTask, searchTask).ConfigureAwait(false);

            var mapSug = mapTask.Result ?? new List<string>();
            var wikiSug = sugTask.Result ?? new List<string>();
            var wikiFind = searchTask.Result ?? new List<string>();

            // merge without dupes
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var merged = new List<(string label, string value, int score)>();

            // maps first, but slightly lower score so exact NPC titles can outrank
            foreach (var m in mapSug)
            {
                if (string.IsNullOrWhiteSpace(m)) continue;
                if (seen.Add(m))
                    merged.Add(($"Map: {m}", m, 400));
            }

            // wiki suggestions + wiki search combined, scored
            void addWikiList(IEnumerable<string> titles)
            {
                foreach (var t in titles)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    if (!seen.Add(t)) continue;
                    merged.Add((t, t, ScoreTitle(t, text)));
                }
            }

            addWikiList(wikiSug);
            addWikiList(wikiFind);

            // sort by score desc, then alphabetically... i think i could've used a min-heap here but this works too...
            var ordered = merged
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.value, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSuggestButtons)
                .Select(x => (x.label, x.value))
                .ToList();

            return ordered;
        }


        private async Task UpdateSuggestionsAsync()
        {
            int myId = Interlocked.Increment(ref _suggestReqId);

            string q = (_searchBox.Text ?? "").Trim();
            if (q.Length < 2)
            {
                HideSuggestions();
                return;
            }

            await _suggestGate.WaitAsync();
            try
            {
                // if another call came in while we waited then stop (critical section)
                if (myId != _suggestReqId) return;

                var ct = BeginSuggestToken();

                // debounce
                await Task.Delay(250, ct);
                ct.ThrowIfCancellationRequested();

                // if user typed again during debounce then stop (critical section)
                if (myId != _suggestReqId) return;

                // re-read text after debounce
                q = (_searchBox.Text ?? "").Trim();
                if (q.Length < 2)
                {
                    HideSuggestions();
                    return;
                }

                // multi-suggestions (maps + wiki)
                var merged = await BuildMergedSuggestionsAsync(q, ct);
                ct.ThrowIfCancellationRequested();

                if (myId != _suggestReqId) return;

                if (merged == null || merged.Count == 0)
                {
                    HideSuggestions();
                    return;
                }

                // no focus stealing
                RenderSuggestions(merged);

            }
            catch (OperationCanceledException)
            {

            }
            catch
            {
                HideSuggestions();
            }
            finally
            {
                _suggestGate.Release(); // semafore disable if all good (or error) so we can retry same section later
            }
        }


        private void BuildUi()
        {

            var cr = this.ContentRegion;

            _contentRoot = new Panel()
            {
                Parent = this,
                Location = new Point(40, 40),
                Size = new Point(520, 400),
                ClipsBounds = false
            };

            const int pad = 10;

            _searchBox = new TextBox()
            {
                Parent = _contentRoot,
                Location = new Point(0, 0),
                Width = cr.Width - pad * 3 - 110,
                PlaceholderText = "NPC name..."
            };



            _suggestPanel = new FlowPanel()
            {
                Parent = _contentRoot,
                Location = new Point(_searchBox.Location.X, _searchBox.Location.Y + 34),
                Size = new Point(_searchBox.Width, 120),
                ClipsBounds = true,
                CanScroll = true,
                FlowDirection = ControlFlowDirection.SingleTopToBottom,
                ControlPadding = new Vector2(0, 2),
                Visible = false
            };

            // suggestion
            _suggestBtns = new StandardButton[MaxSuggestButtons];
            _suggestValues = new string[MaxSuggestButtons];

            for (int i = 0; i < MaxSuggestButtons; i++)
            {
                int idx = i;

                var b = new StandardButton()
                {
                    Parent = _suggestPanel,
                    Size = new Point(_suggestPanel.Width - 18, 28),
                    Text = "",
                    Visible = false
                };

                MakeNonFocusable(b);

                b.Click += async (s, e) =>
                {
                    var val = _suggestValues[idx];
                    if (string.IsNullOrWhiteSpace(val)) return;
                    _searchBox.Text = val;
                    HideSuggestions();
                    await DoSearchAsync();
                };

                _suggestBtns[i] = b;
            }
            
            _searchBtn = new StandardButton()
            {
                Parent = _contentRoot,
                Location = new Point(_searchBox.Right + pad, _searchBox.Top - 2),
                Size = new Point(110, 34),
                Text = "Search"
            };


            _searchBtn.Click += async (s, e) => await DoSearchAsync();

            _status = new Label()
            {
                Parent = _contentRoot,
                Location = new Point(0, _suggestPanel.Location.Y + _suggestPanel.Height + 8),
                AutoSizeWidth = true,
                Text = "Awaiting NPC selection from the list below..."
            };

            int bottomButtonsH = 34;
            int bottomY = cr.Height - pad - bottomButtonsH;

            _resultsViewport = new Panel()
            {
                Parent = _contentRoot,
                Location = new Point(_searchBox.Left, _status.Bottom + 8),
                Size = new Point(cr.Width - pad * 2, bottomY - (_status.Bottom - 32)),
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


            //int bottomY = _contentRoot.Height - 34; // i hardcoded it later, so this is obsolete

            _clearBtn = new StandardButton()
            {
                Parent = _contentRoot,
                Location = new Point(_searchBox.Left, 355),

                Size = new Point(220, 34),
                Text = "Remove marker / Stop search"
            };
            _clearBtn.Click += (s, e) =>
            {
                // cancel any in-flight search first (just a small security reason to maintain the threads)
                StopSearch();

                _clearTarget?.Invoke();
                _resultsPanel.ClearChildren();
                _status.Text = "Marker removed (and search stopped if it was running).";
            };

            _clearCacheBtn = new StandardButton()
            {
                Parent = _contentRoot,
                Location = new Point(cr.Width - pad - 160, 355),
                Size = new Point(160, 34),

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
            
            MakeNonFocusable(_contentRoot);
            MakeNonFocusable(_resultsViewport);
            MakeNonFocusable(_resultsPanel);

            _searchBox.TextChanged += (s, e) => { _ = UpdateSuggestionsAsync(); };
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

                var mapInfo = await _gw2.GetMapInfoAsync(r.MapId, _activeSearchToken);

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

                if(DEBUG_LOGS)
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


        private async Task<Gw2MapInfo> ResolveMapInfoByContinentPointAsync(int cx, int cy, CancellationToken ct)
        {
            long key = (((long)cx) << 32) ^ (uint)cy;

            if (_continentPointMemo.TryGetValue(key, out var memo) && memo != null)
                return memo;

            var allMapIds = await _mapIndex.GetAllKnownMapIdsAsync(ct).ConfigureAwait(false);
            if (allMapIds == null || allMapIds.Count == 0) return null;

            int curCont = _currentContinentIdProvider != null ? _currentContinentIdProvider() : 0;

            bool Contains(Gw2MapInfo mi)
            {
                double minX = Math.Min(mi.ContinentRect.X1, mi.ContinentRect.X2);
                double maxX = Math.Max(mi.ContinentRect.X1, mi.ContinentRect.X2);
                double minY = Math.Min(mi.ContinentRect.Y1, mi.ContinentRect.Y2);
                double maxY = Math.Max(mi.ContinentRect.Y1, mi.ContinentRect.Y2);
                return (cx >= minX && cx <= maxX && cy >= minY && cy <= maxY);
            }

            // 1/ current continent first
            if (curCont != 0)
            {
                for (int i = 0; i < allMapIds.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if ((i % 20) == 0) await Task.Yield(); // keep GW2/Blish responsive

                    var mi = await _gw2.GetMapInfoAsync(allMapIds[i], ct).ConfigureAwait(false);
                    if (mi == null) continue;
                    if (mi.ContinentId != curCont) continue;

                    if (Contains(mi))
                    {
                        _continentPointMemo[key] = mi;
                        return mi;
                    }
                }
            }

            // 2/ any continent
            for (int i = 0; i < allMapIds.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if ((i % 20) == 0) await Task.Yield(); // keep GW2/Blish responsive

                var mi = await _gw2.GetMapInfoAsync(allMapIds[i], ct).ConfigureAwait(false);
                if (mi == null) continue;

                if (Contains(mi))
                {
                    _continentPointMemo[key] = mi;
                    return mi;
                }
            }
            return null;
        }


        private async Task AddResolvedHitAsync(NpcCandidateHit h, CancellationToken ct)
        {
            int curCont = _currentContinentIdProvider != null ? _currentContinentIdProvider() : 0;

            await Task.Yield();
            ct.ThrowIfCancellationRequested();

            int? mapId = h.MapId;

            // 1/ resolve mapId by name if possible
            if (mapId == null && !string.IsNullOrWhiteSpace(h.MapName))
                mapId = await _mapIndex.ResolveMapIdByNameAsync(h.MapName, ct);

            Gw2MapInfo mapInfo = null;

            if (mapId != null)
                mapInfo = await _gw2.GetMapInfoAsync(mapId.Value, ct);

            double cx = 0, cy = 0;
            bool mapResolved = (mapInfo != null);

            // small in-line helper to detect whether h.X/h.Y look like *map* coords for THIS map
            bool LooksLikeMapCoordsFor(Gw2MapInfo mi)
            {
                if (mi == null) return false;

                double minX = Math.Min(mi.MapRect.X1, mi.MapRect.X2);
                double maxX = Math.Max(mi.MapRect.X1, mi.MapRect.X2);
                double minY = Math.Min(mi.MapRect.Y1, mi.MapRect.Y2);
                double maxY = Math.Max(mi.MapRect.Y1, mi.MapRect.Y2);

                return (h.X >= minX && h.X <= maxX && h.Y >= minY && h.Y <= maxY);
            }

            // 2/ if map is resolved:
            //    - if coords fit MapRect => treat as map coords and convert
            //    - else => treat as continent coords directly
            if (mapResolved)
            {
                if (LooksLikeMapCoordsFor(mapInfo))
                {
                    var cont = CoordConverter.MapToContinent(h.X, h.Y, mapInfo.MapRect, mapInfo.ContinentRect);
                    cx = cont.cx;
                    cy = cont.cy;
                }
                else
                {
                    // “interactive map gives continent coords” cases (often lounge/odd pages too)
                    cx = h.X;
                    cy = h.Y;
                }
            }
            else
            {
                // 3/ fallback: infer map by continent point containment
                //    (actaully this is the expensive scan so it must be cancellable)
                bool looksLikeContinentCoords = (h.X > 20000 || h.Y > 20000);

                if (looksLikeContinentCoords)
                {
                    mapInfo = await ResolveMapInfoByContinentPointAsync(h.X, h.Y, ct);
                    if (mapInfo != null)
                    {
                        mapId = mapInfo.Id;
                        cx = h.X;
                        cy = h.Y;
                        mapResolved = true;
                    }
                }
            }

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

                    // keeping this for debug
                    TargetMapX = h.X,
                    TargetMapY = h.Y,

                    TargetContinentId = mapInfo.ContinentId,
                    TargetContinentX = cx,
                    TargetContinentY = cy,
                    MapInfo = mapInfo
                };

                _setTarget?.Invoke(target);

                if (curCont != 0 && target.TargetContinentId != 0 && curCont != target.TargetContinentId)
                    _status.Text = $"You are on {ContinentNames.Name(curCont)}. Target is on {ContinentNames.Name(target.TargetContinentId)}. Teleport there, then open map.";
                else
                    _status.Text = $"Marker set. Open world map.";
            };
        }



        private async Task DoSearchAsync()
        {

            HideSuggestions();
            CancelSuggest();

            string q = (_searchBox.Text ?? "").Trim();
            if (q.Length == 0) return;

            await _searchGate.WaitAsync(); // UI thread
            try
            {
                var ct = BeginNewSearchToken();

                _searchBtn.Enabled = false;
                _searchBox.Enabled = false;

                _resultsPanel.ClearChildren();
                _status.Text = "Searching wiki...";

                var res = await _wiki.ResolveByNpcNameAsync(q, ct);

                if (res == null) { _status.Text = "No results."; return; }

                if (res.CandidateTitles != null && res.CandidateTitles.Count > 1 &&
                    (res.Hits == null || res.Hits.Count == 0))
                {
                    _status.Text = "Multiple pages found. Pick one:";
                    foreach (var t in res.CandidateTitles) AddTitleChoice(t);
                    return;
                }

                if (res.Hits != null && res.Hits.Count > 0)
                {
                    _status.Text = $"Found {res.Hits.Count} hit(s). Resolving maps...";

                    var shownMapIds = new HashSet<int>();

                    // 1/ coordinate hits first
                    foreach (var h in res.Hits)
                    {
                        int? mid = await AddResolvedHitAsync_ReturnMapId(h, ct);
                        if (mid.HasValue) shownMapIds.Add(mid.Value);
                    }

                    // 2/ then anchors for other maps
                    if (_merchantResolver != null)
                    {
                        _status.Text = "Also checking other locations (anchors)...";
                        var anchors = await _merchantResolver.ResolveMerchantAsync(res.Title ?? q, ct);

                        if (anchors != null)
                        {
                            foreach (var a in anchors)
                            {
                                if (a == null) continue;
                                if (shownMapIds.Contains(a.MapId)) continue;
                                AddResolvedRow(a);
                                shownMapIds.Add(a.MapId);
                            }
                        }
                    }

                    _status.Text = "Done. Click a hit to set marker.";
                    return;
                }

                if (_merchantResolver != null)
                {
                    _status.Text = "No direct coordinates. Resolving via GW2 API (anchors)...";

                    var anchors = await _merchantResolver.ResolveMerchantAsync(res.Title ?? q, ct);
                    if (anchors != null && anchors.Count > 0)
                    {
                        _status.Text = "Pick an anchor location:";
                        foreach (var a in anchors)
                            AddResolvedRow(a);

                        return;
                    }

                    _status.Text = "Found page, but no coordinates parsed (and no anchor found).";
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
            finally
            {
                _searchBtn.Enabled = true;
                _searchBox.Enabled = true;
                _searchGate.Release();
            }
        }

        private static void ForceFlowPanelLayout(FlowPanel fp)
        {
            if (fp == null) return;

            try
            {
                // most versions at least repaint
                fp.Invalidate();

                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;

                // different Blish versions use different method names
                foreach (var name in new[] { "RecalculateLayout", "ReflowChildren", "UpdateLayout", "InvalidateLayout" })
                {
                    var m = fp.GetType().GetMethod(name, flags);
                    if (m != null && m.GetParameters().Length == 0)
                    {
                        m.Invoke(fp, null);
                        break;
                    }
                }
            }
            catch
            {

            }
        }

        private CancellationToken BeginNewSearchToken()
        {
            try { _searchCts?.Cancel(); } catch { }
            try { _searchCts?.Dispose(); } catch { }

            _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _activeSearchToken = _searchCts.Token;
            return _activeSearchToken;
        }

        private void StopSearch()
        {
            try { _searchCts?.Cancel(); } catch { }
            _searchBtn.Enabled = true;
            _searchBox.Enabled = true;
            _status.Text = "Search cancelled.";
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
                await _searchGate.WaitAsync(); // gate title-click resolves too
                try
                {
                    var ct = BeginNewSearchToken();

                    _searchBtn.Enabled = false;
                    _searchBox.Enabled = false;

                    _resultsPanel.ClearChildren();
                    _status.Text = "Loading page...";

                    var res = await _wiki.ResolveByTitleAsync(title, ct);

                    if (res == null || res.Hits == null || res.Hits.Count == 0)
                    {
                        if (_merchantResolver == null)
                        {
                            _status.Text = "Merchant resolver not initialized.";
                            return;
                        }

                        _status.Text = "No direct coordinates. Resolving via GW2 API...";
                        var resolved = await _merchantResolver.ResolveMerchantAsync(title, ct);

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

                    var shownMapIds = new HashSet<int>();

                    // 1/ coord hits first
                    foreach (var h in res.Hits)
                    {
                        int? mid = await AddResolvedHitAsync_ReturnMapId(h, ct);
                        if (mid.HasValue) shownMapIds.Add(mid.Value);
                    }

                    // 2/ anchors after
                    if (_merchantResolver != null)
                    {
                        _status.Text = "Also checking other locations (anchors)...";
                        var anchors = await _merchantResolver.ResolveMerchantAsync(title, ct);

                        if (anchors != null)
                        {
                            foreach (var a in anchors)
                            {
                                if (a == null) continue;
                                if (shownMapIds.Contains(a.MapId)) continue;
                                AddResolvedRow(a);
                                shownMapIds.Add(a.MapId);
                            }
                        }
                    }

                    _status.Text = "Done. Click a hit to set marker.";
                }
                catch (OperationCanceledException)
                {
                    _status.Text = "Cancelled.";
                }
                catch (Exception ex)
                {
                    _status.Text = "Error: " + ex.Message;
                }
                finally
                {
                    _searchBtn.Enabled = true;
                    _searchBox.Enabled = true;
                    _searchGate.Release();
                }
            };
        }



        private async Task<int?> AddResolvedHitAsync_ReturnMapId(NpcCandidateHit h, CancellationToken ct)
        {
            int? mapId = h.MapId;
            bool looksLikeContinentCoords = (h.X > 30000 || h.Y > 30000);

            if (mapId == null && !string.IsNullOrWhiteSpace(h.MapName))
                mapId = await _mapIndex.ResolveMapIdByNameAsync(h.MapName, ct);

            Gw2MapInfo mapInfo = null;

            if (mapId != null)
                mapInfo = await _gw2.GetMapInfoAsync(mapId.Value, ct);

            if (mapInfo == null && looksLikeContinentCoords)
            {
                mapInfo = await ResolveMapInfoByContinentPointAsync(h.X, h.Y, ct);
                if (mapInfo != null) mapId = mapInfo.Id;
            }

            await AddResolvedHitAsync(h,ct); // uses existing UI row builder

            return mapInfo?.Id ?? mapId;
        }


    }
}
