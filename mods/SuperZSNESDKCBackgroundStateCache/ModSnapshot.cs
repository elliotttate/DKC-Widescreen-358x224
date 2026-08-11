using System;
using System.Collections.Generic;

namespace SuperZSNESDKCBackgroundStateCache
{
    internal sealed class ModSnapshot
    {
        private ModData _mod;
        private int _versionNo;
        private SceneSnapshot[] _scenes;

        internal void Capture(ModData mod)
        {
            _mod = mod;
            if (mod == null)
            {
                _versionNo = 0;
                _scenes = null;
                return;
            }
            _versionNo = mod.versionNo;
            var count = mod.sceneData == null ? 0 : mod.sceneData.Count;
            if (_scenes == null || _scenes.Length != count)
            {
                _scenes = new SceneSnapshot[count];
                for (var i = 0; i < count; i++) _scenes[i] = new SceneSnapshot();
            }
            for (var i = 0; i < count; i++) _scenes[i].Capture(mod.sceneData[i]);
        }

        internal bool Matches(ModData mod)
        {
            if (!ReferenceEquals(_mod, mod)) return false;
            if (mod == null) return _scenes == null;
            if (_versionNo != mod.versionNo) return false;
            var count = mod.sceneData == null ? 0 : mod.sceneData.Count;
            if (_scenes == null || _scenes.Length != count) return false;
            for (var i = 0; i < count; i++) if (!_scenes[i].Matches(mod.sceneData[i])) return false;
            return true;
        }

        internal void CopyFrom(ModSnapshot source)
        {
            _mod = source._mod;
            _versionNo = source._versionNo;
            if (source._scenes == null) { _scenes = null; return; }
            if (_scenes == null || _scenes.Length != source._scenes.Length)
            {
                _scenes = new SceneSnapshot[source._scenes.Length];
                for (var i = 0; i < _scenes.Length; i++) _scenes[i] = new SceneSnapshot();
            }
            for (var i = 0; i < _scenes.Length; i++) _scenes[i].CopyFrom(source._scenes[i]);
        }
    }

    internal sealed class SceneSnapshot
    {
        private ModData.SceneData _scene;
        private string _name;
        private int _fadeBits;
        private bool _reuse;
        private bool _global;
        private int[] _wide;
        private uint[][] _palDetect;
        private readonly SceneInfoSnapshot _info = new SceneInfoSnapshot();

        internal void Capture(ModData.SceneData scene)
        {
            _scene = scene;
            if (scene == null)
            {
                _name = null; _wide = null; _palDetect = null; _info.Capture(null);
                return;
            }
            _name = scene.sceneName;
            _fadeBits = ExactBits.Single(scene.wideScreenFadeOut);
            _reuse = scene.wideReuseInsideVisuals;
            _global = scene.globalMaterialScene;
            CopyInts(scene.wideScreenLengths, ref _wide);
            CopyNestedUints(scene.palDetectCRCValues, ref _palDetect);
            _info.Capture(scene.sceneInfo);
        }

        internal bool Matches(ModData.SceneData scene)
        {
            if (!ReferenceEquals(_scene, scene)) return false;
            if (scene == null) return true;
            return string.Equals(_name, scene.sceneName, StringComparison.Ordinal) &&
                   _fadeBits == ExactBits.Single(scene.wideScreenFadeOut) && _reuse == scene.wideReuseInsideVisuals &&
                   _global == scene.globalMaterialScene && EqualInts(_wide, scene.wideScreenLengths) &&
                   EqualNestedUints(_palDetect, scene.palDetectCRCValues) && _info.Matches(scene.sceneInfo);
        }

        internal void CopyFrom(SceneSnapshot source)
        {
            _scene = source._scene;
            _name = source._name;
            _fadeBits = source._fadeBits;
            _reuse = source._reuse;
            _global = source._global;
            CopyArray(source._wide, ref _wide);
            if (source._palDetect == null) _palDetect = null;
            else
            {
                if (_palDetect == null || _palDetect.Length != source._palDetect.Length)
                    _palDetect = new uint[source._palDetect.Length][];
                for (var i = 0; i < source._palDetect.Length; i++) CopyArray(source._palDetect[i], ref _palDetect[i]);
            }
            _info.CopyFrom(source._info);
        }

        private static void CopyInts(List<int> source, ref int[] destination)
        {
            if (source == null) { destination = null; return; }
            if (destination == null || destination.Length != source.Count) destination = new int[source.Count];
            for (var i = 0; i < source.Count; i++) destination[i] = source[i];
        }

        private static bool EqualInts(int[] expected, List<int> actual)
        {
            if (expected == null || actual == null) return expected == null && actual == null;
            if (expected.Length != actual.Count) return false;
            for (var i = 0; i < expected.Length; i++) if (expected[i] != actual[i]) return false;
            return true;
        }

        private static void CopyNestedUints(List<List<uint>> source, ref uint[][] destination)
        {
            if (source == null) { destination = null; return; }
            if (destination == null || destination.Length != source.Count) destination = new uint[source.Count][];
            for (var i = 0; i < source.Count; i++)
            {
                var list = source[i];
                if (list == null) { destination[i] = null; continue; }
                if (destination[i] == null || destination[i].Length != list.Count) destination[i] = new uint[list.Count];
                for (var j = 0; j < list.Count; j++) destination[i][j] = list[j];
            }
        }

        private static bool EqualNestedUints(uint[][] expected, List<List<uint>> actual)
        {
            if (expected == null || actual == null) return expected == null && actual == null;
            if (expected.Length != actual.Count) return false;
            for (var i = 0; i < expected.Length; i++)
            {
                var a = expected[i]; var b = actual[i];
                if (a == null || b == null) { if (a != null || b != null) return false; continue; }
                if (a.Length != b.Count) return false;
                for (var j = 0; j < a.Length; j++) if (a[j] != b[j]) return false;
            }
            return true;
        }

        private static void CopyArray(int[] source, ref int[] destination)
        {
            if (source == null) { destination = null; return; }
            if (destination == null || destination.Length != source.Length) destination = new int[source.Length];
            Array.Copy(source, destination, source.Length);
        }

        private static void CopyArray(uint[] source, ref uint[] destination)
        {
            if (source == null) { destination = null; return; }
            if (destination == null || destination.Length != source.Length) destination = new uint[source.Length];
            Array.Copy(source, destination, source.Length);
        }
    }

    internal sealed class SceneInfoSnapshot
    {
        private ModData.SceneInfo _info;
        private readonly int[] _floatBits = new int[28];
        private readonly int[] _ints = new int[14];
        private readonly int[] _compareFloatBits = new int[28];
        private readonly int[] _compareInts = new int[14];
        private float[] _zPositions;
        private float[] _zScales;

        internal void Capture(ModData.SceneInfo info)
        {
            _info = info;
            if (info == null) return;
            Fill(info, _floatBits, _ints);
            CopyFloats(info.zPositions, ref _zPositions);
            CopyFloats(info.zScales, ref _zScales);
        }

        private static void Fill(ModData.SceneInfo info, int[] floats, int[] ints)
        {
            var f = 0;
            floats[f++] = ExactBits.Single(info.ambLightColR); floats[f++] = ExactBits.Single(info.ambLightColG); floats[f++] = ExactBits.Single(info.ambLightColB);
            floats[f++] = ExactBits.Single(info.dirLightColR); floats[f++] = ExactBits.Single(info.dirLightColG); floats[f++] = ExactBits.Single(info.dirLightColB);
            floats[f++] = ExactBits.Single(info.lightDirX); floats[f++] = ExactBits.Single(info.lightDirY); floats[f++] = ExactBits.Single(info.lightDirZ);
            floats[f++] = ExactBits.Single(info.ptLightColR); floats[f++] = ExactBits.Single(info.ptLightColG); floats[f++] = ExactBits.Single(info.ptLightColB);
            floats[f++] = ExactBits.Single(info.ptLightOfsZ); floats[f++] = ExactBits.Single(info.pointLightStr);
            floats[f++] = ExactBits.Single(info.m7zofs); floats[f++] = ExactBits.Single(info.m7forwofs); floats[f++] = ExactBits.Single(info.m7pangle);
            floats[f++] = ExactBits.Single(info.m7pfov); floats[f++] = ExactBits.Single(info.m7xscale); floats[f++] = ExactBits.Single(info.m7zofsp2);
            floats[f++] = ExactBits.Single(info.m7forwofsp2); floats[f++] = ExactBits.Single(info.m7panglep2); floats[f] = ExactBits.Single(info.overclockRatio);
            var i = 0;
            ints[i++] = (int)info.enhancementMode; ints[i++] = info.pointLight ? 1 : 0;
            ints[i++] = info.m7dualScreen ? 1 : 0; ints[i++] = info.m7scrn1center; ints[i++] = info.m7scrn2center;
            ints[i++] = info.m7dsscrn1miny; ints[i++] = info.m7dsscrn1maxy; ints[i++] = info.m7dsscrn2miny;
            ints[i++] = info.m7dsscrn2maxy; ints[i++] = info.zNeutralPos; ints[i++] = info.neutralZScale ? 1 : 0;
            ints[i++] = info.enableHDR ? 1 : 0; ints[i++] = info.enableShadows ? 1 : 0;
            ints[i] = (info.mode7Perspective ? 1 : 0) | (info.hiResWindow ? 2 : 0);
        }

        internal bool Matches(ModData.SceneInfo info)
        {
            if (!ReferenceEquals(_info, info)) return false;
            if (info == null) return true;
            Fill(info, _compareFloatBits, _compareInts);
            for (var i = 0; i < _floatBits.Length; i++) if (_floatBits[i] != _compareFloatBits[i]) return false;
            for (var i = 0; i < _ints.Length; i++) if (_ints[i] != _compareInts[i]) return false;
            return EqualFloats(_zPositions, info.zPositions) && EqualFloats(_zScales, info.zScales);
        }

        internal void CopyFrom(SceneInfoSnapshot source)
        {
            _info = source._info;
            Array.Copy(source._floatBits, _floatBits, _floatBits.Length);
            Array.Copy(source._ints, _ints, _ints.Length);
            CopyArray(source._zPositions, ref _zPositions);
            CopyArray(source._zScales, ref _zScales);
        }

        private static void CopyFloats(List<float> source, ref float[] destination)
        {
            if (source == null) { destination = null; return; }
            if (destination == null || destination.Length != source.Count) destination = new float[source.Count];
            for (var i = 0; i < source.Count; i++) destination[i] = source[i];
        }

        private static bool EqualFloats(float[] expected, List<float> actual)
        {
            if (expected == null || actual == null) return expected == null && actual == null;
            if (expected.Length != actual.Count) return false;
            for (var i = 0; i < expected.Length; i++) if (ExactBits.Single(expected[i]) != ExactBits.Single(actual[i])) return false;
            return true;
        }

        private static void CopyArray(float[] source, ref float[] destination)
        {
            if (source == null) { destination = null; return; }
            if (destination == null || destination.Length != source.Length) destination = new float[source.Length];
            Array.Copy(source, destination, source.Length);
        }
    }
}
