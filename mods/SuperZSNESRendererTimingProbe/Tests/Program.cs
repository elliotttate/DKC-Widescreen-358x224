using System;
using System.Collections.Generic;

internal static class Program
{
    private static int Main()
    {
        try
        {
            FirstVisibleDirtyFrameUploads();
            UnchangedVisibleFrameDoesNotUpload();
            OneChangedTileUploadsItsBankOnce();
            InvisibleDirtyTileRemainsPending();
            Console.WriteLine("RendererTimingProbe dirty-upload model: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            return 1;
        }
    }

    private static void FirstVisibleDirtyFrameUploads()
    {
        var model = new DirtyModel(16, 2);
        model.TileDirty[3] = true;
        model.StartFrame();
        model.GetTile(3, 0);
        Require(model.GenerateTextures() == 1, "first visible dirty tile did not upload its bank");
        Require(!model.TileDirty[3], "visible dirty tile was not consumed");
    }

    private static void UnchangedVisibleFrameDoesNotUpload()
    {
        var model = new DirtyModel(16, 2);
        model.StartFrame();
        model.GetTile(2, 0);
        model.GetTile(3, 0);
        Require(model.GenerateTextures() == 0, "unchanged visible bank was redundantly uploaded");
    }

    private static void OneChangedTileUploadsItsBankOnce()
    {
        var model = new DirtyModel(16, 2);
        model.TileDirty[1] = true;
        model.TileDirty[2] = true;
        model.StartFrame();
        model.GetTile(1, 0);
        model.GetTile(2, 0);
        Require(model.GenerateTextures() == 1, "multiple changed tiles in one bank did not coalesce to one upload");
    }

    private static void InvisibleDirtyTileRemainsPending()
    {
        var model = new DirtyModel(16, 2);
        model.TileDirty[12] = true;
        model.StartFrame();
        model.GetTile(1, 0);
        Require(model.GenerateTextures() == 0, "invisible dirty tile caused an unrelated upload");
        Require(model.TileDirty[12], "invisible dirty tile was consumed before it was requested");
        model.StartFrame();
        model.GetTile(12, 1);
        Require(model.GenerateTextures() == 1, "deferred dirty tile did not upload when it became visible");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class DirtyModel
    {
        internal readonly bool[] TileDirty;
        private readonly bool[] _bankDirty;

        internal DirtyModel(int tiles, int banks)
        {
            TileDirty = new bool[tiles];
            _bankDirty = new bool[banks];
        }

        internal void StartFrame() { Array.Clear(_bankDirty, 0, _bankDirty.Length); }

        internal void GetTile(int tile, int bank)
        {
            if (!TileDirty[tile]) return;
            _bankDirty[bank] = true;
            TileDirty[tile] = false;
        }

        internal int GenerateTextures()
        {
            var uploads = 0;
            foreach (var dirty in _bankDirty) if (dirty) uploads++;
            return uploads;
        }
    }
}
