using System.Collections.Generic;

namespace SuperZSNESLayerDepthControllerIL2CPP
{
    internal sealed class LayerComponentProfile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, float> ComponentDepths { get; set; } =
            new Dictionary<string, float>();
        public ForegroundGroundSettings ForegroundGround { get; set; } =
            new ForegroundGroundSettings();
    }

    internal sealed class ForegroundGroundSettings
    {
        public bool Enabled { get; set; }
        public int Background { get; set; }
        public int CutY { get; set; } = 184;
        public float Depth { get; set; } = -4f;
        public float OffsetY { get; set; }
        public float SurfaceScaleX { get; set; } = 1.05f;
        public float SurfaceScaleY { get; set; } = 1f;
        public bool FollowGroundEdge { get; set; } = true;
        public int EdgeSearchRadius { get; set; } = 56;
    }
}
