using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SuperZSNESSpriteDepthStudio
{
    internal static class SpriteDepthRuntime
    {
        private static readonly byte[] Oam = new byte[544];
        private static readonly byte[] Vram = new byte[65536];
        private static readonly byte[] Cgram = new byte[224 * 512];
        private static readonly byte[] CgramWorking = new byte[512];
        private static readonly byte[] ObjSel = new byte[224];
        private static readonly byte[] ObjActive = new byte[224];
        private static readonly byte[] Registers = new byte[64];
        private static readonly float[] Offsets = new float[128];
        private static readonly float[] Scales = Enumerable.Repeat(1f, 128).ToArray();
        private static ManualLogSource _log;
        private static SpriteDepthNativePatcher _native;
        private static ConfigEntry<float> _layerSpacing;
        private static ConfigEntry<float> _orderSpacing;
        private static ConfigEntry<bool> _require3D;
        private static ConfigEntry<bool> _persistCaptures;
        private static string _root;
        private static string _exchange;
        private static string _profiles;
        private static string _captureRequest;
        private static string _launchRequest;
        private static string _loadStateRequest;
        private static string _lastRomPath = string.Empty;
        private static string _lastRomSha = string.Empty;
        private static string _profilePath = string.Empty;
        private static SpriteDepthProfile _profile = new SpriteDepthProfile();
        private static DateTime _profileWriteUtc;
        private static long _nextPoll;
        private static long _tableUpdates;
        private static long _captures;
        private static string _lastError = string.Empty;
        private static string _componentSnapshotJson = string.Empty;

        internal static string RootDirectory => _root;

        internal static void Initialize(ManualLogSource log, SpriteDepthNativePatcher native,
            ConfigEntry<float> layerSpacing, ConfigEntry<float> orderSpacing,
            ConfigEntry<bool> require3D,
            ConfigEntry<bool> persistCaptures)
        {
            _log = log;
            _native = native;
            _layerSpacing = layerSpacing;
            _orderSpacing = orderSpacing;
            _require3D = require3D;
            _persistCaptures = persistCaptures;
            _root = Path.Combine(Paths.PluginPath, "SuperZSNESSpriteDepthStudioIL2CPP");
            _exchange = Path.Combine(_root, "Exchange");
            _profiles = Path.Combine(_root, "Profiles");
            _captureRequest = Path.Combine(_exchange, "capture.request");
            _launchRequest = Path.Combine(_exchange, "launch.request");
            _loadStateRequest = Path.Combine(_exchange, "load-state.request");
            Directory.CreateDirectory(_exchange);
            Directory.CreateDirectory(_profiles);
            WriteStatus("loaded");
        }

        internal static void BeforeRender(PPURenderer renderer)
        {
            try
            {
                if (renderer?.snesPPU == null || _native == null || !_native.Applied) return;
                string rom = MainMenuManager.Instance?.GetLoadedGameFilename() ?? string.Empty;
                EnsureProfile(rom);
                Copy(renderer.snesPPU.GetStartFrameOAMMemory(), Oam);
                BuildObjectLines(renderer.snesPPU, ObjSel, ObjActive, out _, out _);
                bool allow = !_require3D.Value ||
                    MainMenuManager.Instance?.mainMenuSettings?.gfxMode == MainMenuManager.GFXModes.Gimmick3D;
                float spacing = Math.Max(0f, Math.Min(2f, _layerSpacing.Value));
                float orderSpacing = Math.Max(0.0001f, Math.Min(1f / 128f,
                    _orderSpacing.Value));
                float cameraDistance = Math.Max(5f, Math.Min(55f, 30f - renderer.zPos));
                int priorityAddress = renderer.snesPPU.GetOAMPriority();
                int startSlot = priorityAddress != 0 ? (priorityAddress & 0xFE) >> 1 : 0;
                for (int slot = 0; slot < 128; slot++)
                {
                    int layer = allow ? ResolveDepthFast(slot, Oam, ObjSel, _profile) : 0;
                    int order = SpriteDepthOrdering.RenderOrder(startSlot, slot);
                    float targetOrderZ = order * orderSpacing;
                    float offset = allow
                        ? layer * spacing + SpriteDepthOrdering.CompressedOffset(
                            startSlot, slot, orderSpacing) : 0f;
                    Offsets[slot] = offset;
                    int priority = (Oam[slot * 4 + 3] >> 4) & 3;
                    int plane = renderer.sprPriorityToZ == null || priority >= renderer.sprPriorityToZ.Length
                        ? 0 : renderer.sprPriorityToZ[priority];
                    float baseZ = renderer.zPositions == null || plane < 0 || plane >= renderer.zPositions.Length
                        ? 0f : renderer.zPositions[plane];
                    float denominator = cameraDistance + baseZ;
                    float targetZ = baseZ + targetOrderZ + layer * spacing;
                    Scales[slot] = !allow || Math.Abs(denominator) < 0.001f ? 1f :
                        Math.Max(0.05f, Math.Min(20f,
                            (cameraDistance + targetZ) / denominator));
                }
                _native.Update(Offsets, Scales);
                _tableUpdates++;
            }
            catch (Exception exception)
            {
                _lastError = exception.GetType().Name + ": " + exception.Message;
                _log?.LogError("Sprite depth table update failed: " + exception);
                ClearTables();
            }
        }

        internal static void Tick()
        {
            try
            {
                long now = Stopwatch.GetTimestamp();
                bool hotkey = Input.GetKeyDown(KeyCode.F10);
                bool requested = File.Exists(_captureRequest);
                if (hotkey || requested)
                {
                    CaptureCurrent();
                    try { if (requested) File.Delete(_captureRequest); } catch { }
                }
                if (File.Exists(_launchRequest))
                {
                    try { File.Delete(_launchRequest); } catch { }
                    LaunchStudio();
                }
                if (File.Exists(_loadStateRequest))
                {
                    string request = File.ReadAllText(_loadStateRequest).Trim();
                    if (string.IsNullOrEmpty(request))
                        throw new InvalidDataException("load-state.request is empty.");
                    if (request.StartsWith("suffix:", StringComparison.OrdinalIgnoreCase))
                        MasterExecutor.Instance.LoadState(request.Substring(7));
                    else
                    {
                        string path = Path.GetFullPath(request);
                        if (!File.Exists(path))
                            throw new FileNotFoundException("Requested save state is missing.", path);
                        MasterExecutor.Instance.LoadStateFilename(path);
                    }
                    File.Delete(_loadStateRequest);
                    WriteStatus("state-loaded");
                }
                if (now >= _nextPoll)
                {
                    _nextPoll = now + Stopwatch.Frequency / 4;
                    EnsureProfile(MainMenuManager.Instance?.GetLoadedGameFilename() ?? string.Empty);
                    ReloadProfileIfChanged();
                }
            }
            catch (Exception exception)
            {
                _lastError = exception.GetType().Name + ": " + exception.Message;
                _log?.LogError("Object Depth Studio tick failed: " + exception);
            }
        }

        internal static void LaunchStudio()
        {
            string executable = Path.Combine(_root, "Studio", "SpriteDepthStudio.exe");
            if (!File.Exists(executable))
            {
                _log?.LogWarning("Object Depth Studio executable is missing: " + executable);
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--root \"" + _root + "\"",
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = true
            });
        }

        internal static void Shutdown()
        {
            ClearTables();
            WriteStatus("unloaded");
        }

        private static void CaptureCurrent()
        {
            PPURenderer renderer = MasterExecutor.Instance?.snesRenderer;
            SNESPPU ppu = renderer?.snesPPU ?? MasterExecutor.Instance?.CorePPU;
            if (ppu == null) throw new InvalidOperationException("No running SNES PPU is available.");
            string rom = MainMenuManager.Instance?.GetLoadedGameFilename() ?? string.Empty;
            EnsureProfile(rom);
            Copy(ppu.GetStartFrameOAMMemory(), Oam);
            Copy(ppu.GetPPUMemory(), Vram);
            CopyRegisters(ppu, Registers);
            BuildCgramLines(ppu, Cgram, out int cgramWrites);
            BuildObjectLines(ppu, ObjSel, ObjActive, out int oamWrites, out int objSelWrites);
            List<SpriteRecord> sprites = SpriteDecoder.Decode(Vram, Oam, Cgram, ObjSel, ObjActive);
            string componentPath = Path.Combine(_exchange, "snapshot-components.json");
            BackgroundComponentReport componentReport = ExportComponentSnapshot(componentPath);
            int[] scrollX = { ppu._startScrollXBG1, ppu._startScrollXBG2,
                ppu._startScrollXBG3 };
            int[] scrollY = { ppu._startScrollYBG1, ppu._startScrollYBG2,
                ppu._startScrollYBG3 };
            int backgroundObjects = componentReport == null ? 0 :
                BackgroundObjectDecoder.Decode(Vram, Cgram, Registers, scrollX, scrollY,
                    componentReport).Count;
            string level = componentReport?.Level ?? ReadLevel(ppu).ToString("X4");
            int levelId = int.TryParse(level, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out int parsedLevel) ? parsedLevel : ReadLevel(ppu);
            List<GameActorRecord> actors = CaptureGameActors(ppu);
            string componentProfile = Path.Combine(Paths.PluginPath,
                "SuperZSNESLayerDepthControllerIL2CPP", "profiles",
                "level-" + level + ".json");
            var manifest = new SpriteCaptureManifest
            {
                CapturedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                RomPath = rom,
                RomFileName = Path.GetFileName(rom ?? string.Empty),
                RomSha256 = _lastRomSha,
                ProfileFile = _profilePath,
                PriorityAddress = ppu.GetOAMPriority(),
                MidFrameOamWrites = oamWrites,
                MidFrameObjSelWrites = objSelWrites,
                MidFrameCgramWrites = cgramWrites,
                CgramBytes = Cgram.Length,
                ActiveSpriteCount = sprites.Count(s => s.IntersectsScreen),
                Level = level,
                LevelName = DkcSemanticNames.Level(levelId),
                Actors = actors,
                BackgroundScrollX = scrollX,
                BackgroundScrollY = scrollY,
                BackgroundDepthStep = componentReport?.Spacing > 0f
                    ? componentReport.Spacing : 0.08f,
                VisibleBackgroundObjectCount = backgroundObjects,
                ComponentReportFile = componentReport == null ? string.Empty :
                    "snapshot-components.json",
                ComponentProfileFile = componentProfile
            };
            WriteCapture(_exchange, manifest);
            if (_persistCaptures.Value)
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
                WriteCapture(Path.Combine(_root, "Captures", stamp), manifest);
            }
            _captures++;
            WriteStatus("captured");
            _log?.LogInfo("Captured " + manifest.ActiveSpriteCount + " visible OAM parts and " +
                          manifest.VisibleBackgroundObjectCount +
                          " visible background objects. Open SpriteDepthStudio.exe.");
        }

        private static void WriteCapture(string directory, SpriteCaptureManifest manifest)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, manifest.OamFile), Oam);
            File.WriteAllBytes(Path.Combine(directory, manifest.VramFile), Vram);
            File.WriteAllBytes(Path.Combine(directory, manifest.CgramFile), Cgram);
            File.WriteAllBytes(Path.Combine(directory, manifest.ObjSelFile), ObjSel);
            File.WriteAllBytes(Path.Combine(directory, manifest.ObjActiveFile), ObjActive);
            File.WriteAllBytes(Path.Combine(directory, manifest.RegistersFile), Registers);
            if (!string.IsNullOrEmpty(manifest.ComponentReportFile) &&
                !string.IsNullOrEmpty(_componentSnapshotJson))
                File.WriteAllText(Path.Combine(directory, manifest.ComponentReportFile),
                    _componentSnapshotJson);
            SpriteDepthFiles.WriteJsonAtomic(Path.Combine(directory, "snapshot.json"), manifest);
        }

        private static void CopyRegisters(SNESPPU ppu, byte[] destination)
        {
            if (ppu?._ppuStartFrame == null || ppu._ppuStartFrame.Length < destination.Length)
                throw new InvalidOperationException("PPU start-register snapshot is unavailable.");
            for (int i = 0; i < destination.Length; i++)
                destination[i] = ppu._ppuStartFrame[i];
        }

        private static int ReadLevel(SNESPPU ppu)
        {
            try
            {
                Il2CppStructArray<byte> ram = ppu?.masterExecutor?.CoreMemoryMap?.GetRam();
                return ram != null && ram.Length > 0x31 ? ram[0x30] | (ram[0x31] << 8) : 0;
            }
            catch { return 0; }
        }

        private static List<GameActorRecord> CaptureGameActors(SNESPPU ppu)
        {
            var result = new List<GameActorRecord>();
            try
            {
                Il2CppStructArray<byte> ram = ppu?.masterExecutor?.CoreMemoryMap?.GetRam();
                if (ram == null || ram.Length < 0x170F) return result;
                int layerX = ReadWord(ram, 0x088B);
                int layerY = ReadWord(ram, 0x0895);
                for (int slot = 0; slot < 26; slot++)
                {
                    int offset = slot * 2;
                    int id = ReadWord(ram, 0x0D45 + offset);
                    if (id <= 0 || id > 0xFF) continue;
                    int worldX = ReadWord(ram, 0x0B19 + offset);
                    int worldY = ReadWord(ram, 0x0BC1 + offset);
                    result.Add(new GameActorRecord
                    {
                        ActorSlot = slot,
                        SpriteId = id,
                        Name = DkcSemanticNames.Actor(id),
                        WorldX = worldX,
                        WorldY = worldY,
                        ScreenX = SignedDelta(worldX, layerX),
                        ScreenY = SignedDelta(worldY, layerY),
                        CurrentPose = ReadWord(ram, 0x0D11 + offset),
                        DisplayedPose = ReadWord(ram, 0x0AE5 + offset)
                    });
                }
            }
            catch (Exception exception)
            {
                _log?.LogWarning("Could not capture named DKC actors: " + exception.Message);
            }
            return result;
        }

        private static int ReadWord(Il2CppStructArray<byte> ram, int address) =>
            ram[address] | (ram[address + 1] << 8);

        private static int SignedDelta(int value, int origin) =>
            (short)((value - origin) & 0xFFFF);

        private static BackgroundComponentReport ExportComponentSnapshot(string destination)
        {
            _componentSnapshotJson = string.Empty;
            try
            {
                bool exported = false;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type api = assembly.GetType(
                        "SuperZSNESLayerDepthControllerIL2CPP.LayerDepthAuthoringApi", false);
                    MethodInfo method = api?.GetMethod("ExportCurrentComponents",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method == null) continue;
                    exported = method.Invoke(null, new object[] { destination }) is bool ok && ok;
                    break;
                }
                if (!exported)
                {
                    string fallback = Path.Combine(Paths.PluginPath,
                        "SuperZSNESLayerDepthControllerIL2CPP", "components-current.json");
                    if (File.Exists(fallback)) File.Copy(fallback, destination, true);
                }
                if (!File.Exists(destination)) return null;
                _componentSnapshotJson = File.ReadAllText(destination);
                return SpriteDepthFiles.ReadJson<BackgroundComponentReport>(destination);
            }
            catch (Exception exception)
            {
                _log?.LogWarning("Could not capture background component catalog: " +
                    exception.Message);
                return null;
            }
        }

        private static void BuildObjectLines(SNESPPU ppu, byte[] destination,
            byte[] activeDestination,
            out int oamWrites, out int objSelWrites)
        {
            oamWrites = 0; objSelWrites = 0;
            byte value = ppu._ppuStartFrame != null && ppu._ppuStartFrame.Length > 1
                ? ppu._ppuStartFrame[1] : (byte)0;
            byte inidisp = ppu._ppuStartFrame != null && ppu._ppuStartFrame.Length > 0 ? ppu._ppuStartFrame[0] : (byte)0x80;
            byte tm = ppu._ppuStartFrame != null && ppu._ppuStartFrame.Length > 44 ? ppu._ppuStartFrame[44] : (byte)0;
            byte ts = ppu._ppuStartFrame != null && ppu._ppuStartFrame.Length > 45 ? ppu._ppuStartFrame[45] : (byte)0;
            byte cgwsel = ppu._ppuStartFrame != null && ppu._ppuStartFrame.Length > 48 ? ppu._ppuStartFrame[48] : (byte)0;
            int eventIndex = 0;
            for (int line = 0; line < destination.Length; line++)
            {
                while (ppu._ppuLineChanges != null && eventIndex < ppu._curPPUChangeIdx &&
                       eventIndex < ppu._ppuLineChanges.Length &&
                       ppu._ppuLineChanges[eventIndex].lineNo <= line)
                {
                    SNESPPU.PPULineChange change = ppu._ppuLineChanges[eventIndex++];
                    if (change.address == 0x2101) { value = change.val; objSelWrites++; }
                    if (change.address == 0x2100) inidisp=change.val;
                    if (change.address == 0x212C) tm=change.val;
                    if (change.address == 0x212D) ts=change.val;
                    if (change.address == 0x2130) cgwsel=change.val;
                    if (change.address >= 0x2102 && change.address <= 0x2104) oamWrites++;
                }
                destination[line] = value;
                if(activeDestination!=null&&line<activeDestination.Length)
                    activeDestination[line]=(byte)(((inidisp&0x80)==0&&
                        (((tm&0x10)!=0)||((ts&0x10)!=0&&(cgwsel&2)!=0)))?1:0);
            }
        }

        private static void BuildCgramLines(SNESPPU ppu, byte[] destination,
            out int changeCount)
        {
            if(destination==null||destination.Length!=224*512)
                throw new ArgumentException("CGRAM line capture must be 114688 bytes.");
            Copy(ppu.GetCGMemoryStartFrame(),CgramWorking);
            int eventIndex=0;changeCount=0;
            for(int line=0;line<224;line++)
            {
                while(ppu._cgLineChanges!=null&&eventIndex<ppu._cgChangeIdx&&
                      eventIndex<ppu._cgLineChanges.Length&&ppu._cgLineChanges[eventIndex].lineNo<=line)
                {
                    SNESPPU.CGLineChange change=ppu._cgLineChanges[eventIndex++];
                    int address=(change.colNo&255)*2;
                    CgramWorking[address]=change.colorLo;CgramWorking[address+1]=change.colorHi;
                    changeCount++;
                }
                Buffer.BlockCopy(CgramWorking,0,destination,line*512,512);
            }
        }

        private static int ResolveDepthFast(int slot, byte[] oam, byte[] objSel,
            SpriteDepthProfile profile)
        {
            if (profile?.Rules == null || profile.Rules.Count == 0) return 0;
            SpriteRecord sprite = SpriteDecoder.ReadMetadata(slot, oam, objSel);
            return SpriteDepthRules.Resolve(profile, sprite);
        }

        private static void EnsureProfile(string romPath)
        {
            romPath ??= string.Empty;
            if (string.Equals(romPath, _lastRomPath, StringComparison.OrdinalIgnoreCase)) return;
            _lastRomPath = romPath;
            _lastRomSha = HashRom(romPath);
            _profilePath = Path.Combine(_profiles, SpriteDepthFiles.SafeProfileName(
                Path.GetFileName(romPath), _lastRomSha));
            _profile = SpriteDepthFiles.ReadJson<SpriteDepthProfile>(_profilePath) ??
                new SpriteDepthProfile
                {
                    RomFileName = Path.GetFileName(romPath), RomSha256 = _lastRomSha
                };
            _profileWriteUtc = File.Exists(_profilePath) ? File.GetLastWriteTimeUtc(_profilePath) : DateTime.MinValue;
        }

        private static void ReloadProfileIfChanged()
        {
            if (string.IsNullOrEmpty(_profilePath) || !File.Exists(_profilePath)) return;
            DateTime write = File.GetLastWriteTimeUtc(_profilePath);
            if (write <= _profileWriteUtc) return;
            SpriteDepthProfile loaded = SpriteDepthFiles.ReadJson<SpriteDepthProfile>(_profilePath);
            if (loaded != null) { _profile = loaded; _profileWriteUtc = write; WriteStatus("profile-reloaded"); }
        }

        private static string HashRom(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            }
            catch (Exception exception) { _log?.LogWarning("Could not hash ROM: " + exception.Message); }
            return string.Empty;
        }

        private static void ClearTables()
        {
            Array.Clear(Offsets, 0, Offsets.Length);
            for (int i=0;i<Scales.Length;i++) Scales[i]=1f;
            try { if (_native?.Applied == true) _native.Update(Offsets, Scales); } catch { }
        }

        private static void Copy(Il2CppStructArray<byte> source, byte[] destination)
        {
            if (source == null || source.Length != destination.Length)
                throw new InvalidOperationException("Unexpected PPU buffer length; wanted " + destination.Length + ".");
            for (int i=0;i<destination.Length;i++) destination[i]=source[i];
        }

        private static void WriteStatus(string state)
        {
            try
            {
                Directory.CreateDirectory(_root);
                File.WriteAllText(Path.Combine(_root, "status.json"), "{" +
                    "\"version\":\"0.4.0\",\"state\":\"" + Escape(state) + "\"," +
                    "\"nativeApplied\":" + (_native?.Applied == true ? "true" : "false") + "," +
                    "\"tableUpdates\":" + _tableUpdates + ",\"captures\":" + _captures + "," +
                    "\"rom\":\"" + Escape(_lastRomPath) + "\",\"profile\":\"" + Escape(_profilePath) + "\"," +
                    "\"rules\":" + (_profile?.Rules?.Count ?? 0) + ",\"lastError\":\"" + Escape(_lastError) + "\"}");
            }
            catch { }
        }

        private static string Escape(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
