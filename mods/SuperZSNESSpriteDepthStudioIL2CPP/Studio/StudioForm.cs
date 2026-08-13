using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SuperZSNESSpriteDepthStudio
{
    internal sealed class StudioForm : Form
    {
        private readonly string _root, _exchange;
        private readonly FlowLayoutPanel _cards;
        private readonly Label _status, _captureInfo;
        private readonly CheckBox _showAll;
        private readonly CheckBox _individual;
        private readonly RadioButton _allObjects, _spritesOnly, _backgroundOnly;
        private readonly TextBox _search;
        private readonly Timer _poll;
        private readonly float _uiScale;
        private DateTime _snapshotWrite;
        private SpriteCaptureManifest _manifest;
        private SpriteDepthProfile _profile;
        private BackgroundDepthProfile _backgroundProfile = new BackgroundDepthProfile();
        private List<SpriteRecord> _sprites = new List<SpriteRecord>();
        private List<BackgroundObjectRecord> _backgrounds = new List<BackgroundObjectRecord>();
        private readonly List<Bitmap> _bitmaps = new List<Bitmap>();

        internal StudioForm(string root)
        {
            _root=Path.GetFullPath(root); _exchange=Path.Combine(_root,"Exchange");
            Directory.CreateDirectory(_exchange);
            AutoScaleMode=AutoScaleMode.None;
            using(Graphics desktop=Graphics.FromHwnd(IntPtr.Zero))
                _uiScale=Math.Max(1f,desktop.DpiX/96f);
            int S(int value)=>Math.Max(1,(int)Math.Round(value*_uiScale));
            Text="SuperZSNES Object Depth Studio"; MinimumSize=new Size(820,600);
            Size=new Size(S(1280),S(900)); MinimumSize=new Size(S(820),S(600)); StartPosition=FormStartPosition.CenterScreen;
            BackColor=Color.FromArgb(24,27,32); ForeColor=Color.White;
            var toolbar=new Panel{Dock=DockStyle.Top,Height=S(145),BackColor=Color.FromArgb(31,35,42),Padding=new Padding(S(12))};
            var capture=new Button{Text="Capture current frame  (F10)",Location=new Point(S(12),S(12)),Size=new Size(S(210),S(40))};
            var refresh=new Button{Text="Reload",Location=new Point(S(230),S(12)),Size=new Size(S(88),S(40))};
            var folder=new Button{Text="Open files",Location=new Point(S(326),S(12)),Size=new Size(S(96),S(40))};
            _allObjects=new RadioButton{Text="All objects",Checked=true,Location=new Point(S(446),S(23)),AutoSize=true,ForeColor=Color.White};
            _spritesOnly=new RadioButton{Text="Sprites",Location=new Point(S(550),S(23)),AutoSize=true,ForeColor=Color.White};
            _backgroundOnly=new RadioButton{Text="Background scenery",Location=new Point(S(630),S(23)),AutoSize=true,ForeColor=Color.White};
            _individual=new CheckBox{Text="Individual OAM parts",Location=new Point(S(12),S(76)),AutoSize=true,ForeColor=Color.Gainsboro};
            _showAll=new CheckBox{Text="Show all 128 slots",Location=new Point(S(185),S(76)),AutoSize=true,ForeColor=Color.Gainsboro,Enabled=false};
            _search=new TextBox{PlaceholderText="Filter sprite, scenery, BG, slot, or tile…",Location=new Point(S(365),S(69)),Width=S(300)};
            _captureInfo=new Label{Text="No object capture yet.",Location=new Point(S(12),S(116)),AutoSize=true,ForeColor=Color.FromArgb(172,190,218)};
            _status=new Label{Text="Ready",Dock=DockStyle.Bottom,Height=S(28),Padding=new Padding(S(10),S(5),0,0),BackColor=Color.FromArgb(31,35,42),ForeColor=Color.Gainsboro};
            toolbar.Controls.AddRange(new Control[]{capture,refresh,folder,_allObjects,_spritesOnly,
                _backgroundOnly,_individual,_showAll,_search,_captureInfo});
            _cards=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,WrapContents=true,Padding=new Padding(S(8)),BackColor=BackColor};
            Controls.Add(_cards); Controls.Add(toolbar); Controls.Add(_status);
            capture.Click+=(_,__)=>RequestCapture(); refresh.Click+=(_,__)=>LoadSnapshot(true);
            folder.Click+=(_,__)=>Process.Start(new ProcessStartInfo("explorer.exe","\""+_root+"\""){UseShellExecute=true});
            _individual.CheckedChanged+=(_,__)=>{_showAll.Enabled=_individual.Checked;RebuildCards();};
            _showAll.CheckedChanged+=(_,__)=>RebuildCards(); _search.TextChanged+=(_,__)=>RebuildCards();
            _allObjects.CheckedChanged+=(_,__)=>RebuildCards();
            _spritesOnly.CheckedChanged+=(_,__)=>RebuildCards();
            _backgroundOnly.CheckedChanged+=(_,__)=>RebuildCards();
            _poll=new Timer{Interval=500}; _poll.Tick+=(_,__)=>LoadSnapshot(false); _poll.Start();
            FormClosed+=(_,__)=>DisposeBitmaps();
            Shown+=(_,__)=>{LoadSnapshot(true);if(_manifest==null)RequestCapture();};
        }

        private void RequestCapture()
        {
            try{File.WriteAllText(Path.Combine(_exchange,"capture.request"),DateTime.UtcNow.ToString("O"));_status.Text="Capture requested; waiting for the emulator’s next Update…";}
            catch(Exception ex){ShowError("Could not request capture",ex);}
        }

        private void LoadSnapshot(bool force)
        {
            string path=Path.Combine(_exchange,"snapshot.json");
            if(!File.Exists(path))return;
            DateTime write=File.GetLastWriteTimeUtc(path);
            if(!force&&write<=_snapshotWrite)return;
            try
            {
                SpriteCaptureManifest manifest=SpriteDepthFiles.ReadJson<SpriteCaptureManifest>(path);
                if(manifest==null)throw new InvalidDataException("snapshot.json is empty.");
                byte[] oam=ReadExact(manifest.OamFile,544),vram=ReadExact(manifest.VramFile,65536),
                    cgram=ReadCgram(manifest.CgramFile),objsel=ReadExact(manifest.ObjSelFile,224);
                string activePath=Path.Combine(_exchange,manifest.ObjActiveFile??string.Empty);
                byte[] objactive=File.Exists(activePath)?ReadExact(manifest.ObjActiveFile,224):null;
                List<SpriteRecord> decoded=SpriteDecoder.Decode(vram,oam,cgram,objsel,objactive);
                List<BackgroundObjectRecord> backgrounds=new List<BackgroundObjectRecord>();
                if(!string.IsNullOrEmpty(manifest.ComponentReportFile))
                {
                    string componentPath=Path.Combine(_exchange,manifest.ComponentReportFile);
                    string registersName=string.IsNullOrEmpty(manifest.RegistersFile)?
                        "snapshot-registers.bin":manifest.RegistersFile;
                    if(File.Exists(componentPath))
                    {
                        BackgroundComponentReport report=
                            SpriteDepthFiles.ReadJson<BackgroundComponentReport>(componentPath);
                        byte[] registers=ReadExact(registersName,64);
                        backgrounds=BackgroundObjectDecoder.Decode(vram,cgram,registers,
                            manifest.BackgroundScrollX,manifest.BackgroundScrollY,report);
                    }
                }
                _manifest=manifest;_sprites=decoded;_backgrounds=backgrounds;_snapshotWrite=write;
                _profile=SpriteDepthFiles.ReadJson<SpriteDepthProfile>(manifest.ProfileFile)??new SpriteDepthProfile
                {RomFileName=manifest.RomFileName,RomSha256=manifest.RomSha256};
                _backgroundProfile=SpriteDepthFiles.ReadJson<BackgroundDepthProfile>(
                    manifest.ComponentProfileFile)??new BackgroundDepthProfile();
                int groups=SpriteGroupBuilder.Build(decoded).Count;
                string levelName=string.IsNullOrWhiteSpace(manifest.LevelName)?
                    ("level $"+manifest.Level):manifest.LevelName+" ($"+manifest.Level+")";
                _captureInfo.Text=manifest.RomFileName+"  •  "+levelName+"  •  "+groups+
                    " logical sprites / "+manifest.ActiveSpriteCount+
                    " visible OAM parts  •  "+backgrounds.Count+" visible scenery objects  •  "+
                    LocalTime(manifest.CapturedUtc)+
                    (manifest.MidFrameOamWrites>0?"  •  ⚠ mid-frame OAM writes":"");
                RebuildCards();
                _status.Text="Snapshot loaded. Change a depth value; the emulator hot-reloads the profile automatically.";
            }
            catch(Exception ex){ShowError("Could not load the object snapshot",ex);}
        }

        private void RebuildCards()
        {
            if(_sprites==null)return;
            _cards.SuspendLayout(); _cards.Controls.Clear(); DisposeBitmaps();
            string filter=(_search.Text??string.Empty).Trim().ToLowerInvariant();
            var selected=new List<StudioSprite>();
            if(!_backgroundOnly.Checked)
                selected.AddRange(_individual.Checked?BuildIndividualViews():BuildGroupViews());
            if(!_spritesOnly.Checked)
                selected.AddRange(BuildBackgroundViews());
            if(filter.Length>0)selected=selected.Where(s=>s.Identity.ToLowerInvariant().Contains(filter)||
                (s.Detail??string.Empty).ToLowerInvariant().Contains(filter)||
                s.Members.Any(m=>m.Slot.ToString().Contains(filter)||("$"+m.Tile.ToString("X2")).ToLowerInvariant().Contains(filter))).ToList();
            foreach(StudioSprite sprite in selected)
            {
                Bitmap bitmap=CreateBitmap(sprite);_bitmaps.Add(bitmap);
                int depth;bool secondary;bool automatic=false;
                if(sprite.BackgroundObject!=null)
                {
                    float step=GetBackgroundDepthStep();
                    float value=0f;
                    bool has=_backgroundProfile?.ComponentDepths!=null&&
                        _backgroundProfile.ComponentDepths.TryGetValue(
                            sprite.BackgroundObject.Id,out value);
                    float effective=has?value:sprite.BackgroundObject.AutomaticDepth;
                    depth=Math.Max(-12,Math.Min(12,(int)Math.Round(effective/step)));
                    secondary=automatic=!has;
                }
                else
                {
                    int[] depths=sprite.Members.Select(m=>
                        SpriteDepthRules.Resolve(_profile,m)).Distinct().ToArray();
                    depth=depths.Length==1?depths[0]:0;
                    secondary=sprite.Members.Count>0&&sprite.Members.All(m=>
                        SpriteDepthRules.IsAppearanceRule(_profile,m));
                }
                var card=new SpriteCard(sprite,bitmap,depth,secondary,_uiScale,automatic);
                card.RuleChanged+=CardRuleChanged;_cards.Controls.Add(card);
            }
            _cards.ResumeLayout();
        }

        private void CardRuleChanged(SpriteCard card)
        {
            try
            {
                if(card.Sprite.BackgroundObject!=null)
                {
                    _backgroundProfile??=new BackgroundDepthProfile();
                    _backgroundProfile.ComponentDepths??=new Dictionary<string,float>();
                    string id=card.Sprite.BackgroundObject.Id;
                    if(card.Automatic)_backgroundProfile.ComponentDepths.Remove(id);
                    else _backgroundProfile.ComponentDepths[id]=
                        card.Depth*GetBackgroundDepthStep();
                    SpriteDepthFiles.WriteJsonAtomic(_manifest.ComponentProfileFile,
                        _backgroundProfile);
                    _status.Text=card.Sprite.Identity+" → "+
                        (card.Automatic?"automatic depth":"layer "+card.Depth+" ("+
                         (card.Depth*GetBackgroundDepthStep()).ToString("0.00")+")")+
                        ". The scenery mapper will hot-reload it.";
                    return;
                }
                foreach(SpriteRecord member in card.Sprite.Members)
                    SpriteDepthRules.Set(_profile,member,card.AllMatching,card.Depth);
                SpriteDepthFiles.WriteJsonAtomic(_manifest.ProfileFile,_profile);
                _status.Text=card.Sprite.Identity+" → layer "+card.Depth+
                    (card.AllMatching?" (all matching appearances)":" (this OAM slot)")+". Saved and waiting for hot reload.";
            }
            catch(Exception ex){ShowError("Could not save the sprite depth profile",ex);}
        }

        private byte[] ReadExact(string name,int size)
        {
            byte[] value=File.ReadAllBytes(Path.Combine(_exchange,name));
            if(value.Length!=size)throw new InvalidDataException(name+" should be "+size+" bytes, got "+value.Length+".");
            return value;
        }

        private byte[] ReadCgram(string name)
        {
            byte[] value=File.ReadAllBytes(Path.Combine(_exchange,name));
            if(value.Length!=512&&value.Length!=224*512)
                throw new InvalidDataException(name+" should be 512 or 114688 bytes, got "+value.Length+".");
            return value;
        }

        private List<StudioSprite> BuildIndividualViews()
        {
            return _sprites.Where(s=>_showAll.Checked||s.IntersectsScreen).Select(s=>new StudioSprite
            {Identity=s.Identity,Detail=(s.IntersectsScreen?"visible":"off-screen")+"   pal "+s.Palette+"   pri "+s.Priority,
             X=s.X,Y=s.Y,Width=s.Width,Height=s.Height,OpaquePixels=s.OpaquePixels,IntersectsScreen=s.IntersectsScreen,
             Pixels=s.Pixels,Members=new List<SpriteRecord>{s}}).ToList();
        }

        private List<StudioSprite> BuildGroupViews()
        {
            var result=new List<StudioSprite>();
            var actors=new List<GameActorRecord>(_manifest?.Actors??new List<GameActorRecord>());
            List<SpriteGroupRecord> groups=SpriteGroupBuilder.Build(_sprites);
            GameActorRecord hero=actors.FirstOrDefault(a=>(a.SpriteId==1||a.SpriteId==2)&&
                (a.CurrentPose!=0||a.DisplayedPose!=0));
            SpriteGroupRecord heroGroup=hero==null?null:groups.OrderByDescending(g=>g.Members.Count)
                .ThenByDescending(g=>g.OpaquePixels).FirstOrDefault();
            if(hero!=null)actors.Remove(hero);
            foreach(SpriteGroupRecord group in groups)
            {
                GameActorRecord actor=ReferenceEquals(group,heroGroup)?hero:
                    FindNearestActor(group,actors);
                if(actor!=null)actors.Remove(actor);
                result.Add(new StudioSprite
                {
                    Identity=actor==null?group.Identity:actor.Name+"  •  "+group.Identity,
                    Detail=(actor==null?string.Empty:"actor #"+actor.ActorSlot+" / ID $"+
                        actor.SpriteId.ToString("X2")+" / pose $"+
                        actor.DisplayedPose.ToString("X4")+"   ")+group.Members.Count+" parts   "+
                        SlotSummary(group.Members),
                    X=group.X,Y=group.Y,Width=group.Width,Height=group.Height,
                    OpaquePixels=group.OpaquePixels,IntersectsScreen=true,
                    Pixels=group.Pixels,Members=group.Members
                });
            }
            return result;
        }

        private static GameActorRecord FindNearestActor(SpriteGroupRecord group,
            List<GameActorRecord> actors)
        {
            GameActorRecord best=null;int bestScore=int.MaxValue;
            foreach(GameActorRecord actor in actors)
            {
                int dx=DistanceToRange(actor.ScreenX,group.X-8,group.X+group.Width+8);
                int dy=DistanceToRange(actor.ScreenY,group.Y-8,group.Y+group.Height+8);
                int score=dx*dx+dy*dy;
                if(score<bestScore){best=actor;bestScore=score;}
            }
            return bestScore<=12*12?best:null;
        }

        private static int DistanceToRange(int value,int minimum,int maximum) =>
            value<minimum?minimum-value:value>maximum?value-maximum:0;

        private List<StudioSprite> BuildBackgroundViews()
        {
            return _backgrounds.Select(item=>new StudioSprite
            {
                Identity=(string.IsNullOrWhiteSpace(_manifest?.LevelName)?string.Empty:
                    _manifest.LevelName+"  •  ")+"BG"+(item.Background+1)+
                    " scenery  •  "+item.TileCount+" tiles",
                Detail=item.Id,
                X=item.X,Y=item.Y,Width=item.Width,Height=item.Height,
                OpaquePixels=item.OpaquePixels,IntersectsScreen=true,Pixels=item.Pixels,
                BackgroundObject=item
            }).ToList();
        }

        private float GetBackgroundDepthStep()
        {
            float value=_manifest?.BackgroundDepthStep??0.08f;
            return value>0.0001f&&value<=1f?value:0.08f;
        }

        private static string SlotSummary(List<SpriteRecord> members)
        {
            int[] slots=members.Select(m=>m.Slot).OrderBy(v=>v).ToArray();
            if(slots.Length==0)return string.Empty;
            if(slots.Length==1)return "slot #"+slots[0];
            bool consecutive=true;for(int i=1;i<slots.Length;i++)if(slots[i]!=slots[i-1]+1){consecutive=false;break;}
            return consecutive?"slots #"+slots[0]+"–"+slots[^1]:"slots "+string.Join(",",slots.Take(8))+(slots.Length>8?"…":"");
        }

        private static Bitmap CreateBitmap(StudioSprite sprite)
        {
            var bitmap=new Bitmap(Math.Max(1,sprite.Width),Math.Max(1,sprite.Height),PixelFormat.Format32bppArgb);
            for(int y=0;y<sprite.Height;y++)for(int x=0;x<sprite.Width;x++)
                bitmap.SetPixel(x,y,Color.FromArgb(unchecked((int)sprite.Pixels[y*sprite.Width+x])));
            return bitmap;
        }

        private void DisposeBitmaps(){foreach(Bitmap b in _bitmaps)b.Dispose();_bitmaps.Clear();}
        private static string LocalTime(string utc)=>DateTime.TryParse(utc,out DateTime parsed)?parsed.ToLocalTime().ToString("MMM d, h:mm:ss tt"):utc;
        private void ShowError(string title,Exception exception){_status.Text=title+": "+exception.Message;MessageBox.Show(exception.ToString(),title,MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }
}
