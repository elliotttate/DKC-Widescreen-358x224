using System;
using UnityEngine;

namespace SuperZSNESFramebufferPresentationPrototype
{
    internal sealed class PersistentFrameSurface : IDisposable
    {
        private Texture2D _uploadTexture;
        private RenderTexture _presentationTexture;
        private byte[] _rgba;
        private int _width;
        private int _height;

        internal RenderTexture PresentationTexture => _presentationTexture;

        internal void Upload(IndexedFramebuffer framebuffer, bool rowsAreTopDown)
        {
            EnsureResources(framebuffer.Width, framebuffer.Height);
            ExpandIndexed(framebuffer.Indices, framebuffer.Palette, framebuffer.Width,
                framebuffer.Height, rowsAreTopDown, _rgba);
            _uploadTexture.LoadRawTextureData(_rgba);
            _uploadTexture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            Graphics.Blit(_uploadTexture, _presentationTexture);
        }

        private void EnsureResources(int width, int height)
        {
            if (_uploadTexture != null && _presentationTexture != null &&
                _width == width && _height == height) return;
            Dispose();
            _width = width;
            _height = height;
            _rgba = new byte[checked(width * height * 4)];
            _uploadTexture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                name = "SuperZSNES CPU framebuffer upload",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _presentationTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                name = "SuperZSNES CPU framebuffer presentation",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            if (!_presentationTexture.Create())
                throw new InvalidOperationException("Persistent framebuffer RenderTexture creation failed.");
        }

        internal static void ExpandIndexed(byte[] indices, Color32[] palette, int width, int height,
            bool rowsAreTopDown, byte[] rgba)
        {
            if (indices == null || palette == null || rgba == null || palette.Length != 256 ||
                width <= 0 || height <= 0 || indices.Length != checked(width * height) ||
                rgba.Length != checked(width * height * 4))
                throw new ArgumentException("Indexed framebuffer shape is invalid.");
            for (var destinationY = 0; destinationY < height; destinationY++)
            {
                // Texture2D raw row zero is the bottom row. Most CPU renderers
                // produce top-down rows, so reverse rows during expansion.
                var sourceY = rowsAreTopDown ? height - 1 - destinationY : destinationY;
                var sourceOffset = sourceY * width;
                var destinationOffset = destinationY * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var color = palette[indices[sourceOffset + x]];
                    var offset = destinationOffset + x * 4;
                    rgba[offset] = color.r;
                    rgba[offset + 1] = color.g;
                    rgba[offset + 2] = color.b;
                    rgba[offset + 3] = color.a;
                }
            }
        }

        public void Dispose()
        {
            if (_presentationTexture != null)
            {
                _presentationTexture.Release();
                UnityEngine.Object.Destroy(_presentationTexture);
            }
            if (_uploadTexture != null) UnityEngine.Object.Destroy(_uploadTexture);
            _presentationTexture = null;
            _uploadTexture = null;
            _rgba = null;
            _width = 0;
            _height = 0;
        }
    }
}
