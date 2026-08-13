using System;
using System.Collections.Generic;
using System.Linq;
using SuperZSNESSpriteDepthStudio;

internal static class Program
{
    private static int Main()
    {
        try
        {
            byte[] vram=new byte[65536],oam=new byte[544],cgram=new byte[512],objsel=new byte[224];
            for(int row=0;row<8;row++)vram[row*2]=0x80;
            cgram[129*2]=0x1F;
            oam[0]=10;oam[1]=19;oam[2]=0;oam[3]=0;
            List<SpriteRecord> sprites=SpriteDecoder.Decode(vram,oam,cgram,objsel);
            SpriteRecord first=sprites[0];
            Require(sprites.Count==128,"all 128 OAM entries decoded");
            Require(first.X==10&&first.Y==20&&first.Width==8&&first.Height==8,"small sprite metadata");
            Require(first.OpaquePixels==8&&first.Pixels[0]==0xFFFF0000u,"4bpp pixels and BGR555 palette decoded");
            byte[] inactive=new byte[224];
            Require(!SpriteDecoder.Decode(vram,oam,cgram,objsel,inactive)[0].IntersectsScreen,
                "disabled OBJ scanlines hide dormant OAM by default");
            inactive[20]=1;
            Require(SpriteDecoder.Decode(vram,oam,cgram,objsel,inactive)[0].IntersectsScreen,
                "enabled OBJ scanline exposes the rendered sprite");
            first.IntersectsScreen=true;
            var adjacent=new SpriteRecord{Slot=1,X=12,Y=20,Width=8,Height=8,OpaquePixels=1,
                IntersectsScreen=true,Pixels=new uint[64]};adjacent.Pixels[0]=0xFFFFFFFFu;
            var separate=new SpriteRecord{Slot=2,X=40,Y=20,Width=8,Height=8,OpaquePixels=1,
                IntersectsScreen=true,Pixels=new uint[64]};separate.Pixels[0]=0xFFFFFFFFu;
            List<SpriteGroupRecord> groups=SpriteGroupBuilder.Build(new[]{first,adjacent,separate},2);
            Require(groups.Count==2&&groups.Any(g=>g.Members.Count==2),"touching OAM parts form one logical sprite");
            adjacent.Palette=1;
            Require(SpriteGroupBuilder.Build(new[]{first,adjacent},2).Count==2,
                "touching parts with different sprite palettes remain separate authoring objects");
            oam[512]=2;
            first=SpriteDecoder.ReadMetadata(0,oam,objsel);
            Require(first.Large&&first.Width==16&&first.Height==16,"large OBJ size decoded");

            var profile=new SpriteDepthProfile();
            SpriteDepthRules.Set(profile,first,false,-3);
            Require(SpriteDepthRules.Resolve(profile,first)==-3,"slot rule resolves");
            SpriteRecord same=new SpriteRecord{Slot=7,Tile=first.Tile,Palette=first.Palette,Priority=first.Priority,
                NameSelect=first.NameSelect,Large=first.Large,SizeSelector=first.SizeSelector};
            Require(SpriteDepthRules.Resolve(profile,same)==0,"slot rule does not leak to other objects");
            SpriteDepthRules.Set(profile,first,true,4);
            Require(SpriteDepthRules.Resolve(profile,same)==4,"appearance rule covers matching sprites");
            SpriteDepthRules.Set(profile,first,true,0);
            Require(profile.Rules.Count==0,"zero layer removes authored rule");
            Require(SpriteDepthOrdering.RenderOrder(126,1)==3,
                "OAM priority rotation wraps deterministically");
            Near(SpriteDepthOrdering.CompressedOffset(0,64,0.001f),
                0.064f-0.5f,"OAM order compression preserves order without visible card spacing");
            Require(DkcSemanticNames.Actor(0x31)=="Swinging rope"&&
                DkcSemanticNames.Level(0x00)=="Jungle Hijinxs",
                "disassembly-derived actor and level names resolve");

            byte[] bgVram=new byte[65536],bgCgram=new byte[512],registers=new byte[64];
            registers[7]=0x21;
            bgVram[0x4000]=1;
            for(int row=0;row<8;row++)bgVram[32+row*2]=0xFF;
            bgCgram[2]=0x1F;
            var component=new BackgroundComponentInfo{Id="BG1-A4000-TEST",Background=0,
                TileCount=1,Depth=-0.08f,Addresses=new[]{0x4000}};
            List<BackgroundObjectRecord> scenery=BackgroundObjectDecoder.Decode(bgVram,bgCgram,
                registers,new[]{0,0,0},new[]{0,0,0},new BackgroundComponentReport
                {Level="0001",Components=new List<BackgroundComponentInfo>{component}});
            Require(scenery.Count==1&&scenery[0].X==0&&scenery[0].Y==0&&
                scenery[0].Width==8&&scenery[0].Height==8,"visible BG component is reconstructed and cropped");
            Require(scenery[0].OpaquePixels==64&&scenery[0].Pixels[0]==0xFFFF0000u,
                "BG planar pixels and palette are decoded");

            byte[] scale=SpriteDepthNativePatcher.BuildScaleStub(0x200000,0x100005,new IntPtr(0x300200));
            byte[] z=SpriteDepthNativePatcher.BuildZStub(0x200100,0x100005,new IntPtr(0x300000));
            Require(Contains(scale,new byte[]{0x50,0x8B,0x45,0x10,0xF3,0x0F,0x59,0x04,0x85}),"scale stub indexes OAM slot");
            Require(Contains(scale,SpriteDepthNativePatcher.ScaleHookBytes),"scale stub retains stock store");
            Require(Contains(z,new byte[]{0xF3,0x0F,0x58,0x4D,0x90,0x50,0x8B,0x45,0x10}),"Z stub retains stock add and indexes slot");
            Require(scale[^5]==0xE9&&z[^5]==0xE9,"both stubs return through rel32 jump");
            Console.WriteLine("PASS: sprite decode/grouping/rules, visible BG scenery reconstruction, and native per-slot stubs.");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine("FAIL: "+ex.Message);return 1;}
    }
    private static void Require(bool value,string name){if(!value)throw new InvalidOperationException(name);}
    private static void Near(float actual,float expected,string name){if(Math.Abs(actual-expected)>0.0001f)throw new InvalidOperationException(name+": expected "+expected+", got "+actual);}
    private static bool Contains(byte[] h,byte[] n){for(int i=0;i<=h.Length-n.Length;i++){int j=0;for(;j<n.Length;j++)if(h[i+j]!=n[j])break;if(j==n.Length)return true;}return false;}
}
