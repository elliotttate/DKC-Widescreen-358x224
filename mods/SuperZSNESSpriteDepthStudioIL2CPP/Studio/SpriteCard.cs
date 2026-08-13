using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SuperZSNESSpriteDepthStudio
{
    internal sealed class SpriteCard : Panel
    {
        private readonly StudioSprite _sprite;
        private readonly NumericUpDown _depth;
        private readonly CheckBox _allMatching;
        private bool _loading;
        internal event Action<SpriteCard> RuleChanged;
        internal StudioSprite Sprite => _sprite;
        internal int Depth => (int)_depth.Value;
        internal bool AllMatching => _allMatching.Checked;
        internal bool Automatic => _sprite.BackgroundObject != null && _allMatching.Checked;

        internal SpriteCard(StudioSprite sprite, Bitmap bitmap, int depth, bool allMatching,
            float uiScale, bool automatic = false)
        {
            int S(int value) => Math.Max(1, (int)Math.Round(value * uiScale));
            _sprite = sprite;
            Width = S(254); Height = S(302); Margin = new Padding(S(8));
            BackColor = Color.FromArgb(35, 39, 47);
            Padding = new Padding(8);
            BorderStyle = BorderStyle.FixedSingle;
            var title = new Label
            {
                Text = sprite.Identity, ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Location = new Point(S(8), S(7)), AutoSize = true
            };
            var preview = new PixelPictureBox
            {
                Image = bitmap, Location = new Point(S(8), S(34)), Size = new Size(S(236), S(144)),
                BackColor = Color.FromArgb(18, 20, 24), Cursor = Cursors.Hand
            };
            preview.Click += (_,__) =>
                new ImageInspectorForm(sprite.Identity, sprite.Detail, bitmap).Show();
            new ToolTip().SetToolTip(preview,
                "Click for full-screen pixel view. Mouse wheel zooms; drag pans.");
            var info = new Label
            {
                Text = "X " + sprite.X + "   Y " + sprite.Y + "   " + sprite.Width + "×" + sprite.Height +
                       "\n" + sprite.Detail,
                ForeColor = sprite.IntersectsScreen ? Color.FromArgb(174, 230, 190) : Color.Silver,
                Location = new Point(S(8), S(184)), Size = new Size(S(236), S(42)), Font = new Font("Segoe UI", 8.5f)
            };
            var depthLabel = new Label
            {
                Text = "Depth layer", ForeColor = Color.Gainsboro,
                Location = new Point(S(8), S(235)), AutoSize = true
            };
            _depth = new NumericUpDown
            {
                Minimum = -12, Maximum = 12, Value = Math.Max(-12, Math.Min(12, depth)),
                Location = new Point(S(158), S(230)), Width = S(78), Height = S(30), TextAlign = HorizontalAlignment.Center,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            var direction = new Label
            {
                Text = "− front   + back", ForeColor = Color.FromArgb(145, 166, 195),
                Location = new Point(S(8), S(270)), AutoSize = true, Font = new Font("Segoe UI", 8.2f)
            };
            _allMatching = new CheckBox
            {
                Text = sprite.BackgroundObject != null ? "automatic" : "all matching",
                Checked = sprite.BackgroundObject != null ? automatic : allMatching,
                ForeColor = Color.Gainsboro,
                Location = new Point(S(130), S(268)), AutoSize = true
            };
            Controls.AddRange(new Control[] { title, preview, info, depthLabel, _depth, direction, _allMatching });
            _loading = false;
            _depth.ValueChanged += (_,__) => Changed();
            _allMatching.CheckedChanged += (_,__) => Changed();
            if (sprite.BackgroundObject != null) _depth.Enabled = !automatic;
            UpdateAccent();
        }

        private void Changed()
        {
            if (_loading) return;
            if (_sprite.BackgroundObject != null) _depth.Enabled = !_allMatching.Checked;
            UpdateAccent();
            RuleChanged?.Invoke(this);
        }

        private void UpdateAccent()
        {
            int value = (int)_depth.Value;
            BackColor = value < 0 ? Color.FromArgb(46, 40, 62) :
                value > 0 ? Color.FromArgb(37, 52, 56) : Color.FromArgb(35, 39, 47);
        }
    }

    internal sealed class StudioSprite
    {
        internal string Identity;
        internal string Detail;
        internal int X,Y,Width,Height,OpaquePixels;
        internal bool IntersectsScreen;
        internal uint[] Pixels;
        internal List<SpriteRecord> Members=new List<SpriteRecord>();
        internal BackgroundObjectRecord BackgroundObject;
    }

    internal sealed class PixelPictureBox : Control
    {
        internal Bitmap Image { get; set; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int cell = 8;
            for (int y=0;y<Height;y+=cell)
            for (int x=0;x<Width;x+=cell)
                using (var brush = new SolidBrush(((x/cell+y/cell)&1)==0 ?
                    Color.FromArgb(28,31,37) : Color.FromArgb(45,49,57)))
                    e.Graphics.FillRectangle(brush,x,y,cell,cell);
            if (Image == null) return;
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            float scale = Math.Min((Width-8f)/Image.Width, (Height-8f)/Image.Height);
            scale = Math.Max(1f, (float)Math.Floor(scale));
            int w=(int)(Image.Width*scale), h=(int)(Image.Height*scale);
            e.Graphics.DrawImage(Image, new Rectangle((Width-w)/2,(Height-h)/2,w,h),
                0,0,Image.Width,Image.Height,GraphicsUnit.Pixel);
        }
    }
}
