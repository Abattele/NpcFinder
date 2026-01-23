using Blish_HUD.Controls;
using Blish_HUD.Content;
using Microsoft.Xna.Framework;

namespace NpcFinder.Controls
{
    public sealed class ChangelogWindow : StandardWindow
    {
        private Panel _root;
        private Panel _viewport;
        private Label _text;

        public ChangelogWindow(AsyncTexture2D background, string changelogText)
            : base(
                background,
                new Rectangle(5, 60, 580, 590),  
                new Rectangle(30, 20, 580, 550)
            )  
        {
            Title = "Abattele's NPC Finder - Changelog";

            CanResize = false;      
            SavesPosition = true;

            SavesPosition = true;

            BuildUi(changelogText ?? "");
        }

        public void SetText(string changelogText)
        {
            if (_text != null) _text.Text = changelogText ?? "";
        }

        private void BuildUi(string changelogText)
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

            _text = new Label()
            {
                Parent = _viewport,
                Location = new Point(8, 8),
                AutoSizeHeight = true,
                Width = _viewport.Width - 16,
                WrapText = true,
                Text = changelogText
            };

            // keep wrapping correct when resizing
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
    }
}
