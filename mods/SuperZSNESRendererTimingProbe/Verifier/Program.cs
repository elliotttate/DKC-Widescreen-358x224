using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

internal static class Program
{
    private static readonly string Managed = Environment.GetEnvironmentVariable("SUPERZSNES_MANAGED_DIR")
        ?? throw new InvalidOperationException("Set SUPERZSNES_MANAGED_DIR before running the verifier.");
    private static readonly string BepInExCore = Path.Combine(
        Environment.GetEnvironmentVariable("BEPINEX_ROOT")
            ?? throw new InvalidOperationException("Set BEPINEX_ROOT before running the verifier."),
        "BepInEx", "core");
    private static readonly string Plugin = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "Release", "net472",
        "SuperZSNESRendererTimingProbe.dll"));

    private static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        return Verify();
    }

    private static int Verify()
    {
        try
        {
            Assembly.LoadFrom(Path.Combine(Managed, "Assembly-CSharp.dll"));
            var assembly = Assembly.LoadFrom(Plugin);
            var layoutType = assembly.GetType("SuperZSNESRendererTimingProbe.RendererLayout", true);
            var resolve = layoutType.GetMethod("ResolveAndVerify", BindingFlags.Static | BindingFlags.NonPublic);
            var layout = resolve.Invoke(null, null);
            var getters = (MethodInfo[])layoutType.GetField("TextureGetters", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(layout);

            var gate = assembly.GetType("SuperZSNESRendererTimingProbe.DirtyUploadGate", true);
            var transpiler = gate.GetMethod("Transpiler", BindingFlags.Static | BindingFlags.Public);
            var countField = gate.GetField("TransformCount", BindingFlags.Static | BindingFlags.NonPublic);
            countField.SetValue(null, 0);
            foreach (var getter in getters)
            {
                var input = PatchProcessor.GetOriginalInstructions(getter);
                var output = (IEnumerable)transpiler.Invoke(null, new object[] { input, getter });
                var count = 0;
                foreach (var ignored in output) count++;
                if (count == 0) throw new InvalidOperationException(getter.Name + " transpiler emitted no instructions.");
                Console.WriteLine(getter.Name + ": original=" + input.Count + " transformed=" + count);
            }
            var transforms = (int)countField.GetValue(null);
            if (transforms != 3) throw new InvalidOperationException("Expected 3 transforms, got " + transforms + ".");
            Console.WriteLine("RendererTimingProbe exact v0.230 IL verification: PASS (3/3)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + (ex.InnerException ?? ex));
            return 1;
        }
    }

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name + ".dll";
        foreach (var directory in new[] { Managed, BepInExCore, Path.GetDirectoryName(Plugin) })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
}
