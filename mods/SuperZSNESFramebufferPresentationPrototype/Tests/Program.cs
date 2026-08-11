using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using SuperZSNESFramebufferPresentationPrototype;
using UnityEngine;

namespace SuperZSNESFramebufferPresentationPrototype.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var gamePath = args.Length == 0
                    ? Path.Combine(RequiredEnvironment("SUPERZSNES_MANAGED_DIR"), "Assembly-CSharp.dll")
                    : Path.GetFullPath(args[0]);
                var managed = Path.GetDirectoryName(gamePath);
                var bepinex = Path.Combine(RequiredEnvironment("BEPINEX_ROOT"), "BepInEx", "core");
                AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) => Resolve(eventArgs.Name, managed, bepinex);
                VerifyExactGamePresentationShape(gamePath);
                VerifyUnityUploadSurface();
                VerifyPatchAndFallbackSurface();
                VerifyIndexedExpansion();
                VerifyProviderRegistration();
                Console.WriteLine("PASS framebuffer presentation prototype offline verification");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL " + exception);
                return 1;
            }
        }

        private static string RequiredEnvironment(string name) =>
            Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Set " + name + " before running the verifier.");

        private static void VerifyExactGamePresentationShape(string gamePath)
        {
            using (var game = AssemblyDefinition.ReadAssembly(gamePath))
            {
                var renderer = game.MainModule.Types.Single(type => type.FullName == "PPURenderer");
                var generate = renderer.Methods.Single(method => method.Name == "GenerateBackgrounds" &&
                    method.Parameters.Count == 0 && method.ReturnType.FullName == "System.Void");
                Require(generate.HasBody, "GenerateBackgrounds has no body.");
                Require(renderer.Fields.Single(field => field.Name == "mainScreenBlitter").FieldType.FullName == "MainScreenBlit",
                    "PPURenderer.mainScreenBlitter type changed.");

                var blitter = game.MainModule.Types.Single(type => type.FullName == "MainScreenBlit");
                var onRender = blitter.Methods.Single(method => method.Name == "OnRenderImage" &&
                    method.Parameters.Count == 2 && method.Parameters.All(parameter =>
                        parameter.ParameterType.FullName == "UnityEngine.RenderTexture"));
                Require(blitter.Fields.Single(field => field.Name == "_transferMaterialUsed").FieldType.FullName == "UnityEngine.Material",
                    "MainScreenBlit transfer material field changed.");
                Require(blitter.Fields.Single(field => field.Name == "transferRenderTexture").FieldType.FullName == "UnityEngine.RenderTexture",
                    "MainScreenBlit transfer target field changed.");
                var blits = onRender.Body.Instructions.Count(instruction => instruction.Operand is MethodReference method &&
                    method.DeclaringType.FullName == "UnityEngine.Graphics" && method.Name == "Blit");
                Require(blits == 3, "Stock OnRenderImage must retain its two active-path Blits plus one fallback Blit.");
            }
            Console.WriteLine("gameShape generateBackgrounds=exact onRenderImageBlits=3 transferChain=exact");
        }

        private static void VerifyUnityUploadSurface()
        {
            Require(typeof(Texture2D).GetConstructor(new[] { typeof(int), typeof(int), typeof(TextureFormat), typeof(bool) }) != null,
                "Texture2D persistent RGBA constructor is unavailable.");
            Require(typeof(Texture2D).GetMethod("LoadRawTextureData", new[] { typeof(byte[]) }) != null,
                "Texture2D.LoadRawTextureData(byte[]) is unavailable.");
            Require(typeof(Texture2D).GetMethod("Apply", new[] { typeof(bool), typeof(bool) }) != null,
                "Texture2D.Apply(bool,bool) is unavailable.");
            Require(typeof(RenderTexture).GetConstructor(new[] { typeof(int), typeof(int), typeof(int),
                        typeof(RenderTextureFormat), typeof(RenderTextureReadWrite) }) != null,
                "RenderTexture persistent ARGB32 constructor is unavailable.");
            Require(typeof(Graphics).GetMethods(BindingFlags.Public | BindingFlags.Static).Any(method => method.Name == "Blit" &&
                        method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(Texture) &&
                        method.GetParameters()[1].ParameterType == typeof(RenderTexture)),
                "Graphics.Blit(Texture,RenderTexture) is unavailable.");
            Require(typeof(Graphics).GetMethods(BindingFlags.Public | BindingFlags.Static).Any(method => method.Name == "Blit" &&
                        method.GetParameters().Length == 3 && method.GetParameters()[0].ParameterType == typeof(Texture) &&
                        method.GetParameters()[1].ParameterType == typeof(RenderTexture) &&
                        method.GetParameters()[2].ParameterType == typeof(Material)),
                "Graphics.Blit(Texture,RenderTexture,Material) is unavailable.");
            Require((int)TextureFormat.RGBA32 >= 0 && (int)RenderTextureFormat.ARGB32 >= 0,
                "Required Unity formats are unavailable.");
            Console.WriteLine("unitySurface rgba32Upload=1 argb32PersistentRT=1 blit2=1 blitMaterial=1");
        }

        private static void VerifyPatchAndFallbackSurface()
        {
            var assembly = typeof(SuperZSNESFramebufferPresentationPrototypePlugin).Assembly;
            var patches = assembly.GetType("SuperZSNESFramebufferPresentationPrototype.PresentationPatches", true);
            var generate = patches.GetMethod("GenerateBackgroundsPrefix", BindingFlags.Public | BindingFlags.Static);
            var render = patches.GetMethod("OnRenderImagePrefix", BindingFlags.Public | BindingFlags.Static);
            Require(generate != null && generate.ReturnType == typeof(bool) && generate.GetParameters().Length == 1,
                "GenerateBackgrounds bypass prefix ABI changed.");
            Require(render != null && render.ReturnType == typeof(bool) && render.GetParameters().Length == 2,
                "OnRenderImage substitution prefix ABI changed.");

            using (var plugin = AssemblyDefinition.ReadAssembly(assembly.Location))
            {
                var controller = plugin.MainModule.Types.Single(type => type.FullName ==
                    "SuperZSNESFramebufferPresentationPrototype.PresentationController");
                var validate = controller.Methods.Single(method => method.Name == "TryValidateRuntime");
                var strings = validate.Body.Instructions.Select(instruction => instruction.Operand as string)
                    .Where(value => value != null).ToArray();
                foreach (var required in new[] { "DKC_Widescreen_358x224", "mode7-start-unsupported",
                             "mode7-scanline-unsupported", "ui-fade-requires-stock-composite" })
                    Require(strings.Contains(required), "Missing fail-closed guard: " + required);

                var surface = plugin.MainModule.Types.Single(type => type.FullName ==
                    "SuperZSNESFramebufferPresentationPrototype.PersistentFrameSurface");
                var upload = surface.Methods.Single(method => method.Name == "Upload");
                var calls = upload.Body.Instructions.Select(instruction => instruction.Operand as MethodReference)
                    .Where(method => method != null).Select(method => method.FullName).ToArray();
                Require(calls.Any(call => call.Contains("Texture2D::LoadRawTextureData")) &&
                        calls.Any(call => call.Contains("Texture2D::Apply")) &&
                        calls.Any(call => call.Contains("UnityEngine.Graphics::Blit")),
                    "Compiled persistent upload path is incomplete.");
            }
            Console.WriteLine("fallback dkcOnly=1 mode7=2 uiFade=1 stockPrefixReturn=1 persistentUploadIL=1");
        }

        private static void VerifyIndexedExpansion()
        {
            var surface = typeof(SuperZSNESFramebufferPresentationPrototypePlugin).Assembly.GetType(
                "SuperZSNESFramebufferPresentationPrototype.PersistentFrameSurface", true);
            var expand = surface.GetMethod("ExpandIndexed", BindingFlags.Static | BindingFlags.NonPublic);
            var indices = new byte[] { 1, 2, 3, 4 }; // top row, then bottom row
            var palette = new Color32[256];
            palette[1] = new Color32(10, 11, 12, 13);
            palette[2] = new Color32(20, 21, 22, 23);
            palette[3] = new Color32(30, 31, 32, 33);
            palette[4] = new Color32(40, 41, 42, 43);
            var rgba = new byte[16];
            expand.Invoke(null, new object[] { indices, palette, 2, 2, true, rgba });
            Require(rgba.Take(4).SequenceEqual(new byte[] { 30, 31, 32, 33 }),
                "Top-down source was not flipped into Texture2D bottom row.");
            Require(rgba.Skip(12).Take(4).SequenceEqual(new byte[] { 20, 21, 22, 23 }),
                "Top row RGBA expansion is incorrect.");
            Array.Clear(rgba, 0, rgba.Length);
            expand.Invoke(null, new object[] { indices, palette, 2, 2, false, rgba });
            Require(rgba.Take(4).SequenceEqual(new byte[] { 10, 11, 12, 13 }),
                "Bottom-up source was unexpectedly flipped.");
            Console.WriteLine("indexedExpansion rgbaExact=1 topDownFlip=1 bottomUp=1");
        }

        private static void VerifyProviderRegistration()
        {
            var first = new StubSource();
            var second = new StubSource();
            Require(FramebufferPresentationApi.Register(first), "First provider registration failed.");
            Require(FramebufferPresentationApi.Register(first), "Idempotent provider registration failed.");
            Require(!FramebufferPresentationApi.Register(second), "Second provider replaced the active provider.");
            FramebufferPresentationApi.Unregister(second);
            Require(!FramebufferPresentationApi.Register(second), "Wrong-provider unregister cleared the active provider.");
            FramebufferPresentationApi.Unregister(first);
            Require(FramebufferPresentationApi.Register(second), "Provider did not unregister cleanly.");
            FramebufferPresentationApi.Unregister(second);
            Console.WriteLine("providerRegistration singleOwner=1 idempotent=1 guardedUnregister=1");
        }

        private sealed class StubSource : IIndexedFramebufferSource
        {
            public bool TryRenderFrame(IndexedFramebufferRequest request, IndexedFramebuffer framebuffer,
                out bool rowsAreTopDown, out string rejectionReason)
            {
                rowsAreTopDown = true;
                rejectionReason = "test";
                return false;
            }
        }

        private static Assembly Resolve(string displayName, params string[] directories)
        {
            var name = new AssemblyName(displayName).Name + ".dll";
            foreach (var directory in directories)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) return Assembly.Load(File.ReadAllBytes(path));
            }
            return null;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
