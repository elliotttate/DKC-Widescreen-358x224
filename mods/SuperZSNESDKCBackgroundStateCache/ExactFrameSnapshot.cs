using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SuperZSNESDKCBackgroundStateCache
{
    internal struct FrameView
    {
        internal PPURenderer Renderer;
        internal SNESPPU Ppu;
        internal int Epoch;
        internal string Filename;
        internal int NumLines;
        internal MainMenuManager.GameSpecificSettings GameSettings;
        internal ModData ModData;
        internal ModData.SceneData CurrentScene;
        internal ModData.SceneData GlobalScene;
        internal byte[] Vram;
        internal byte[] Cgram;
        internal byte[] CgramStart;
        internal byte[] Io;

        internal static bool TryCreate(PPURenderer renderer, FieldInfo ppuField, int epoch,
            out FrameView view, out string rejection)
        {
            view = default(FrameView);
            rejection = null;
            try
            {
                var menu = MainMenuManager.Instance;
                var ppu = ppuField.GetValue(renderer) as SNESPPU;
                if (menu == null || ppu == null || ppu.masterExecutor == null ||
                    ppu.masterExecutor.uiInterface == null)
                {
                    rejection = "invalid-runtime-references";
                    return false;
                }

                var filename = menu.GetLoadedGameFilename() ?? string.Empty;
                if (filename.IndexOf("DKC_Widescreen_358x224", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    rejection = "non-dkc-rom";
                    return false;
                }

                var start = ppu._ppuStartFrame;
                var ppuChanges = ppu._ppuLineChanges;
                var cgChanges = ppu._cgLineChanges;
                var io = ppu.GetIORegisters();
                var vram = ppu.GetPPUMemory();
                var cgram = ppu.GetCGMemory();
                var cgramStart = ppu.GetCGMemoryStartFrame();
                if (start == null || start.Length < 64 || io == null || io.Length < 320 ||
                    vram == null || vram.Length != 65536 || cgram == null || cgram.Length != 512 ||
                    cgramStart == null || cgramStart.Length != 512 || ppuChanges == null || cgChanges == null ||
                    ppu._curPPUChangeIdx < 0 || ppu._curPPUChangeIdx > ppuChanges.Length ||
                    ppu._cgChangeIdx < 0 || ppu._cgChangeIdx > cgChanges.Length)
                {
                    rejection = "invalid-ppu-array-shape";
                    return false;
                }

                if ((start[5] & 7) == 7)
                {
                    rejection = "mode7-start";
                    return false;
                }
                var activeMask = (start[44] | start[45]) & 15;
                if (HasMode7ScanlineChange(ppuChanges, ppu._curPPUChangeIdx))
                {
                    rejection = "mode7-scanline";
                    return false;
                }
                for (var index = 0; index < ppu._curPPUChangeIdx; index++)
                {
                    var change = ppuChanges[index];
                    if (change.address == 0x212C || change.address == 0x212D)
                        activeMask |= change.val & 15;
                }
                if (activeMask == 0)
                {
                    rejection = "no-active-background";
                    return false;
                }

                var settings = menu.GetGameSettings();
                if (settings == null)
                {
                    rejection = "missing-game-settings";
                    return false;
                }
                var modData = ppu.masterExecutor.modData;
                if (HasUnsupportedEnhancements(modData))
                {
                    rejection = "unsupported-enhanced-material-or-font";
                    return false;
                }

                view = new FrameView
                {
                    Renderer = renderer,
                    Ppu = ppu,
                    Epoch = epoch,
                    Filename = filename,
                    NumLines = ppu.masterExecutor.GetNumLines(),
                    GameSettings = settings,
                    ModData = modData,
                    CurrentScene = ppu.masterExecutor.uiInterface.GetCurSceneData(),
                    GlobalScene = ppu.masterExecutor.uiInterface.GetGlobalMaterialSceneData(),
                    Vram = vram,
                    Cgram = cgram,
                    CgramStart = cgramStart,
                    Io = io
                };
                return true;
            }
            catch (Exception exception)
            {
                rejection = "capture-error-" + exception.GetType().Name;
                return false;
            }
        }

        // This intentionally examines the original, unfiltered stream. A
        // Mode-7 selection anywhere in the frame disables this non-Mode7 cache.
        internal static bool HasMode7ScanlineChange(SNESPPU.PPULineChange[] changes, int count)
        {
            if (changes == null || count < 0 || count > changes.Length) return true;
            for (var index = 0; index < count; index++)
                if (changes[index].address == 0x2105 && (changes[index].val & 7) == 7) return true;
            return false;
        }

        private static bool HasUnsupportedEnhancements(ModData modData)
        {
            if (modData == null) return false;
            if ((modData.dynamicFontDataValues != null && modData.dynamicFontDataValues.Count != 0) ||
                (modData.tileUVModData != null && modData.tileUVModData.Count != 0) ||
                (modData.tile3DModData != null && modData.tile3DModData.Count != 0))
                return true;
            if (modData.sceneData == null) return false;
            foreach (var scene in modData.sceneData)
                if (scene != null && scene.materials != null && scene.materials.Count != 0) return true;
            return false;
        }
    }

    internal sealed class ExactFrameSnapshot
    {
        // These are the 33 PPULineChange switch cases in the exact v0.230
        // PPURenderer.GenerateBackground IL, plus $212C/$212D. The latter two
        // are consumed by GenerateBackgrounds to decide which BG layers are
        // active and therefore must remain part of an all-or-none cache key.
        //
        // The list is sorted so the hot-path predicate can use BinarySearch.
        // Order is NOT normalized in the saved stream: retained records are
        // copied and compared in their original order with exact line/value.
        internal static readonly uint[] RelevantPpuChangeAddresses =
        {
            0x2100,
            0x2105, 0x2106, 0x2107, 0x2108, 0x2109, 0x210A, 0x210B, 0x210C,
            0x210D, 0x210E, 0x210F, 0x2110, 0x2111, 0x2112, 0x2113, 0x2114,
            0x211A, 0x211B, 0x211C, 0x211D, 0x211E, 0x211F, 0x2120,
            0x2123, 0x2124, 0x2125,
            0x212A, 0x212B, 0x212C, 0x212D, 0x212E, 0x212F,
            0x2130, 0x2131
        };

        // Current PPU IO bytes that can affect BG/window/color state. OAM/OBJ,
        // VRAM-port and CGRAM-port latches are excluded; their rendered data is
        // represented by full VRAM/current+start CGRAM and scanline changes.
        private static readonly int[] RelevantIoIndices =
        {
            256,
            261, 262, 263, 264, 265, 266, 267, 268, 269, 270, 271, 272, 273, 274, 275, 276,
            282, 283, 284, 285, 286, 287, 288,
            291, 292, 293, 294, 295, 296, 297, 298, 299, 300, 301, 302, 303, 304, 305, 306, 307
        };
        internal bool Valid;

        private PPURenderer _renderer;
        private SNESPPU _ppu;
        private int _epoch;
        private string _filename;
        private int _numLines;
        private readonly RendererSnapshot _rendererState = new RendererSnapshot();
        private readonly GameSettingsSnapshot _game = new GameSettingsSnapshot();
        private readonly ModSnapshot _mod = new ModSnapshot();
        private ModData.SceneData _currentScene;
        private ModData.SceneData _globalScene;

        private byte[] _vram;
        private byte[] _cgram;
        private byte[] _cgramStart;
        private byte[] _ppuStart;
        private byte[] _ioPpuWindow;
        private byte[] _dirty2;
        private byte[] _dirty4;
        private byte[] _dirty8;
        private byte[] _dirtyCg2;
        private byte[] _dirtyCg4;
        private bool[] _dirtyBg0;
        private bool[] _dirtyBg1;
        private bool[] _dirtyBg2;
        private bool[] _dirtyBg3;
        private byte _dirtyCg8;
        private byte _dirtyCgMode7;
        private SNESPPU.PPULineChange[] _ppuChanges;
        private SNESPPU.CGLineChange[] _cgChanges;
        private int _ppuChangeCount;
        private int _cgChangeCount;

        internal void Capture(FrameView view)
        {
            _renderer = view.Renderer;
            _ppu = view.Ppu;
            _epoch = view.Epoch;
            _filename = view.Filename;
            _numLines = view.NumLines;
            _rendererState.Capture(view.Renderer, view.Ppu);
            _game.Capture(view.GameSettings);
            _mod.Capture(view.ModData);
            _currentScene = view.CurrentScene;
            _globalScene = view.GlobalScene;

            CopyBytes(view.Vram, ref _vram);
            CopyBytes(view.Cgram, ref _cgram);
            CopyBytes(view.CgramStart, ref _cgramStart);
            CopyBytes(view.Ppu._ppuStartFrame, ref _ppuStart);
            CopySelectedBytes(view.Io, RelevantIoIndices, ref _ioPpuWindow);
            CopyBytes(view.Ppu._dirty2bpp, ref _dirty2);
            CopyBytes(view.Ppu._dirty4bpp, ref _dirty4);
            CopyBytes(view.Ppu._dirty8bpp, ref _dirty8);
            CopyBytes(view.Ppu._dirtycg2bpp, ref _dirtyCg2);
            CopyBytes(view.Ppu._dirtycg4bpp, ref _dirtyCg4);
            CopyBools(view.Ppu._dirtyBG0, ref _dirtyBg0);
            CopyBools(view.Ppu._dirtyBG1, ref _dirtyBg1);
            CopyBools(view.Ppu._dirtyBG2, ref _dirtyBg2);
            CopyBools(view.Ppu._dirtyBG3, ref _dirtyBg3);
            _dirtyCg8 = view.Ppu._dirtycg8bpp;
            _dirtyCgMode7 = view.Ppu._dirtycgmode7;
            CopyPpuChanges(view.Ppu._ppuLineChanges, view.Ppu._curPPUChangeIdx);
            CopyCgChanges(view.Ppu._cgLineChanges, view.Ppu._cgChangeIdx);
            Valid = true;
        }

        internal bool Matches(FrameView view, out string reason)
        {
            if (!Valid) { reason = "cold-or-invalidated"; return false; }
            if (!ReferenceEquals(_renderer, view.Renderer) || !ReferenceEquals(_ppu, view.Ppu) ||
                _epoch != view.Epoch || !string.Equals(_filename, view.Filename, StringComparison.Ordinal) ||
                _numLines != view.NumLines)
            { reason = "runtime-or-rom-identity"; return false; }
            if (!_rendererState.Matches(view.Renderer, view.Ppu))
            { reason = "renderer-scroll-window-width"; return false; }
            if (!_game.Matches(view.GameSettings))
            { reason = "game-settings"; return false; }
            if (!ReferenceEquals(_currentScene, view.CurrentScene) || !ReferenceEquals(_globalScene, view.GlobalScene) ||
                !_mod.Matches(view.ModData))
            { reason = "scene-or-mod-configuration"; return false; }
            if (!EqualBytes(_ppuStart, view.Ppu._ppuStartFrame) ||
                !EqualSelectedBytes(_ioPpuWindow, view.Io, RelevantIoIndices))
            { reason = "ppu-start-or-io-registers"; return false; }
            if (!EqualPpuChanges(view.Ppu._ppuLineChanges, view.Ppu._curPPUChangeIdx) ||
                !EqualCgChanges(view.Ppu._cgLineChanges, view.Ppu._cgChangeIdx))
            { reason = "scanline-change-stream"; return false; }
            if (!EqualBytes(_cgramStart, view.CgramStart) || !EqualBytes(_cgram, view.Cgram))
            { reason = "cgram"; return false; }
            if (!EqualBytes(_vram, view.Vram))
            { reason = "vram-full-including-obj"; return false; }
            if (!EqualBytes(_dirty2, view.Ppu._dirty2bpp) || !EqualBytes(_dirty4, view.Ppu._dirty4bpp) ||
                !EqualBytes(_dirty8, view.Ppu._dirty8bpp) || !EqualBytes(_dirtyCg2, view.Ppu._dirtycg2bpp) ||
                !EqualBytes(_dirtyCg4, view.Ppu._dirtycg4bpp) || _dirtyCg8 != view.Ppu._dirtycg8bpp ||
                _dirtyCgMode7 != view.Ppu._dirtycgmode7 || !EqualBools(_dirtyBg0, view.Ppu._dirtyBG0) ||
                !EqualBools(_dirtyBg1, view.Ppu._dirtyBG1) || !EqualBools(_dirtyBg2, view.Ppu._dirtyBG2) ||
                !EqualBools(_dirtyBg3, view.Ppu._dirtyBG3))
            { reason = "ppu-dirty-state"; return false; }
            reason = null;
            return true;
        }

        internal void CopyFrom(ExactFrameSnapshot source)
        {
            if (!source.Valid) { Valid = false; return; }
            _renderer = source._renderer;
            _ppu = source._ppu;
            _epoch = source._epoch;
            _filename = source._filename;
            _numLines = source._numLines;
            _rendererState.CopyFrom(source._rendererState);
            _game.CopyFrom(source._game);
            _mod.CopyFrom(source._mod);
            _currentScene = source._currentScene;
            _globalScene = source._globalScene;
            CopyBytes(source._vram, ref _vram);
            CopyBytes(source._cgram, ref _cgram);
            CopyBytes(source._cgramStart, ref _cgramStart);
            CopyBytes(source._ppuStart, ref _ppuStart);
            CopyBytes(source._ioPpuWindow, ref _ioPpuWindow);
            CopyBytes(source._dirty2, ref _dirty2);
            CopyBytes(source._dirty4, ref _dirty4);
            CopyBytes(source._dirty8, ref _dirty8);
            CopyBytes(source._dirtyCg2, ref _dirtyCg2);
            CopyBytes(source._dirtyCg4, ref _dirtyCg4);
            CopyBools(source._dirtyBg0, ref _dirtyBg0);
            CopyBools(source._dirtyBg1, ref _dirtyBg1);
            CopyBools(source._dirtyBg2, ref _dirtyBg2);
            CopyBools(source._dirtyBg3, ref _dirtyBg3);
            _dirtyCg8 = source._dirtyCg8;
            _dirtyCgMode7 = source._dirtyCgMode7;
            CopyPpuChanges(source._ppuChanges, source._ppuChangeCount);
            CopyCgChanges(source._cgChanges, source._cgChangeCount);
            Valid = true;
        }

        private void CopyPpuChanges(SNESPPU.PPULineChange[] source, int count)
        {
            var retainedCount = 0;
            for (var i = 0; i < count; i++)
                if (IsRelevantPpuChangeAddress(source[i].address)) retainedCount++;
            if (_ppuChanges == null || _ppuChanges.Length < retainedCount)
                _ppuChanges = new SNESPPU.PPULineChange[Math.Max(retainedCount, 16)];
            var destinationIndex = 0;
            for (var i = 0; i < count; i++)
                if (IsRelevantPpuChangeAddress(source[i].address))
                    _ppuChanges[destinationIndex++] = source[i];
            _ppuChangeCount = retainedCount;
        }

        private void CopyCgChanges(SNESPPU.CGLineChange[] source, int count)
        {
            if (_cgChanges == null || _cgChanges.Length < count)
                _cgChanges = new SNESPPU.CGLineChange[Math.Max(count, source == null ? 0 : source.Length)];
            if (count != 0) Array.Copy(source, _cgChanges, count);
            _cgChangeCount = count;
        }

        private bool EqualPpuChanges(SNESPPU.PPULineChange[] source, int count)
        {
            if (source == null || count < 0 || count > source.Length) return false;
            var retainedIndex = 0;
            for (var i = 0; i < count; i++)
            {
                if (!IsRelevantPpuChangeAddress(source[i].address)) continue;
                if (retainedIndex >= _ppuChangeCount ||
                    _ppuChanges[retainedIndex].lineNo != source[i].lineNo ||
                    _ppuChanges[retainedIndex].address != source[i].address ||
                    _ppuChanges[retainedIndex].val != source[i].val) return false;
                retainedIndex++;
            }
            return retainedIndex == _ppuChangeCount;
        }

        internal static bool IsRelevantPpuChangeAddress(uint address) =>
            Array.BinarySearch(RelevantPpuChangeAddresses, address) >= 0;

        private bool EqualCgChanges(SNESPPU.CGLineChange[] source, int count)
        {
            if (_cgChangeCount != count || source == null || count > source.Length) return false;
            for (var i = 0; i < count; i++)
                if (_cgChanges[i].lineNo != source[i].lineNo || _cgChanges[i].colNo != source[i].colNo ||
                    _cgChanges[i].colorLo != source[i].colorLo || _cgChanges[i].colorHi != source[i].colorHi) return false;
            return true;
        }

        internal static void CopyBytes(byte[] source, ref byte[] destination)
        {
            if (source == null) { destination = null; return; }
            if (destination == null || destination.Length != source.Length) destination = new byte[source.Length];
            Buffer.BlockCopy(source, 0, destination, 0, source.Length);
        }

        internal static bool EqualBytes(byte[] expected, byte[] actual)
        {
            if (ReferenceEquals(expected, actual)) return true;
            if (expected == null || actual == null || expected.Length != actual.Length) return false;
            for (var i = 0; i < expected.Length; i++) if (expected[i] != actual[i]) return false;
            return true;
        }

        private static void CopySelectedBytes(byte[] source, int[] indices, ref byte[] destination)
        {
            if (destination == null || destination.Length != indices.Length) destination = new byte[indices.Length];
            for (var i = 0; i < indices.Length; i++) destination[i] = source[indices[i]];
        }

        private static bool EqualSelectedBytes(byte[] expected, byte[] actual, int[] indices)
        {
            if (expected == null || expected.Length != indices.Length || actual == null) return false;
            for (var i = 0; i < indices.Length; i++)
                if (indices[i] >= actual.Length || expected[i] != actual[indices[i]]) return false;
            return true;
        }

        private static void CopyBools(bool[] source, ref bool[] destination)
        {
            if (source == null) { destination = null; return; }
            if (destination == null || destination.Length != source.Length) destination = new bool[source.Length];
            Array.Copy(source, destination, source.Length);
        }

        private static bool EqualBools(bool[] expected, bool[] actual)
        {
            if (ReferenceEquals(expected, actual)) return true;
            if (expected == null || actual == null || expected.Length != actual.Length) return false;
            for (var i = 0; i < expected.Length; i++) if (expected[i] != actual[i]) return false;
            return true;
        }
    }

    internal sealed class RendererSnapshot
    {
        // Scanline stream counts are deliberately absent. Exact filtered PPU
        // and unfiltered CG record comparisons own their respective counts.
        private int[] _values = new int[22];
        private int[] _compare = new int[22];

        internal void Capture(PPURenderer renderer, SNESPPU ppu)
        {
            Fill(_values, renderer, ppu);
        }

        private static void Fill(int[] values, PPURenderer renderer, SNESPPU ppu)
        {
            var i = 0;
            values[i++] = renderer.DebugLineEnd;
            values[i++] = renderer.disableBG1 ? 1 : 0;
            values[i++] = renderer.disableBG2 ? 1 : 0;
            values[i++] = renderer.disableBG3 ? 1 : 0;
            values[i++] = renderer.disableBG4 ? 1 : 0;
            values[i++] = renderer.disableWin ? 1 : 0;
            values[i++] = ExactBits.Single(renderer.ratioXL);
            values[i++] = ExactBits.Single(renderer.ratioXR);
            values[i++] = ExactBits.Single(renderer.ratioY);
            values[i++] = Screen.width;
            values[i++] = Screen.height;
            values[i++] = ppu._startScrollXBG1;
            values[i++] = ppu._startScrollYBG1;
            values[i++] = ppu._startScrollXBG2;
            values[i++] = ppu._startScrollYBG2;
            values[i++] = ppu._startScrollXBG3;
            values[i++] = ppu._startScrollYBG3;
            values[i++] = ppu._startScrollXBG4;
            values[i++] = ppu._startScrollYBG4;
            values[i++] = unchecked((int)ppu._startFixedColor);
            values[i++] = ppu._startbgofs_latch;
            values[i] = ppu._startbghofs_latch;
        }

        internal bool Matches(PPURenderer renderer, SNESPPU ppu)
        {
            Fill(_compare, renderer, ppu);
            for (var i = 0; i < _values.Length; i++) if (_values[i] != _compare[i]) return false;
            return true;
        }

        internal void CopyFrom(RendererSnapshot source) => Array.Copy(source._values, _values, _values.Length);
    }

    internal sealed class GameSettingsSnapshot
    {
        private bool _valid;
        private readonly int[] _values = new int[14];
        private readonly int[] _compare = new int[14];

        internal void Capture(MainMenuManager.GameSpecificSettings settings)
        {
            Fill(_values, settings);
            _valid = true;
        }

        private static void Fill(int[] values, MainMenuManager.GameSpecificSettings settings)
        {
            var i = 0;
            values[i++] = settings.disEnhanceHires ? 1 : 0;
            values[i++] = settings.disEnhanceTextures ? 1 : 0;
            values[i++] = settings.disEnhance3D ? 1 : 0;
            values[i++] = settings.disEnhanceAudio ? 1 : 0;
            values[i++] = settings.disEnhanceOC ? 1 : 0;
            values[i++] = settings.disEnhanceWide ? 1 : 0;
            values[i++] = settings.disEnhanceBorder ? 1 : 0;
            values[i++] = settings.widescreenOverride ? 1 : 0;
            values[i++] = settings.wideScreenBG;
            values[i++] = settings.widescreenM7;
            values[i++] = settings.widescreenOBJ;
            values[i++] = settings.widescreenCOL;
            values[i++] = settings.aspectOverride;
            values[i] = settings.inaccurateEmuMode ? 1 : 0;
        }

        internal bool Matches(MainMenuManager.GameSpecificSettings settings)
        {
            if (!_valid || settings == null) return false;
            Fill(_compare, settings);
            for (var i = 0; i < _values.Length; i++) if (_values[i] != _compare[i]) return false;
            return true;
        }

        internal void CopyFrom(GameSettingsSnapshot source)
        {
            Array.Copy(source._values, _values, _values.Length);
            _valid = source._valid;
        }
    }
}
