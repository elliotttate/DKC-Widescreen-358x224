namespace DkcWidescreenPatcher
{
    internal sealed class PatchVariant
    {
        internal PatchVariant(string id, string name, string description,
            string outputName, string expectedSha256)
        {
            Id = id;
            Name = name;
            Description = description;
            OutputName = outputName;
            ExpectedSha256 = expectedSha256;
        }

        internal string Id { get; }
        internal string Name { get; }
        internal string Description { get; }
        internal string OutputName { get; }
        internal string ExpectedSha256 { get; }

        public override string ToString() => Name;
    }
}
