using CrookedToe.Modules.OSCLeash;
using Valve.VR;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class PoseOwnershipTrackerTests
{
    [TestMethod]
    public void SmallExternalOverwriteYieldsInsteadOfReapplying()
    {
        HmdMatrix34_t baseline = IdentityPose(y: 0.2f);
        var tracker = new PoseOwnershipTracker();
        tracker.CaptureBaseline(baseline);

        Assert.AreEqual(PoseUpdateResult.Success, tracker.TryCompose(baseline, 0.01f, out HmdMatrix34_t firstWrite));
        tracker.AcceptWrite(firstWrite, 0.01f);

        PoseUpdateResult result = tracker.TryCompose(baseline, 0.011f, out _);

        Assert.AreEqual(PoseUpdateResult.ExternalWriterActive, result);
    }

    [TestMethod]
    public void VerticalOffsetUsesTheBaselinesUpAxis()
    {
        HmdMatrix34_t baseline = IdentityPose(x: 1f, y: 2f, z: 3f);
        baseline.m1 = 0.6f;
        baseline.m5 = 0.8f;
        baseline.m9 = 0f;

        HmdMatrix34_t result = StandingPoseMath.ApplyVerticalOffset(baseline, 2f);

        Assert.AreEqual(2.2f, result.m3, 0.0001f);
        Assert.AreEqual(3.6f, result.m7, 0.0001f);
        Assert.AreEqual(3f, result.m11, 0.0001f);
    }

    [TestMethod]
    public void RestoreIsSkippedAfterAnExternalPoseChange()
    {
        HmdMatrix34_t baseline = IdentityPose(y: 1f);
        var tracker = new PoseOwnershipTracker();
        tracker.CaptureBaseline(baseline);
        tracker.TryCompose(baseline, 0.5f, out HmdMatrix34_t writtenPose);
        tracker.AcceptWrite(writtenPose, 0.5f);

        HmdMatrix34_t externalPose = baseline;
        externalPose.m3 = 0.1f;

        Assert.AreEqual(PoseUpdateResult.ExternalWriterActive, tracker.TryBuildRestore(externalPose, out _));
    }

    [TestMethod]
    public void RestoreReturnsToTheCapturedBaselineWhenStillOwned()
    {
        HmdMatrix34_t baseline = IdentityPose(y: 1f);
        var tracker = new PoseOwnershipTracker();
        tracker.CaptureBaseline(baseline);
        tracker.TryCompose(baseline, 0.5f, out HmdMatrix34_t writtenPose);
        tracker.AcceptWrite(writtenPose, 0.5f);

        PoseUpdateResult result = tracker.TryBuildRestore(writtenPose, out HmdMatrix34_t restoredPose);

        Assert.AreEqual(PoseUpdateResult.Success, result);
        Assert.IsTrue(StandingPoseMath.ApproximatelyEquals(baseline, restoredPose));
    }

    private static HmdMatrix34_t IdentityPose(float x = 0f, float y = 0f, float z = 0f)
    {
        return new HmdMatrix34_t
        {
            m0 = 1f,
            m5 = 1f,
            m10 = 1f,
            m3 = x,
            m7 = y,
            m11 = z
        };
    }
}
