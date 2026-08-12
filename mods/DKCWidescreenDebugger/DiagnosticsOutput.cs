using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace DKCWidescreenDebugger
{
    internal sealed class ScreenshotData
    {
        public byte[] Data;
        public string MimeType;
        public string Path;
        public string Target;
        public int Width;
        public int Height;
        public int Frame;
    }

    internal sealed class SessionLog : IDisposable
    {
        private readonly ManualLogSource _log;
        private readonly StreamWriter _events;
        private StreamWriter _cpu;
        private StreamWriter _writes;
        private StreamWriter _reads;
        private StreamWriter _ppu;
        private bool _dirty;

        public string Root { get; private set; }

        public SessionLog(string root, ManualLogSource log)
        {
            Root = root;
            _log = log;
            Directory.CreateDirectory(root);
            _events = NewWriter("events.jsonl");
            Event("session-start", new Dictionary<string, object>
            {
                { "pluginVersion", DKCWidescreenDebuggerPlugin.PluginVersion },
                { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                { "unity", Application.unityVersion },
                { "game", Application.version }
            });
        }

        public void Event(string type, IDictionary<string, object> data)
        {
            var item = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                { "type", type },
                { "data", data ?? new Dictionary<string, object>() }
            };
            _events.WriteLine(Json.Object(item));
            _dirty = true;
        }

        public void Cpu(string line)
        {
            if (_cpu == null)
            {
                _cpu = NewWriter("cpu-trace.csv");
                _cpu.WriteLine("frame,line,dot,cycles,pc,a,x,y,s,d,db,flags,instruction");
            }
            _cpu.WriteLine(line);
            _dirty = true;
        }

        public void Write(string line)
        {
            if (_writes == null)
            {
                _writes = NewWriter("memory-writes.csv");
                _writes.WriteLine("frame,line,dot,pc,address,value");
            }
            _writes.WriteLine(line);
            _dirty = true;
        }

        public void Read(string line)
        {
            if (_reads == null)
            {
                _reads = NewWriter("memory-reads.csv");
                _reads.WriteLine("frame,line,dot,pc,address,value");
            }
            _reads.WriteLine(line);
            _dirty = true;
        }

        public void Ppu(string line)
        {
            if (_ppu == null)
            {
                _ppu = NewWriter("ppu-register-writes.csv");
                _ppu.WriteLine("frame,line,dot,pc,address,value");
            }
            _ppu.WriteLine(line);
            _dirty = true;
        }

        public void Flush()
        {
            if (!_dirty) return;
            _events.Flush();
            if (_cpu != null) _cpu.Flush();
            if (_writes != null) _writes.Flush();
            if (_reads != null) _reads.Flush();
            if (_ppu != null) _ppu.Flush();
            _dirty = false;
        }

        private StreamWriter NewWriter(string name)
        {
            return new StreamWriter(Path.Combine(Root, name), true) { AutoFlush = false };
        }

        public void Dispose()
        {
            try { Event("session-end", new Dictionary<string, object>()); Flush(); } catch { }
            if (_cpu != null) _cpu.Dispose();
            if (_writes != null) _writes.Dispose();
            if (_reads != null) _reads.Dispose();
            if (_ppu != null) _ppu.Dispose();
            _events.Dispose();
            _log.LogInfo("Debugger session saved to " + Root);
        }
    }

    internal sealed class CaptureService
    {
        private static readonly string[] RendererArrayFields =
        {
            "ppu", "cg", "curcgpal", "oamBuffer", "oamBufferUsed", "wideScreenLengths", "wideOverride",
            "bgposlo", "bgposhi", "bgposhi2", "offsetPerTileX", "offsetPerTileY", "sprOnByLineMain",
            "sprOnByLineSub", "sprOnByLineMainOrigin", "sprOnByLineSubOrigin", "sprLeftXClamp", "sprRightXClamp"
        };

        private readonly SessionLog _session;
        private readonly ManualLogSource _log;

        public CaptureService(SessionLog session, ManualLogSource log)
        {
            _session = session;
            _log = log;
        }

        public string Capture(object master, string reason)
        {
            if (master == null) throw new InvalidOperationException("SuperZSNES is not running a game yet.");
            var frame = Reflect.IntCall(master, "GetFrameNo", -1);
            var folder = Path.Combine(_session.Root, "capture-f" + frame.ToString("D8") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(folder);

            var cpu = Reflect.Get(master, "CPUCore65c816");
            var memory = Reflect.Get(master, "CoreMemoryMap");
            var ppu = Reflect.Get(master, "CorePPU");
            var renderer = Reflect.Get(master, "snesRenderer");
            var menu = Reflect.Get(master, "mainMenuManager") ?? Reflect.Static("MainMenuManager", "Instance");
            var settings = Reflect.TryCall(menu, "GetGameSettings", string.Empty);

            WriteBytes(folder, "wram-7e7f.bin", Reflect.BytesCall(memory, "GetRam"));
            WriteBytes(folder, "sram.bin", Reflect.BytesCall(memory, "GetSram"));
            WriteBytes(folder, "vram.bin", Reflect.BytesCall(ppu, "GetPPUMemory"));
            WriteBytes(folder, "cgram.bin", Reflect.BytesCall(ppu, "GetCGMemory"));
            WriteBytes(folder, "cgram-frame-start.bin", Reflect.BytesCall(ppu, "GetCGMemoryStartFrame"));
            WriteBytes(folder, "oam.bin", Reflect.BytesCall(ppu, "GetOAMMemory"));
            WriteBytes(folder, "oam-frame-start.bin", Reflect.BytesCall(ppu, "GetStartFrameOAMMemory"));
            WriteBytes(folder, "io-registers.bin", Reflect.BytesCall(ppu, "GetIORegisters"));

            var cpuState = Reflect.TryCall(cpu, "GetSaveState");
            var ppuState = Reflect.TryCall(ppu, "GetState");
            File.WriteAllText(Path.Combine(folder, "cpu-state.json"), Reflect.ScalarObjectJson(cpuState));
            File.WriteAllText(Path.Combine(folder, "ppu-state.json"), Reflect.ScalarObjectJson(ppuState));
            File.WriteAllText(Path.Combine(folder, "widescreen-settings.json"), Reflect.ScalarObjectJson(settings));
            File.WriteAllText(Path.Combine(folder, "renderer-state.json"), RendererStateJson(renderer));

            foreach (var name in RendererArrayFields) DumpRendererField(folder, renderer, name);

            CaptureRenderTexture(folder, renderer, "GetMainScreenRenderTexture", "frame-main.png");
            CaptureRenderTexture(folder, renderer, "GetFinalComposedTexture", "frame-composed.png");

            var metadata = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                { "reason", reason },
                { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                { "frame", frame },
                { "line", Reflect.IntCall(master, "GetLineNo", -1) },
                { "dot", Reflect.IntCall(master, "GetPixelNo", -1) },
                { "pc", Reflect.UIntCall(cpu, "GetPCAddress", 0).ToString("X6") },
                { "unityVersion", Application.unityVersion },
                { "applicationVersion", Application.version },
                { "screenWidth", Screen.width },
                { "screenHeight", Screen.height }
            };
            File.WriteAllText(Path.Combine(folder, "capture.json"), Json.Object(metadata));
            _session.Event("capture", new Dictionary<string, object> { { "reason", reason }, { "frame", frame }, { "folder", folder } });
            _session.Flush();
            _log.LogInfo("Diagnostic capture saved: " + folder);
            return folder;
        }

        public ScreenshotData Screenshot(object master, string requestedTarget, string requestedFormat, int requestedQuality)
        {
            if (master == null) throw new InvalidOperationException("SuperZSNES is not running a game yet.");
            var renderer = Reflect.Get(master, "snesRenderer");
            var target = (requestedTarget ?? "main").Trim().ToLowerInvariant();
            var format = (requestedFormat ?? "png").Trim().ToLowerInvariant();
            if (format == "jpg") format = "jpeg";
            if (format != "png" && format != "jpeg") throw new ArgumentException("Screenshot format must be png or jpeg.");
            var quality = Math.Max(1, Math.Min(100, requestedQuality));
            byte[] data;
            int width;
            int height;

            if (target == "window")
            {
                var texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (texture == null) throw new InvalidOperationException("Unity could not capture the game window.");
                width = texture.width;
                height = texture.height;
                try { data = EncodeTexture(texture, format, quality); }
                finally { UnityEngine.Object.Destroy(texture); }
            }
            else
            {
                if (target != "main" && target != "sub" && target != "composed")
                    throw new ArgumentException("Screenshot target must be main, sub, composed, or window.");
                var method = target == "composed" ? "GetFinalComposedTexture" : "GetMainScreenRenderTexture";
                // The live transfer texture is the full widescreen result after
                // main/subscreen color math. GetFinalComposedTexture creates a
                // separate fixed 256x224 diagnostic texture, so use it only as
                // a fallback when the live transfer surface is unavailable.
                var requestedTexture = target == "composed"
                    ? Reflect.Get(renderer, "transferScreenRenderTexture") ?? Reflect.TryCall(renderer, method)
                    : target == "sub"
                        ? Reflect.Get(renderer, "subScreenRenderTexture")
                        : Reflect.TryCall(renderer, method);
                var composedTexture = requestedTexture as Texture2D;
                var renderTexture = requestedTexture as RenderTexture;
                if (composedTexture != null)
                {
                    width = composedTexture.width;
                    height = composedTexture.height;
                    try { data = EncodeTexture(composedTexture, format, quality); }
                    finally { UnityEngine.Object.Destroy(composedTexture); }
                }
                else
                {
                    if ((renderTexture == null || !renderTexture.IsCreated()) && target == "composed")
                    {
                        target = "main";
                        renderTexture = Reflect.TryCall(renderer, "GetMainScreenRenderTexture") as RenderTexture;
                    }
                    if (renderTexture == null || !renderTexture.IsCreated())
                        throw new InvalidOperationException("The requested emulator render texture is not available yet. Load a ROM and render at least one frame.");
                    data = ReadRenderTexture(renderTexture, format, quality, out width, out height);
                }
            }

            var frame = Reflect.IntCall(master, "GetFrameNo", -1);
            var folder = Path.Combine(_session.Root, "screenshots");
            Directory.CreateDirectory(folder);
            var extension = format == "jpeg" ? ".jpg" : ".png";
            var mimeType = format == "jpeg" ? "image/jpeg" : "image/png";
            var path = Path.Combine(folder, "screenshot-" + target + "-f" + frame.ToString("D8") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + extension);
            File.WriteAllBytes(path, data);
            _session.Event("screenshot", new Dictionary<string, object>
            {
                { "target", target }, { "format", format }, { "quality", quality }, { "frame", frame },
                { "width", width }, { "height", height }, { "bytes", data.Length }, { "path", path }
            });
            _session.Flush();
            return new ScreenshotData
            {
                Data = data, MimeType = mimeType, Path = path, Target = target,
                Width = width, Height = height, Frame = frame
            };
        }

        private static string RendererStateJson(object renderer)
        {
            if (renderer == null) return "null";
            var names = new[]
            {
                "numLines", "frameNo", "mode7Perspective", "mode7DisableFirstPerspectiveOnly", "mode7PerspectiveWrap",
                "mode7res", "tileScrollXScale", "ratioXL", "ratioXR", "ratioXL43", "ratioXR43", "ratioY",
                "mesh256Idx", "mesh64Idx", "mesh16Idx", "mesh4Idx", "mesh1Idx", "sizeAX", "sizeAY", "sizeBX", "sizeBY",
                "xClampSize", "dynamicFont", "dynamicFontStartAddr", "dynamicFontEndAddr", "dynamicFontColorPriority",
                "disableBG1", "disableBG2", "disableBG3", "disableBG4", "disableObj", "disableWin", "DebugLineStart", "DebugLineEnd"
            };
            return Reflect.ScalarObjectJson(renderer, names);
        }

        private void CaptureRenderTexture(string folder, object renderer, string method, string file)
        {
            try
            {
                var requestedTexture = Reflect.TryCall(renderer, method);
                var composedTexture = requestedTexture as Texture2D;
                if (composedTexture != null)
                {
                    try { File.WriteAllBytes(Path.Combine(folder, file), composedTexture.EncodeToPNG()); }
                    finally { UnityEngine.Object.Destroy(composedTexture); }
                    return;
                }

                var renderTexture = requestedTexture as RenderTexture;
                if (renderTexture == null || !renderTexture.IsCreated()) return;
                int width;
                int height;
                File.WriteAllBytes(Path.Combine(folder, file), ReadRenderTexture(renderTexture, "png", 100, out width, out height));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not capture " + file + ": " + ex.Message);
            }
        }

        private static byte[] ReadRenderTexture(RenderTexture target, string format, int quality, out int width, out int height)
        {
            var previous = RenderTexture.active;
            Texture2D texture = null;
            width = target.width;
            height = target.height;
            try
            {
                RenderTexture.active = target;
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                texture.Apply(false, false);
                return EncodeTexture(texture, format, quality);
            }
            finally
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                RenderTexture.active = previous;
            }
        }

        private static byte[] EncodeTexture(Texture2D texture, string format, int quality)
        {
            return format == "jpeg" ? texture.EncodeToJPG(quality) : texture.EncodeToPNG();
        }

        private static void WriteBytes(string folder, string name, byte[] bytes)
        {
            if (bytes != null) File.WriteAllBytes(Path.Combine(folder, name), bytes);
        }

        private static void DumpRendererField(string folder, object renderer, string name)
        {
            if (renderer == null) return;
            var value = Reflect.Get(renderer, name);
            if (value == null) return;
            var enumerable = value as IEnumerable;
            if (enumerable == null || value is string) return;
            var values = new List<object>();
            foreach (var item in enumerable)
            {
                if (item == null || item.GetType().IsPrimitive || item is decimal) values.Add(item);
                else return;
            }
            File.WriteAllText(Path.Combine(folder, "renderer-" + name + ".json"), Json.Value(values));
        }
    }
}
