using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SuperZSNESSpriteDepthStudio
{
    internal sealed class ImageInspectorForm : Form
    {
        private readonly ZoomImageCanvas _canvas;
        private readonly Label _zoomLabel;

        internal ImageInspectorForm(string title, string detail, Bitmap source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Text = title + " - Object Inspector";
            BackColor = Color.FromArgb(13, 15, 18);
            ForeColor = Color.White;
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(720, 480);
            KeyPreview = true;

            var toolbar = new Panel
            {
                Dock = DockStyle.Top, Height = 58,
                BackColor = Color.FromArgb(28, 32, 39), Padding = new Padding(12, 9, 12, 8)
            };
            var heading = new Label
            {
                Text = title + "   •   " + source.Width + "×" + source.Height +
                    (string.IsNullOrWhiteSpace(detail) ? string.Empty : "   •   " + detail),
                AutoEllipsis = true, ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Location = new Point(12, 9), Size = new Size(620, 22)
            };
            var hint = new Label
            {
                Text = "Wheel: zoom   Drag: pan   Double-click/F: fit   1: 100%   F11: fullscreen   Esc: close",
                ForeColor = Color.FromArgb(156, 176, 204),
                Font = new Font("Segoe UI", 8.5f), Location = new Point(12, 32), AutoSize = true
            };
            var fit = MakeButton("Fit", 680);
            var actual = MakeButton("100%", 744);
            var minus = MakeButton("−", 824);
            var plus = MakeButton("+", 872);
            _zoomLabel = new Label
            {
                Text = "100%", ForeColor = Color.Gainsboro,
                TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Right, Width = 105
            };
            toolbar.Controls.AddRange(new Control[] { heading, hint, fit, actual, minus, plus, _zoomLabel });

            _canvas = new ZoomImageCanvas(source);
            _canvas.Dock = DockStyle.Fill;
            _canvas.ZoomChanged += zoom => _zoomLabel.Text = Math.Round(zoom * 100) + "%";
            fit.Click += (_,__) => _canvas.Fit();
            actual.Click += (_,__) => _canvas.ActualSize();
            minus.Click += (_,__) => _canvas.ZoomBy(0.8f);
            plus.Click += (_,__) => _canvas.ZoomBy(1.25f);
            Controls.Add(_canvas);
            Controls.Add(toolbar);
            Shown += (_,__) => BeginInvoke(new Action(_canvas.Fit));
            FormClosed += (_,__) => _canvas.DisposeImage();
            KeyDown += HandleKeys;
        }

        private static Button MakeButton(string text, int x) => new Button
        {
            Text = text, Location = new Point(x, 13), Size = new Size(text.Length > 2 ? 70 : 40, 31),
            FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(48, 55, 66),
            TabStop = false
        };

        private void HandleKeys(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.F) _canvas.Fit();
            else if (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1) _canvas.ActualSize();
            else if (e.KeyCode == Keys.Add || e.KeyCode == Keys.Oemplus) _canvas.ZoomBy(1.25f);
            else if (e.KeyCode == Keys.Subtract || e.KeyCode == Keys.OemMinus) _canvas.ZoomBy(0.8f);
            else if (e.KeyCode == Keys.F11)
            {
                bool full = FormBorderStyle == FormBorderStyle.None;
                FormBorderStyle = full ? FormBorderStyle.Sizable : FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                WindowState = FormWindowState.Maximized;
            }
        }
    }

    internal sealed class ZoomImageCanvas : Control
    {
        private readonly Bitmap _image;
        private float _zoom = 1f;
        private PointF _pan;
        private Point _dragStart;
        private PointF _panStart;
        private bool _dragging;
        private bool _fitMode = true;
        internal event Action<float> ZoomChanged;

        internal ZoomImageCanvas(Bitmap source)
        {
            _image = new Bitmap(source);
            BackColor = Color.FromArgb(10, 12, 15);
            DoubleBuffered = true;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            MouseWheel += (_, e) => ZoomAt(e.Location, e.Delta > 0 ? 1.25f : 0.8f);
            MouseDoubleClick += (_,__) => Fit();
            MouseDown += BeginPan;
            MouseMove += ContinuePan;
            MouseUp += (_,__) => { _dragging = false; Cursor = Cursors.Hand; };
            Cursor = Cursors.Hand;
            Resize += (_,__) =>
            {
                if (_fitMode && ClientSize.Width > 1 && ClientSize.Height > 1) Fit();
            };
        }

        internal void DisposeImage() => _image.Dispose();

        internal void Fit()
        {
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            _fitMode = true;
            float availableX = Math.Max(1, ClientSize.Width - 48f) / _image.Width;
            float availableY = Math.Max(1, ClientSize.Height - 48f) / _image.Height;
            _zoom = Clamp(Math.Min(availableX, availableY));
            _pan = PointF.Empty;
            Changed();
        }

        internal void ActualSize()
        {
            _fitMode = false;
            _zoom = 1f;
            _pan = PointF.Empty;
            Changed();
        }

        internal void ZoomBy(float factor) => ZoomAt(
            new Point(ClientSize.Width / 2, ClientSize.Height / 2), factor);

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(BackColor);
            RectangleF destination = ImageBounds();
            DrawChecker(e.Graphics, destination);
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.CompositingQuality = CompositingQuality.HighSpeed;
            e.Graphics.DrawImage(_image, destination,
                new RectangleF(0, 0, _image.Width, _image.Height), GraphicsUnit.Pixel);
            using var border = new Pen(Color.FromArgb(96, 145, 170, 205));
            e.Graphics.DrawRectangle(border, destination.X, destination.Y,
                destination.Width, destination.Height);
        }

        private void DrawChecker(Graphics graphics, RectangleF destination)
        {
            graphics.SetClip(destination);
            const int cell = 16;
            int left = (int)Math.Floor(destination.Left / cell) * cell;
            int top = (int)Math.Floor(destination.Top / cell) * cell;
            using var a = new SolidBrush(Color.FromArgb(32, 36, 43));
            using var b = new SolidBrush(Color.FromArgb(52, 58, 68));
            for (int y = top; y < destination.Bottom; y += cell)
                for (int x = left; x < destination.Right; x += cell)
                    graphics.FillRectangle((((x / cell) + (y / cell)) & 1) == 0 ? a : b,
                        x, y, cell, cell);
            graphics.ResetClip();
        }

        private RectangleF ImageBounds()
        {
            float width = _image.Width * _zoom;
            float height = _image.Height * _zoom;
            return new RectangleF((ClientSize.Width - width) * 0.5f + _pan.X,
                (ClientSize.Height - height) * 0.5f + _pan.Y, width, height);
        }

        private void ZoomAt(Point location, float factor)
        {
            RectangleF before = ImageBounds();
            float imageX = (location.X - before.X) / Math.Max(0.0001f, _zoom);
            float imageY = (location.Y - before.Y) / Math.Max(0.0001f, _zoom);
            float next = Clamp(_zoom * factor);
            if (Math.Abs(next - _zoom) < 0.0001f) return;
            _fitMode = false;
            _zoom = next;
            float centeredX = (ClientSize.Width - _image.Width * _zoom) * 0.5f;
            float centeredY = (ClientSize.Height - _image.Height * _zoom) * 0.5f;
            _pan = new PointF(location.X - imageX * _zoom - centeredX,
                location.Y - imageY * _zoom - centeredY);
            Changed();
        }

        private void BeginPan(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Focus();
            _dragging = true;
            _dragStart = e.Location;
            _panStart = _pan;
            Cursor = Cursors.SizeAll;
        }

        private void ContinuePan(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _pan = new PointF(_panStart.X + e.X - _dragStart.X,
                _panStart.Y + e.Y - _dragStart.Y);
            Invalidate();
        }

        private void Changed()
        {
            Invalidate();
            ZoomChanged?.Invoke(_zoom);
        }

        private static float Clamp(float value) => Math.Max(0.05f, Math.Min(64f, value));
    }
}
