using System;
using System.IO;
using System.Text.Json;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SuperZSNESLayerDepthControllerIL2CPP
{
    internal sealed class ForegroundGroundOverlay : IDisposable
    {
        private readonly string _profilesDirectory;
        private readonly ManualLogSource _log;
        private readonly byte[] _vram = new byte[65536];
        private readonly byte[] _registers = new byte[64];
        private readonly byte[] _cgramLines =
            new byte[ForegroundGroundRasterizer.ViewHeight * 512];
        private readonly byte[] _cgramWorking = new byte[512];
        private readonly int[] _scrollXLines =
            new int[ForegroundGroundRasterizer.ViewHeight];
        private readonly int[] _scrollYLines =
            new int[ForegroundGroundRasterizer.ViewHeight];
        private readonly byte[] _displayLines =
            new byte[ForegroundGroundRasterizer.ViewHeight];
        private readonly byte[] _mainScreenLines =
            new byte[ForegroundGroundRasterizer.ViewHeight];
        private readonly byte[] _colorControlLines =
            new byte[ForegroundGroundRasterizer.ViewHeight];
        private readonly uint[] _pixels = new uint[
            ForegroundGroundRasterizer.ViewWidth * ForegroundGroundRasterizer.ViewHeight];
        private readonly uint[] _croppedPixels = new uint[
            ForegroundGroundRasterizer.ViewWidth * ForegroundGroundRasterizer.ViewHeight];
        private readonly int[] _edgeWorkspace =
            new int[ForegroundGroundRasterizer.ViewWidth];
        private readonly int[] _smoothWorkspace =
            new int[ForegroundGroundRasterizer.ViewWidth];
        private readonly Color32[] _upload = new Color32[
            ForegroundGroundRasterizer.ViewWidth * ForegroundGroundRasterizer.ViewHeight];
        private ForegroundGroundSettings _settings = new ForegroundGroundSettings();
        private PPURenderer _renderer;
        private GameObject _object;
        private Texture2D _texture;
        private Material _material;
        private Mesh _mesh;
        private int _level = -1;
        private long _profileStamp;
        private int _profilePoll;
        private ulong _lastPixels;
        private bool _hasPixels;
        private string _lastReason = string.Empty;
        private int _cropLeft;
        private int _cropTop;
        private int _cropWidth = ForegroundGroundRasterizer.ViewWidth;
        private int _cropHeight = ForegroundGroundRasterizer.ViewHeight;

        internal bool Visible => _object != null && _object.activeSelf;
        internal int Level => _level;
        internal int CutY => _settings?.CutY ?? 0;
        internal float Depth => _settings?.Depth ?? 0f;
        internal float SurfaceScaleX => _settings?.SurfaceScaleX ?? 1.05f;
        internal float SurfaceScaleY => _settings?.SurfaceScaleY ?? 1f;
        internal float SourceWidth { get; private set; }
        internal float SourceHeight { get; private set; }
        internal string ShaderName => _material?.shader?.name ?? string.Empty;
        internal string LastReason => _lastReason;
        internal long Uploads { get; private set; }

        internal ForegroundGroundOverlay(string pluginDirectory,
            ManualLogSource log)
        {
            _profilesDirectory = Path.Combine(pluginDirectory, "profiles");
            _log = log;
            Directory.CreateDirectory(_profilesDirectory);
        }

        internal void Refresh(PPURenderer renderer, bool controllerActive,
            bool perspectiveCompensation, float cameraDistance)
        {
            try
            {
                if (!controllerActive)
                {
                    Hide("controller-inactive");
                    return;
                }
                if (!TryReadState(renderer, out SNESPPU ppu,
                        out int level, out string reason))
                {
                    Hide(reason);
                    return;
                }
                ReloadProfile(level);
                if (_settings == null || !_settings.Enabled)
                {
                    Hide("profile-disabled");
                    return;
                }
                if (!TryCapture(ppu, out reason))
                {
                    Hide(reason);
                    return;
                }

                int background = Math.Max(0, Math.Min(2, _settings.Background));
                if (!ForegroundGroundRasterizer.TryRasterize(_vram, _cgramLines,
                        _registers, _scrollXLines, _scrollYLines, _displayLines,
                        _mainScreenLines, _colorControlLines, background,
                        _settings.CutY, _settings.FollowGroundEdge,
                        _settings.EdgeSearchRadius, _edgeWorkspace,
                        _smoothWorkspace,
                        _pixels, out reason))
                {
                    Hide(reason);
                    return;
                }

                EnsureSurface(renderer);
                ForegroundGroundRasterizer.CropToOpaqueBounds(_pixels, 1,
                    _croppedPixels, out _cropLeft, out _cropTop,
                    out _cropWidth, out _cropHeight);
                if (_cropWidth <= 0 || _cropHeight <= 0)
                {
                    Hide("empty-ground-cutout");
                    return;
                }
                UpdateCropUvs();
                ulong fingerprint = Fingerprint(_croppedPixels);
                if (!_hasPixels || fingerprint != _lastPixels)
                {
                    ConvertForUnity(_croppedPixels, _upload);
                    _texture.SetPixels32(_upload);
                    _texture.Apply(false, false);
                    _lastPixels = fingerprint;
                    _hasPixels = true;
                    Uploads++;
                }
                ApplyTransform(cameraDistance, perspectiveCompensation);
                _object.SetActive(true);
                _lastReason = string.Empty;
            }
            catch (Exception exception)
            {
                string message = reasonKey(exception);
                bool changed = !string.Equals(_lastReason, message,
                    StringComparison.Ordinal);
                Hide(message);
                if (changed)
                    _log?.LogError("Foreground-ground layer failed closed: " + exception);
            }
        }

        internal void Hide(string reason)
        {
            if (_object != null && _object.activeSelf) _object.SetActive(false);
            _lastReason = reason ?? string.Empty;
        }

        private bool TryReadState(PPURenderer renderer, out SNESPPU ppu,
            out int level, out string reason)
        {
            ppu = renderer?.snesPPU;
            level = -1;
            reason = string.Empty;
            string filename = MainMenuManager.Instance?.GetLoadedGameFilename() ?? string.Empty;
            if (filename.IndexOf("DKC_Widescreen_", StringComparison.OrdinalIgnoreCase) < 0)
            {
                reason = "not-supported-dkc-rom";
                return false;
            }
            if (renderer == null || renderer.dynamicFont || ppu == null)
            {
                reason = "renderer-unavailable";
                return false;
            }
            try
            {
                Il2CppStructArray<byte> ram = ppu.masterExecutor?.CoreMemoryMap?.GetRam();
                if (ram == null || ram.Length <= 0x31)
                {
                    reason = "wram-unavailable";
                    return false;
                }
                level = ram[0x30] | (ram[0x31] << 8);
            }
            catch
            {
                reason = "wram-unavailable";
                return false;
            }
            return true;
        }

        private void ReloadProfile(int level)
        {
            string path = Path.Combine(_profilesDirectory,
                "level-" + level.ToString("X4") + ".json");
            if (level == _level && --_profilePoll > 0) return;
            _profilePoll = 15;
            long stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;
            if (level == _level && stamp == _profileStamp) return;
            _level = level;
            _profileStamp = stamp;
            _hasPixels = false;
            if (!File.Exists(path))
            {
                _settings = new ForegroundGroundSettings();
                return;
            }
            LayerComponentProfile profile = JsonSerializer.Deserialize<LayerComponentProfile>(
                File.ReadAllText(path), new JsonSerializerOptions
                { PropertyNameCaseInsensitive = true });
            _settings = profile?.ForegroundGround ?? new ForegroundGroundSettings();
        }

        private bool TryCapture(SNESPPU ppu, out string reason)
        {
            reason = string.Empty;
            if (ppu._ppuStartFrame == null || ppu._ppuStartFrame.Length < 64 ||
                ppu.GetPPUMemory() == null || ppu.GetPPUMemory().Length != 65536 ||
                ppu.GetCGMemoryStartFrame() == null ||
                ppu.GetCGMemoryStartFrame().Length != 512)
            {
                reason = "missing-ppu-state";
                return false;
            }
            Copy(ppu.GetPPUMemory(), _vram, _vram.Length);
            Copy(ppu._ppuStartFrame, _registers, _registers.Length);
            if (ppu._ppuLineChanges == null || ppu._curPPUChangeIdx < 0 ||
                ppu._curPPUChangeIdx > ppu._ppuLineChanges.Length)
            {
                reason = "invalid-raster-change-list";
                return false;
            }
            for (int i = 0; i < ppu._curPPUChangeIdx; i++)
            {
                SNESPPU.PPULineChange change = ppu._ppuLineChanges[i];
                uint address = change.address;
                if (address == 0x2105 || address == 0x2106 ||
                    (address >= 0x2107 && address <= 0x210C))
                {
                    if (change.lineNo > Math.Max(0, Math.Min(223, _settings.CutY)))
                    {
                        reason = "layout-change-below-ground-cut-$" +
                            address.ToString("X4") + "-line-" + change.lineNo;
                        return false;
                    }
                    _registers[address - 0x2100] = change.val;
                }
            }
            BuildVideoLines(ppu, Math.Max(0, Math.Min(2, _settings.Background)));
            if (ppu._cgLineChanges == null || ppu._cgChangeIdx < 0 ||
                ppu._cgChangeIdx > ppu._cgLineChanges.Length)
            {
                reason = "invalid-cgram-change-list";
                return false;
            }
            Copy(ppu.GetCGMemoryStartFrame(), _cgramWorking, 512);
            int eventIndex = 0;
            for (int line = 0; line < ForegroundGroundRasterizer.ViewHeight; line++)
            {
                while (eventIndex < ppu._cgChangeIdx &&
                       eventIndex < ppu._cgLineChanges.Length &&
                       ppu._cgLineChanges[eventIndex].lineNo <= line)
                {
                    SNESPPU.CGLineChange change = ppu._cgLineChanges[eventIndex++];
                    int address = (change.colNo & 255) * 2;
                    _cgramWorking[address] = change.colorLo;
                    _cgramWorking[address + 1] = change.colorHi;
                }
                Buffer.BlockCopy(_cgramWorking, 0, _cgramLines, line * 512, 512);
            }
            return true;
        }

        private void BuildVideoLines(SNESPPU ppu, int background)
        {
            int scrollX = background == 0 ? ppu._startScrollXBG1 :
                background == 1 ? ppu._startScrollXBG2 : ppu._startScrollXBG3;
            int scrollY = background == 0 ? ppu._startScrollYBG1 :
                background == 1 ? ppu._startScrollYBG2 : ppu._startScrollYBG3;
            uint horizontal = (uint)(0x210D + background * 2);
            uint vertical = horizontal + 1;
            byte display = _registers[0];
            byte mainScreen = _registers[44];
            byte colorControl = _registers[48];
            int eventIndex = 0;
            for (int line = 0; line < ForegroundGroundRasterizer.ViewHeight; line++)
            {
                while (eventIndex < ppu._curPPUChangeIdx &&
                       eventIndex < ppu._ppuLineChanges.Length &&
                       ppu._ppuLineChanges[eventIndex].lineNo <= line)
                {
                    SNESPPU.PPULineChange change = ppu._ppuLineChanges[eventIndex++];
                    if (change.address == horizontal)
                        scrollX = ((scrollX >> 8) | (change.val << 8)) & 0xFFFF;
                    else if (change.address == vertical)
                        scrollY = ((scrollY >> 8) | (change.val << 8)) & 0xFFFF;
                    else if (change.address == 0x2100) display = change.val;
                    else if (change.address == 0x212C) mainScreen = change.val;
                    else if (change.address == 0x2130) colorControl = change.val;
                }
                _scrollXLines[line] = scrollX;
                _scrollYLines[line] = scrollY;
                _displayLines[line] = display;
                _mainScreenLines[line] = mainScreen;
                _colorControlLines[line] = colorControl;
            }
        }

        private void EnsureSurface(PPURenderer renderer)
        {
            if (_object != null && _renderer == renderer) return;
            DestroySurface();
            if (renderer?.backgrounds == null)
                throw new InvalidOperationException("3D background root is unavailable");
            Shader shader = FindLoadedTransparentShader();
            if (shader == null)
                throw new InvalidOperationException("loaded transparent RGBA shader is unavailable");

            _renderer = renderer;
            _texture = new Texture2D(ForegroundGroundRasterizer.ViewWidth,
                ForegroundGroundRasterizer.ViewHeight, TextureFormat.RGBA32, false, false)
            {
                name = "DKC Foreground Ground Texture",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _material = new Material(shader)
            {
                name = "DKC Foreground Ground Material",
                mainTexture = _texture,
                renderQueue = 3100
            };
            _material.mainTexture = _texture;
            _material.SetColor("_Color", Color.white);
            if (_material.HasProperty("_RendererColor"))
                _material.SetColor("_RendererColor", Color.white);
            if (_material.HasProperty("_EnableExternalAlpha"))
                _material.SetFloat("_EnableExternalAlpha", 0f);
            _mesh = BuildQuad();
            _object = new GameObject("DKC Foreground Ground Cutout");
            _object.layer = 7;
            _object.transform.SetParent(renderer.backgrounds, false);
            MeshFilter filter = _object.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = _object.AddComponent<MeshRenderer>();
            filter.sharedMesh = _mesh;
            meshRenderer.sharedMaterial = _material;
            _object.SetActive(false);
            _hasPixels = false;
        }

        private Shader FindLoadedTransparentShader()
        {
            Shader[] shaders = Resources.FindObjectsOfTypeAll<Shader>();
            Shader sprites = null;
            Shader unlit = null;
            Shader fallback = null;
            foreach (Shader shader in shaders)
            {
                if (shader == null) continue;
                string name = shader.name ?? string.Empty;
                if (string.Equals(name, "Sprites/Default",
                        StringComparison.OrdinalIgnoreCase)) sprites = shader;
                if (string.Equals(name, "Unlit/Transparent",
                        StringComparison.OrdinalIgnoreCase)) unlit = shader;
                if (name.IndexOf("unlit", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    name.IndexOf("texture", StringComparison.OrdinalIgnoreCase) >= 0)
                    fallback = shader;
                if (string.Equals(name, "UI/Default",
                        StringComparison.OrdinalIgnoreCase) && fallback == null)
                    fallback = shader;
            }
            return sprites ?? unlit ?? fallback;
        }

        private void UpdateCropUvs()
        {
            if (_mesh == null) return;
            float u = _cropWidth / (float)ForegroundGroundRasterizer.ViewWidth;
            float v0 = 1f - _cropHeight /
                (float)ForegroundGroundRasterizer.ViewHeight;
            _mesh.uv = new[]
            {
                new Vector2(0f, v0), new Vector2(u, v0),
                new Vector2(0f, 1f), new Vector2(u, 1f)
            };
        }

        private void ApplyTransform(float cameraDistance,
            bool perspectiveCompensation)
        {
            int background = Math.Max(0, Math.Min(2, _settings.Background));
            Transform sourceRoot = _renderer?.bgPositions != null &&
                _renderer.bgPositions.Length > background
                ? _renderer.bgPositions[background] : _renderer?.backgrounds;
            if (sourceRoot != null && _object.transform.parent != sourceRoot)
                _object.transform.SetParent(sourceRoot, false);
            float depth = Math.Max(-8f, Math.Min(4f, _settings.Depth));
            float scale = perspectiveCompensation
                ? (float)DepthMath.PerspectiveCompensation(depth, cameraDistance) : 1f;
            float offsetY = Math.Max(-4f, Math.Min(4f, _settings.OffsetY));
            float surfaceScaleX = Math.Max(0.5f,
                Math.Min(4f, _settings.SurfaceScaleX));
            float surfaceScaleY = Math.Max(0.5f,
                Math.Min(3f, _settings.SurfaceScaleY));
            float sourceWidth = _cropWidth / 8f;
            float sourceHeight = _cropHeight / 8f;
            float sourceCenterX = (ForegroundGroundRasterizer.ViewLeft +
                _cropLeft + _cropWidth * 0.5f) / 8f;
            float sourceCenterY = 14.0625f -
                (_cropTop + _cropHeight * 0.5f) / 8f;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            _object.transform.localPosition = new Vector3(sourceCenterX,
                sourceCenterY + offsetY, depth);
            _object.transform.localRotation = Quaternion.identity;
            _object.transform.localScale = new Vector3(
                sourceWidth * scale * surfaceScaleX,
                sourceHeight * scale * surfaceScaleY, 1f);
        }

        private static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "DKC Foreground Ground Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
            };
            mesh.triangles = new[] { 0, 3, 1, 3, 0, 2 };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f)
            };
            mesh.colors32 = new[]
            {
                new Color32(255, 255, 255, 255),
                new Color32(255, 255, 255, 255),
                new Color32(255, 255, 255, 255),
                new Color32(255, 255, 255, 255)
            };
            mesh.normals = new[]
            {
                new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, -1f),
                new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, -1f)
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void ConvertForUnity(uint[] source, Color32[] destination)
        {
            int width = ForegroundGroundRasterizer.ViewWidth;
            int height = ForegroundGroundRasterizer.ViewHeight;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                uint argb = source[y * width + x];
                int target = (height - 1 - y) * width + x;
                destination[target] = new Color32((byte)(argb >> 16),
                    (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
            }
        }

        private static ulong Fingerprint(uint[] values)
        {
            ulong hash = 1469598103934665603UL;
            for (int i = 0; i < values.Length; i++)
            {
                hash ^= values[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }

        private static void Copy(Il2CppStructArray<byte> source,
            byte[] destination, int count)
        {
            for (int i = 0; i < count; i++) destination[i] = source[i];
        }

        private static string reasonKey(Exception exception) =>
            exception.GetType().Name + ": " + exception.Message;

        private void DestroySurface()
        {
            if (_object != null) UnityEngine.Object.Destroy(_object);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            _object = null;
            _material = null;
            _texture = null;
            _mesh = null;
            _renderer = null;
        }

        public void Dispose()
        {
            DestroySurface();
        }
    }
}
