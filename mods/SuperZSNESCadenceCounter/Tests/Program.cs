using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace SuperZSNESCadenceCounter.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var gamePath = args.Length == 0
                    ? Path.Combine(Environment.GetEnvironmentVariable("SUPERZSNES_MANAGED_DIR")
                        ?? throw new InvalidOperationException("Set SUPERZSNES_MANAGED_DIR before running the verifier."), "Assembly-CSharp.dll")
                    : Path.GetFullPath(args[0]);
                VerifyTargets(gamePath);
                VerifyNoSynchronizationPrimitives();
                Console.WriteLine("PASS cadence counter offline verification");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex);
                return 1;
            }
        }

        private static void VerifyTargets(string gamePath)
        {
            using (var game = AssemblyDefinition.ReadAssembly(gamePath))
            {
                var master = game.MainModule.Types.Single(type => type.Name == "MasterExecutor");
                var renderer = game.MainModule.Types.Single(type => type.Name == "PPURenderer");
                Require(master.Methods.Count(method => method.Name == "Update" && !method.HasParameters) == 1,
                    "Expected one MasterExecutor.Update target.");
                Require(master.Methods.Count(method => method.Name == "RunFrame" && !method.HasParameters) == 1,
                    "Expected one MasterExecutor.RunFrame target.");
                Require(renderer.Methods.Count(method => method.Name == "GenerateBackgrounds" && !method.HasParameters) == 1,
                    "Expected one PPURenderer.GenerateBackgrounds target.");
                Require(renderer.Methods.Count(method => method.Name == "GenerateBackground" && method.Parameters.Count == 2) == 1,
                    "Expected one PPURenderer.GenerateBackground(layer,data) target.");
            }
            Console.WriteLine("targets update=1 runFrame=1 generateBackgrounds=1 generateBackground=1");
        }

        private static void VerifyNoSynchronizationPrimitives()
        {
            var pluginPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SuperZSNESCadenceCounter.dll");
            using (var plugin = AssemblyDefinition.ReadAssembly(pluginPath))
            {
                var calls = plugin.MainModule.Types.SelectMany(type => type.Methods)
                    .Where(method => method.HasBody)
                    .SelectMany(method => method.Body.Instructions)
                    .Select(instruction => instruction.Operand as MethodReference)
                    .Where(method => method != null)
                    .ToList();
                Require(!calls.Any(method => method.DeclaringType.FullName == "System.Threading.Interlocked"),
                    "Plugin unexpectedly calls Interlocked.");
                Require(!calls.Any(method => method.DeclaringType.FullName == "System.Threading.Monitor"),
                    "Plugin unexpectedly calls Monitor.");
                Require(!plugin.MainModule.AssemblyReferences.Any(reference =>
                        reference.Name.StartsWith("System.Collections.Concurrent", StringComparison.Ordinal)),
                    "Plugin unexpectedly references concurrent collections.");
            }
            Console.WriteLine("synchronization Interlocked=0 Monitor=0 concurrentCollections=0");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
