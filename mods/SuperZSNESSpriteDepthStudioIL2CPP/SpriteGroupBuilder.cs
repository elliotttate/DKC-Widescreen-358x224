using System;
using System.Collections.Generic;
using System.Linq;

namespace SuperZSNESSpriteDepthStudio
{
    public sealed class SpriteGroupRecord
    {
        public int GroupIndex { get; set; }
        public List<SpriteRecord> Members { get; set; } = new List<SpriteRecord>();
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public uint[] Pixels { get; set; } = Array.Empty<uint>();
        public int OpaquePixels { get; set; }
        public string Identity => "Sprite " + (GroupIndex + 1).ToString("00") +
            "  •  " + Members.Count + (Members.Count == 1 ? " OAM part" : " OAM parts");
    }

    public static class SpriteGroupBuilder
    {
        public static List<SpriteGroupRecord> Build(IEnumerable<SpriteRecord> source,
            int joinGap = 2)
        {
            List<SpriteRecord> sprites = source?.Where(s => s != null &&
                s.IntersectsScreen && s.OpaquePixels > 0).OrderBy(s => s.Slot).ToList() ??
                new List<SpriteRecord>();
            int[] parent = Enumerable.Range(0, sprites.Count).ToArray();
            var occupied = new Dictionary<long,List<int>>();
            for (int i=0;i<sprites.Count;i++)
            {
                SpriteRecord sprite=sprites[i];
                for(int py=0;py<sprite.Height;py++)for(int px=0;px<sprite.Width;px++)
                {
                    if((sprite.Pixels[py*sprite.Width+px]>>24)==0)continue;
                    int worldX=sprite.X+px,worldY=sprite.Y+py;
                    for(int dy=-joinGap;dy<=joinGap;dy++)for(int dx=-joinGap;dx<=joinGap;dx++)
                        if(occupied.TryGetValue(Key(worldX+dx,worldY+dy),out List<int> others))
                            foreach(int other in others)
                                if(Compatible(sprite,sprites[other]))Union(parent,i,other);
                    long key=Key(worldX,worldY);
                    if(!occupied.TryGetValue(key,out List<int> owners))occupied[key]=owners=new List<int>();
                    owners.Add(i);
                }
            }
            var buckets = new Dictionary<int,List<SpriteRecord>>();
            for(int i=0;i<sprites.Count;i++)
            {
                int root=Find(parent,i);
                if(!buckets.TryGetValue(root,out List<SpriteRecord> bucket))
                    buckets[root]=bucket=new List<SpriteRecord>();
                bucket.Add(sprites[i]);
            }
            List<SpriteGroupRecord> groups=buckets.Values.Select(BuildOne)
                .OrderBy(g=>g.Y).ThenBy(g=>g.X).ToList();
            for(int i=0;i<groups.Count;i++)groups[i].GroupIndex=i;
            return groups;
        }

        private static long Key(int x,int y)=>((long)y<<32)|(uint)x;
        private static bool Compatible(SpriteRecord a,SpriteRecord b)=>
            a.Palette==b.Palette&&a.Priority==b.Priority&&a.NameSelect==b.NameSelect;

        private static SpriteGroupRecord BuildOne(List<SpriteRecord> members)
        {
            int x=members.Min(s=>s.X),y=members.Min(s=>s.Y);
            int x2=members.Max(s=>s.X+s.Width),y2=members.Max(s=>s.Y+s.Height);
            int width=Math.Max(1,x2-x),height=Math.Max(1,y2-y);
            uint[] pixels=new uint[width*height];int opaque=0;
            foreach(SpriteRecord sprite in members.OrderByDescending(s=>s.Slot))
            for(int sy=0;sy<sprite.Height;sy++)for(int sx=0;sx<sprite.Width;sx++)
            {
                uint color=sprite.Pixels[sy*sprite.Width+sx];if((color>>24)==0)continue;
                int dx=sprite.X-x+sx,dy=sprite.Y-y+sy;
                if(dx<0||dy<0||dx>=width||dy>=height)continue;
                int index=dy*width+dx;if((pixels[index]>>24)==0)opaque++;pixels[index]=color;
            }
            return new SpriteGroupRecord{Members=members,X=x,Y=y,Width=width,Height=height,Pixels=pixels,OpaquePixels=opaque};
        }

        private static int Find(int[] p,int i){while(p[i]!=i){p[i]=p[p[i]];i=p[i];}return i;}
        private static void Union(int[] p,int a,int b){a=Find(p,a);b=Find(p,b);if(a!=b)p[b]=a;}
    }
}
