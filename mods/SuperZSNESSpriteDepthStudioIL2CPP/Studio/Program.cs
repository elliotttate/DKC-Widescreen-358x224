using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SuperZSNESSpriteDepthStudio
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string root = FindRoot(args);
            using var mutex = new Mutex(true, "SuperZSNES-SpriteDepthStudio-" +
                root.ToLowerInvariant().GetHashCode().ToString("X8"), out bool first);
            if (!first)
            {
                MessageBox.Show("Object Depth Studio is already open for this emulator.",
                    "Object Depth Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.Run(new StudioForm(root));
        }

        private static string FindRoot(string[] args)
        {
            for (int i=0;i<args.Length-1;i++)
                if (string.Equals(args[i], "--root", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(args[i+1]);
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            if (string.Equals(directory.Name, "Studio", StringComparison.OrdinalIgnoreCase) && directory.Parent != null)
                return directory.Parent.FullName;
            return directory.FullName;
        }
    }
}
