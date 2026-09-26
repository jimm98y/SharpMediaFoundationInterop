namespace SharpSpatialVideo.Tests;

/// <summary>
/// The four conversions, each checked against what ffmpeg decodes rather than against itself.
///
/// These need a spatial video to work on - SHARPSPATIAL_SAMPLE, or an iPhone recording at
/// C:\Temp\IMG_7881.MOV - and an ffmpeg built with MV-HEVC support to check against. Without
/// either they are skipped rather than failed, because neither can be checked into the repository.
/// </summary>
[TestClass]
public class ConversionTests
{
    /// <summary>
    /// Which eye the base layer carries in Apple's footage, measured from the disparity between
    /// the decoded views. The same decision the tool makes.
    /// </summary>
    private const bool BaseLayerIsLeftEye = false;

    /// <summary>Enough to cover a key frame and the pictures that depend on it.</summary>
    private const int Frames = 12;

    private static string _sample;
    private static string _work;

    [ClassInitialize]
    public static void Prepare(TestContext context)
    {
        _sample = Environment.GetEnvironmentVariable("SHARPSPATIAL_SAMPLE")
            ?? @"C:\Temp\IMG_7881.MOV";

        _work = Path.Combine(Path.GetTempPath(), "SharpSpatialVideo.Tests");
        Directory.CreateDirectory(_work);
    }

    [TestInitialize]
    public void RequireSampleAndFfmpeg()
    {
        if (!File.Exists(_sample))
            Assert.Inconclusive($"no spatial video at {_sample}; set SHARPSPATIAL_SAMPLE to one");

        if (Ffmpeg.Locate() == null)
            Assert.Inconclusive("no ffmpeg found; set SHARPSPATIAL_FFMPEG to one built with MV-HEVC support");
    }

    /// <summary>
    /// Each eye comes out of the split holding the pictures that view holds in the original, to
    /// the bit: the split re-stamps headers and rewrites reference sets, but touches no pixel.
    /// </summary>
    [TestMethod]
    public void SplitKeepsEachEyesPicturesExactly()
    {
        var split = EyeSplitter.Write(_sample, Path.Combine(_work, "split"), BaseLayerIsLeftEye);

        // View 0 is the base layer, view 1 the dependent one.
        CollectionAssert.AreEqual(
            Ffmpeg.FrameHashes(_sample, viewId: 0),
            Ffmpeg.FrameHashes(split.BasePath),
            $"the {split.BaseEye} eye differs from view 0 of the original");

        CollectionAssert.AreEqual(
            Ffmpeg.FrameHashes(_sample, viewId: 1),
            Ffmpeg.FrameHashes(split.DependentPath),
            $"the {split.DependentEye} eye differs from view 1 of the original");
    }

    /// <summary>
    /// Two ordinary one-eye videos - what an editor hands back after working on each eye - go
    /// into spatial video with each eye's pictures untouched. Nothing is decoded on the way, so
    /// what comes out has to hold exactly what went in.
    /// </summary>
    /// <remarks>
    /// The eyes here are encoded from the sample rather than taken from the split, whose dependent
    /// file is not an ordinary video: it carries the base pictures as well, marked not to be
    /// output, because the dependent view is predicted from them.
    /// </remarks>
    [TestMethod]
    public void MuxingTwoEyesKeepsTheirPicturesExactly()
    {
        string left = Path.Combine(_work, "left.mp4");
        string right = Path.Combine(_work, "right.mp4");
        string muxed = Path.Combine(_work, "muxed.mov");

        // View 1 is the left eye and view 0 the right, which is what the eye mapping says.
        Ffmpeg.EncodeView(_sample, BaseLayerIsLeftEye ? 0 : 1, left, Frames);
        Ffmpeg.EncodeView(_sample, BaseLayerIsLeftEye ? 1 : 0, right, Frames);

        LeftRightTranscoder.Write(left, right, muxed, new StereoMetadata());

        // The base layer holds the right eye, the dependent layer the left.
        CollectionAssert.AreEqual(Ffmpeg.FrameHashes(right), Ffmpeg.FrameHashes(muxed, viewId: 0),
            "view 0 differs from the right eye that went in");
        CollectionAssert.AreEqual(Ffmpeg.FrameHashes(left), Ffmpeg.FrameHashes(muxed, viewId: 1),
            "view 1 differs from the left eye that went in");
    }

    /// <summary>
    /// The same with every picture coded as several slices, as an encoder working in parallel
    /// writes them. Each slice is restamped into layer 1 on its own, so an access unit carries
    /// several, and none may be written over by the next.
    /// </summary>
    [TestMethod]
    public void MuxingEyesWithSeveralSlicesAPictureKeepsThemExactly()
    {
        string left = Path.Combine(_work, "left_slices.mp4");
        string right = Path.Combine(_work, "right_slices.mp4");
        string muxed = Path.Combine(_work, "muxed_slices.mov");

        Ffmpeg.EncodeView(_sample, BaseLayerIsLeftEye ? 0 : 1, left, Frames, slices: 4);
        Ffmpeg.EncodeView(_sample, BaseLayerIsLeftEye ? 1 : 0, right, Frames, slices: 4);

        var dependent = MvHevcReader.Read(left);
        Assert.IsTrue(dependent.AccessUnits.All(au => au.Nalus.Count(n => n.IsSlice) > 1),
            $"{left} has pictures of a single slice, so it does not cover several");

        LeftRightTranscoder.Write(left, right, muxed, new StereoMetadata());

        CollectionAssert.AreEqual(Ffmpeg.FrameHashes(right), Ffmpeg.FrameHashes(muxed, viewId: 0),
            "view 0 differs from the right eye that went in");
        CollectionAssert.AreEqual(Ffmpeg.FrameHashes(left), Ffmpeg.FrameHashes(muxed, viewId: 1),
            "view 1 differs from the left eye that went in");

        // The split takes one slice a picture, and says so rather than leaving the rest out.
        Assert.ThrowsException<NotSupportedException>(
            () => EyeSplitter.Write(muxed, Path.Combine(_work, "split_slices"), BaseLayerIsLeftEye));
    }

    /// <summary>
    /// And the pair the split makes goes back together as the file it came from: every parameter
    /// set and every coded slice, byte for byte. The split carries what it had to rewrite - see
    /// <see cref="RoundTripSei"/> - so nothing has to be worked out again on the way back.
    /// </summary>
    [TestMethod]
    public void SplitAndMuxRestoreTheOriginalExactly()
    {
        var split = EyeSplitter.Write(_sample, Path.Combine(_work, "split"), BaseLayerIsLeftEye);
        string rebuilt = Path.Combine(_work, "rebuilt.mov");

        SpatialVideoRebuilder.Write(split.BasePath, split.DependentPath, rebuilt, new StereoMetadata());

        // The whole file, not just the video: the split carries the container too, so what comes
        // back is the file it was split from rather than one holding the same pictures.
        CollectionAssert.AreEqual(File.ReadAllBytes(_sample), File.ReadAllBytes(rebuilt),
            "the rebuilt file differs from the original");

        var before = MvHevcReader.Read(_sample);
        var after = MvHevcReader.Read(rebuilt);

        CollectionAssert.AreEqual(before.BaseParameterSets, after.BaseParameterSets,
            new ByteArrayComparer(), "the base layer's parameter sets differ");
        CollectionAssert.AreEqual(before.LayerParameterSets, after.LayerParameterSets,
            new ByteArrayComparer(), "layer 1's parameter sets differ");

        Assert.AreEqual(before.AccessUnits.Count, after.AccessUnits.Count);

        for (int i = 0; i < before.AccessUnits.Count; i++)
        {
            var original = before.AccessUnits[i].Nalus;
            var restored = after.AccessUnits[i].Nalus;

            Assert.AreEqual(original.Count, restored.Count, $"access unit {i} holds a different number of NAL units");

            for (int j = 0; j < original.Count; j++)
            {
                CollectionAssert.AreEqual(original[j].Data.ToArray(), restored[j].Data.ToArray(),
                    $"access unit {i}, NAL unit {j} ({original[j]})");
            }
        }
    }

    /// <summary>
    /// And so does a spatial video whose key frames are IDR pictures, which is what this tool's own
    /// side by side conversion writes. Such a stream starts its picture order count again at every
    /// key frame, so the count names one picture per key frame rather than one picture in the file:
    /// anything the split files by it collides, and the file comes back holding another key frame's
    /// slice headers. Apple's own footage uses CRA key frames and never resets, so it does not
    /// cover this.
    /// </summary>
    [TestMethod]
    public void SplitAndMuxRestoreAStreamWhoseKeyFramesResetTheCount()
    {
        string sideBySide = Path.Combine(_work, "sbs.mp4");
        WriteSideBySide(sideBySide);

        // A key frame every four pictures, so the count starts again more than once in twelve.
        string spatial = new SbsToMultiview { SimulcastGopSize = 4 }
            .Write(sideBySide, Path.Combine(_work, "idr"), bitrate: 40_000_000, limit: Frames)
            .Single().Path;

        var written = MvHevcReader.Read(spatial);
        int keyFrames = written.AccessUnits.Count(au => au.Nalus.Any(n => n.Type is 19 or 20));
        Assert.IsTrue(keyFrames > 1,
            $"{spatial} holds {keyFrames} IDR access units, so it does not cover a count that restarts");

        var split = EyeSplitter.Write(spatial, Path.Combine(_work, "idrsplit"), BaseLayerIsLeftEye);

        // Each eye file plays, and holds exactly that view's pictures. The dependent file's
        // interleave cannot restart its count at a key frame the way the source does, so its base
        // key frames are rewritten as CRA pictures - which must decode to the same pixels.
        CollectionAssert.AreEqual(Ffmpeg.FrameHashes(spatial, viewId: 0), Ffmpeg.FrameHashes(split.BasePath),
            $"the {split.BaseEye} eye differs from view 0");
        CollectionAssert.AreEqual(Ffmpeg.FrameHashes(spatial, viewId: 1), Ffmpeg.FrameHashes(split.DependentPath),
            $"the {split.DependentEye} eye differs from view 1");

        string rebuilt = Path.Combine(_work, "idr_rebuilt.mov");
        SpatialVideoRebuilder.Write(split.BasePath, split.DependentPath, rebuilt, new StereoMetadata());

        CollectionAssert.AreEqual(File.ReadAllBytes(spatial), File.ReadAllBytes(rebuilt),
            "the rebuilt file differs from the one it was split from");
    }

    /// <summary>
    /// The side by side file carries both eyes, each where it belongs. This one is re-encoded, so
    /// what can be said is how close it stays - not that it is the same.
    /// </summary>
    [TestMethod]
    public void SideBySideCarriesBothEyes()
    {
        string sideBySide = Path.Combine(_work, "sbs.mp4");
        WriteSideBySide(sideBySide);

        // The left half of the picture against view 1, the right half against view 0: the base
        // layer is the right eye, so that is the half it belongs in.
        double left = Ffmpeg.Psnr(_sample, sideBySide,
            secondFilter: "crop=iw/2:ih:0:0", viewId: BaseLayerIsLeftEye ? 0 : 1, frames: Frames);
        double right = Ffmpeg.Psnr(_sample, sideBySide,
            secondFilter: "crop=iw/2:ih:iw/2:0", viewId: BaseLayerIsLeftEye ? 1 : 0, frames: Frames);

        Assert.IsTrue(left > 30, $"the left half is {left:F1} dB from the left eye");
        Assert.IsTrue(right > 30, $"the right half is {right:F1} dB from the right eye");
    }

    /// <summary>
    /// And a side by side file converts back into spatial video with each half in the view it
    /// belongs to. Re-encoded again, so again this is about how close it stays.
    /// </summary>
    [TestMethod]
    public void SideBySideConvertsBackToSpatialVideo()
    {
        string sideBySide = Path.Combine(_work, "sbs.mp4");
        WriteSideBySide(sideBySide);

        string stem = Path.Combine(_work, "roundtrip");
        var results = new SbsToMultiview().Write(sideBySide, stem, bitrate: 40_000_000, limit: Frames);

        string spatial = results.Single().Path;

        // The base layer holds the right eye - the eye mapping SEI carried over from the template
        // says so - which is the right half of the side by side picture.
        double baseView = Ffmpeg.Psnr(spatial, sideBySide,
            secondFilter: "crop=iw/2:ih:iw/2:0", viewId: 0, frames: Frames);
        double dependentView = Ffmpeg.Psnr(spatial, sideBySide,
            secondFilter: "crop=iw/2:ih:0:0", viewId: 1, frames: Frames);

        Assert.IsTrue(baseView > 30, $"view 0 is {baseView:F1} dB from the right half");
        Assert.IsTrue(dependentView > 30, $"view 1 is {dependentView:F1} dB from the left half");
    }

    /// <summary>Compares byte arrays by their contents, which CollectionAssert does not.</summary>
    private sealed class ByteArrayComparer : System.Collections.IComparer
    {
        public int Compare(object x, object y) =>
            ((byte[])x).SequenceEqual((byte[])y) ? 0 : 1;
    }

    /// <summary>The side by side file both of the last two tests work from.</summary>
    private static void WriteSideBySide(string path)
    {
        if (File.Exists(path))
            return;

        var track = MvHevcReader.Read(_sample, loadSamples: false);
        using var rewriter = new SingleLayerRewriter(track);
        rewriter.Plan();

        // The limit counts pictures of the rewritten stream, which holds both views, so it takes
        // rather more than one per frame.
        new SbsConverter(track, rewriter).Convert(path, bitrate: 30_000_000,
            baseLayerIsLeftEye: BaseLayerIsLeftEye, limit: Frames * 3);
    }
}
