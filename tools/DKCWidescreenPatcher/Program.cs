using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DkcWidescreenPatcher
{
    internal static class Program
    {
        internal const string ExpectedSourceMd5 = "30C5F292FF4CBBFCC00FD8FA96C2DE3B";

        internal static readonly PatchVariant[] Variants =
        {
            new PatchVariant("standard", "Widescreen 358x224 (recommended)",
                "The complete widescreen gameplay patch with original SPC music.",
                "DKC_Widescreen_358x224.sfc",
                "B4AB46098E48218E70B5349E09E7FE71E344D23E3568F46E956B44C670006D6D"),
            new PatchVariant("msu1-deluxe", "Widescreen + Deluxe MSU-1 hooks",
                "For the 60-track DKC Deluxe MSU-1 pack. Audio is not included.",
                "DKC_Widescreen_358x224_MSU1_Deluxe.sfc",
                "FD2950B3AAE287E24F8D8B665AFBC3BE0EC3EEC07AA19DE055427DF76BD46AF5"),
            new PatchVariant("msu1-restoration", "Widescreen + Restoration MSU-1 hooks",
                "For traditional 27-track DKC restoration packs. Audio is not included.",
                "DKC_Widescreen_358x224_MSU1_Restoration.sfc",
                "4484CB5374F3C04E9F8DA1880C21D85D0C0403286CFABB65639BAD7CFC55A5A5")
        };

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                Environment.ExitCode = RunCommand(args);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static int RunCommand(string[] args)
        {
            try
            {
                if (args.Length >= 4 && args[0] == "--create-bps")
                {
                    byte[] source = File.ReadAllBytes(args[1]);
                    byte[] target = File.ReadAllBytes(args[2]);
                    string metadata = args.Length >= 5 ? args[4] : "DKC Widescreen 358x224";
                    File.WriteAllBytes(args[3], BpsPatch.Create(source, target, metadata));
                    return 0;
                }
                if (args.Length == 4 && args[0] == "--apply-bps")
                {
                    File.WriteAllBytes(args[3], BpsPatch.Apply(
                        File.ReadAllBytes(args[2]), File.ReadAllBytes(args[1])));
                    return 0;
                }
                if (args.Length == 3 && args[0] == "--verify-embedded")
                {
                    Directory.CreateDirectory(args[2]);
                    foreach (PatchVariant variant in Variants)
                        PatchRom(args[1], Path.Combine(args[2], variant.OutputName), variant);
                    return 0;
                }
                return 2;
            }
            catch (Exception exception)
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "patcher-error.txt"), exception.ToString()); }
                catch { }
                return 1;
            }
        }

        internal static void PatchRom(string sourcePath, string outputPath, PatchVariant variant)
        {
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a different output file; the patcher never overwrites your original ROM.");

            byte[] source = File.ReadAllBytes(sourcePath);
            string sourceMd5 = Hash(source, MD5.Create());
            if (!string.Equals(sourceMd5, ExpectedSourceMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "This ROM is not the supported headerless Donkey Kong Country USA v1.0 dump.\r\n\r\n" +
                    "Expected MD5: " + ExpectedSourceMd5 + "\r\n" +
                    "Selected MD5: " + sourceMd5);

            byte[] patch = LoadPatch(variant.Id);
            byte[] target = BpsPatch.Apply(source, patch);
            string targetSha = Hash(target, SHA256.Create());
            if (!string.Equals(targetSha, variant.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The generated ROM failed its final SHA-256 verification.");

            string fullOutput = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(fullOutput);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = fullOutput + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, target);
                if (File.Exists(fullOutput)) File.Delete(fullOutput);
                File.Move(temporary, fullOutput);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static byte[] LoadPatch(string id)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string suffix = "." + id + ".bps";
            string resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (resourceName != null)
            {
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                using (var memory = new MemoryStream())
                {
                    if (stream == null) throw new InvalidDataException("Embedded patch resource is unavailable.");
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            }

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDirectory, id + ".bps"),
                Path.Combine(baseDirectory, "patches", id + ".bps")
            };
            string adjacent = candidates.FirstOrDefault(File.Exists);
            if (adjacent == null)
                throw new FileNotFoundException("Patch data is missing. Download the complete release package.");
            return File.ReadAllBytes(adjacent);
        }

        private static string Hash(byte[] data, HashAlgorithm algorithm)
        {
            using (algorithm)
                return BitConverter.ToString(algorithm.ComputeHash(data)).Replace("-", string.Empty);
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly TextBox _source = new TextBox();
        private readonly TextBox _output = new TextBox();
        private readonly ComboBox _variant = new ComboBox();
        private readonly Label _description = new Label();
        private readonly Button _patch = new Button();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Label _status = new Label();

        internal MainForm()
        {
            Text = "DKC Widescreen 358x224 Patcher";
            ClientSize = new Size(680, 410);
            MinimumSize = new Size(696, 449);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(245, 247, 249);
            AllowDrop = true;

            var title = new Label
            {
                Text = "Donkey Kong Country — 358x224 widescreen",
                Font = new Font("Segoe UI Semibold", 17F),
                ForeColor = Color.FromArgb(27, 45, 61),
                AutoSize = true,
                Location = new Point(28, 24)
            };
            var subtitle = new Label
            {
                Text = "Select your clean, headerless DKC USA v1.0 ROM. Your original is never modified.",
                ForeColor = Color.FromArgb(73, 87, 99),
                AutoSize = true,
                Location = new Point(31, 65)
            };

            AddFileRow("Clean ROM", _source, 105, BrowseSource);
            AddFileRow("Output ROM", _output, 168, BrowseOutput);

            var variantLabel = FieldLabel("Patch type", 230);
            _variant.SetBounds(150, 225, 493, 28);
            _variant.DropDownStyle = ComboBoxStyle.DropDownList;
            _variant.Items.AddRange(Program.Variants.Cast<object>().ToArray());
            _variant.SelectedIndex = 0;
            _variant.SelectedIndexChanged += (sender, args) =>
            {
                UpdateDescription();
                SuggestOutput();
            };

            _description.SetBounds(150, 260, 493, 40);
            _description.ForeColor = Color.FromArgb(73, 87, 99);

            _patch.Text = "Create widescreen ROM";
            _patch.SetBounds(150, 314, 220, 40);
            _patch.BackColor = Color.FromArgb(25, 113, 194);
            _patch.ForeColor = Color.White;
            _patch.FlatStyle = FlatStyle.Flat;
            _patch.FlatAppearance.BorderSize = 0;
            _patch.Font = new Font("Segoe UI Semibold", 10F);
            _patch.Click += async (sender, args) => await ApplyPatchAsync();

            _progress.SetBounds(385, 314, 258, 12);
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.Visible = false;
            _status.SetBounds(385, 335, 258, 42);
            _status.ForeColor = Color.FromArgb(73, 87, 99);

            Controls.AddRange(new Control[]
            {
                title, subtitle, variantLabel, _variant, _description, _patch, _progress, _status
            });
            UpdateDescription();

            DragEnter += (sender, args) =>
            {
                if (args.Data.GetDataPresent(DataFormats.FileDrop)) args.Effect = DragDropEffects.Copy;
            };
            DragDrop += (sender, args) =>
            {
                string[] files = (string[])args.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    _source.Text = files[0];
                    SuggestOutput(true);
                }
            };
        }

        private Label FieldLabel(string text, int top)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = Color.FromArgb(27, 45, 61),
                Location = new Point(31, top + 5)
            };
            Controls.Add(label);
            return label;
        }

        private void AddFileRow(string label, TextBox box, int top, EventHandler browse)
        {
            FieldLabel(label, top);
            box.SetBounds(150, top, 405, 27);
            box.TextChanged += (sender, args) =>
            {
                if (box == _source && !_output.Focused) SuggestOutput();
            };
            var button = new Button
            {
                Text = "Browse…",
                Location = new Point(565, top - 1),
                Size = new Size(78, 29),
                FlatStyle = FlatStyle.System
            };
            button.Click += browse;
            Controls.Add(box);
            Controls.Add(button);
        }

        private void BrowseSource(object sender, EventArgs args)
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Select clean Donkey Kong Country USA v1.0 ROM",
                Filter = "SNES ROM (*.sfc;*.smc)|*.sfc;*.smc|All files (*.*)|*.*",
                CheckFileExists = true
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _source.Text = dialog.FileName;
                    SuggestOutput(true);
                }
            }
        }

        private void BrowseOutput(object sender, EventArgs args)
        {
            PatchVariant selected = (PatchVariant)_variant.SelectedItem;
            using (var dialog = new SaveFileDialog
            {
                Title = "Save patched ROM",
                Filter = "SNES ROM (*.sfc)|*.sfc|All files (*.*)|*.*",
                FileName = selected.OutputName,
                OverwritePrompt = true
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) _output.Text = dialog.FileName;
            }
        }

        private void UpdateDescription()
        {
            if (_variant.SelectedItem is PatchVariant selected)
                _description.Text = selected.Description;
        }

        private void SuggestOutput(bool force = false)
        {
            if (!(_variant.SelectedItem is PatchVariant selected) || string.IsNullOrWhiteSpace(_source.Text))
                return;
            if (!force && _output.Focused) return;
            try
            {
                string directory = Path.GetDirectoryName(_source.Text);
                if (!string.IsNullOrEmpty(directory)) _output.Text = Path.Combine(directory, selected.OutputName);
            }
            catch (ArgumentException)
            {
                // Let the normal file validation show the actionable error after
                // the user finishes typing or chooses a path with Browse.
            }
        }

        private async Task ApplyPatchAsync()
        {
            string source = _source.Text.Trim();
            string output = _output.Text.Trim();
            PatchVariant selected = (PatchVariant)_variant.SelectedItem;
            if (!File.Exists(source))
            {
                MessageBox.Show(this, "Select your clean DKC ROM first.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(output))
            {
                MessageBox.Show(this, "Choose where to save the patched ROM.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (File.Exists(output) && MessageBox.Show(this,
                    "The output file already exists. Replace it?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            SetBusy(true, "Verifying and patching…");
            try
            {
                await Task.Run(() => Program.PatchRom(source, output, selected));
                SetBusy(false, "Done — ROM verified successfully.");
                DialogResult openFolder = MessageBox.Show(this,
                    "Your widescreen ROM is ready:\r\n\r\n" + output +
                    "\r\n\r\nOpen its folder?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (openFolder == DialogResult.Yes)
                    Process.Start("explorer.exe", "/select,\"" + output + "\"");
            }
            catch (Exception exception)
            {
                SetBusy(false, "Patch failed; your original ROM was not changed.");
                MessageBox.Show(this, exception.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SetBusy(bool busy, string status)
        {
            _source.Enabled = !busy;
            _output.Enabled = !busy;
            _variant.Enabled = !busy;
            _patch.Enabled = !busy;
            _progress.Visible = busy;
            _status.Text = status;
        }
    }
}
