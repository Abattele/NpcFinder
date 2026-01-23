using System;
using System.Collections.Generic;
using Blish_HUD.Content;
using Blish_HUD.Controls;
using Microsoft.Xna.Framework;

namespace NpcFinder.Controls
{
    public sealed class ChangelogWindow : StandardWindow
    {

        private readonly IReadOnlyList<string> _pages;
        private readonly IReadOnlyList<string> _pageTitles;

        private int _pageIndex;

        private Panel _root;
        private Panel _viewport;

        private StandardButton _btnLatest;
        private StandardButton _btnOlder;
        private Label _pageInfo;

        private Label _text;

        public ChangelogWindow(AsyncTexture2D background, string changelogText)
            : this(background,
                  new[] { changelogText ?? "" },
                  new[] { "Latest" })
        {
        }

        // ctr with 1 extra param than my older ctor: multiple pages
        public ChangelogWindow(AsyncTexture2D background, IReadOnlyList<string> pages, IReadOnlyList<string> pageTitles = null)
            : base(
                background,
                new Rectangle(5, 60, 580, 590),
                new Rectangle(30, 20, 580, 550)
            )
        {
            Title = "Abattele's NPC Finder - Changelog";

            CanResize = false;
            SavesPosition = true;

            _pages = (pages != null && pages.Count > 0) ? pages : new[] { "" };
            _pageTitles = (pageTitles != null && pageTitles.Count == _pages.Count) ? pageTitles : null;

            BuildUi();
            SetPage(0);
        }

        public void SetPages(IReadOnlyList<string> pages, IReadOnlyList<string> pageTitles = null)
        {
            if (pages == null || pages.Count == 0) return;
        }

        public void SetText(string changelogText)
        {
            if (_pages.Count > 0)
                _text.Text = changelogText ?? "";
            UpdateHeader();
        }


        private void BuildUi()
        {
            var cr = this.ContentRegion;

            _root = new Panel()
            {
                Parent = this,
                Location = new Point(cr.X, cr.Y),
                Size = new Point(cr.Width, cr.Height),
                ClipsBounds = true
            };

            _viewport = new Panel()
            {
                Parent = _root,
                Location = new Point(0, 0),
                Size = _root.Size,
                ClipsBounds = true
            };


            _btnLatest = new StandardButton()
            {
                Parent = _viewport,
                Location = new Point(8, 420),
                Size = new Point(90, 28),
                Text = "Latest"
            };

            _btnOlder = new StandardButton()
            {
                Parent = _viewport,
                Location = new Point(104, 420),
                Size = new Point(130, 28),
                Text = "Older versions"
            };

            _pageInfo = new Label()
            {
                Parent = _viewport,
                Location = new Point(244, 425),
                AutoSizeWidth = true,
                AutoSizeHeight = true,
                Text = ""
            };

            _btnLatest.Click += (s, e) => SetPage(0);
            _btnOlder.Click += (s, e) => GoOlder();

            _text = new Label()
            {
                Parent = _viewport,
                Location = new Point(8, 8),
                AutoSizeHeight = true,
                Width = _viewport.Width - 16,
                WrapText = true,
                // Text = changelogText
                Text = ""
            };

            // keep wrapping correct when resizing (though i have resizing off for now)
            this.Resized += (s, e) =>
            {
                var cr2 = this.ContentRegion;
                _root.Location = new Point(cr2.X, cr2.Y);
                _root.Size = new Point(cr2.Width, cr2.Height);
                _viewport.Size = _root.Size;

                if (_text != null)
                    _text.Width = _viewport.Width - 16;
            };
        }


        private void GoOlder()
        {

            if (_pages.Count <= 1)
            {
                SetPage(0);
                return;
            }

            // older pages
            if (_pageIndex == 0) _pageIndex = 1;
            else
            {
                _pageIndex++;
                if (_pageIndex >= _pages.Count) _pageIndex = 1;
            }

            SetPage(_pageIndex);
        }

        private void SetPage(int index)
        {
            if (_pages == null || _pages.Count == 0) return;

            index = Math.Max(0, Math.Min(index, _pages.Count - 1));
            _pageIndex = index;

            _text.Text = _pages[_pageIndex] ?? "";
            UpdateHeader();
        }

        private void UpdateHeader()
        {
            if (_pageInfo == null) return;

            if (_pages == null || _pages.Count == 0)
            {
                _pageInfo.Text = "";
                return;
            }

            string title = null;
            if (_pageTitles != null && _pageIndex >= 0 && _pageIndex < _pageTitles.Count)
                title = _pageTitles[_pageIndex];

            if (string.IsNullOrWhiteSpace(title))
            {
                title = (_pageIndex == 0)
                    ? "Latest"
                    : $"Older ({_pageIndex}/{_pages.Count - 1})";
            }

            _pageInfo.Text = title;
        }

    }
}