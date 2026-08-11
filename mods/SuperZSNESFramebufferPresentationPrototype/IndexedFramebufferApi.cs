using System;
using UnityEngine;

namespace SuperZSNESFramebufferPresentationPrototype
{
    /// <summary>
    /// A CPU renderer registers one source. It is called synchronously from
    /// PPURenderer.GenerateBackgrounds on Unity's main thread.
    /// </summary>
    public interface IIndexedFramebufferSource
    {
        bool TryRenderFrame(IndexedFramebufferRequest request, IndexedFramebuffer framebuffer,
            out bool rowsAreTopDown, out string rejectionReason);
    }

    public sealed class IndexedFramebufferRequest
    {
        public PPURenderer Renderer { get; internal set; }
        public SNESPPU Ppu { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
    }

    public sealed class IndexedFramebuffer
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Indices { get; private set; }
        public Color32[] Palette { get; private set; }

        internal void EnsureSize(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException();
            if (Indices == null || Indices.Length != checked(width * height))
                Indices = new byte[checked(width * height)];
            if (Palette == null || Palette.Length != 256) Palette = new Color32[256];
            Width = width;
            Height = height;
        }
    }

    public static class FramebufferPresentationApi
    {
        private static IIndexedFramebufferSource _source;

        public static bool Register(IIndexedFramebufferSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (_source != null && !ReferenceEquals(_source, source)) return false;
            _source = source;
            return true;
        }

        public static void Unregister(IIndexedFramebufferSource source)
        {
            if (ReferenceEquals(_source, source)) _source = null;
        }

        internal static IIndexedFramebufferSource Source => _source;
    }
}
