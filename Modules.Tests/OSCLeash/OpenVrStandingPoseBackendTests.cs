using CrookedToe.Modules.OSCLeash;
using Valve.VR;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class OpenVrStandingPoseBackendTests
{
    [TestMethod]
    public void RawToStandingIsInvertedWithRotationAndTranslation()
    {
        // A 90-degree rotation around Z plus translation. Translation cannot merely
        // be negated: it must also be transformed by the inverse rotation.
        var rawToStanding = new HmdMatrix34_t
        {
            m1 = -1f, m4 = 1f, m10 = 1f, m3 = 2f, m7 = 3f, m11 = 4f
        };
        var backend = new OpenVrStandingPoseBackend(() => rawToStanding, _ => true);
        Assert.IsTrue(backend.TryRead(out var standingToRaw));
        var expected = new HmdMatrix34_t
        {
            m1 = 1f, m4 = -1f, m10 = 1f, m3 = -3f, m7 = 2f, m11 = -4f
        };
        Assert.IsTrue(StandingPoseMath.ApproximatelyEquals(expected, standingToRaw));
    }

    [TestMethod]
    public void ReturnRemovesOnlyLeashHeightAndPreservesPreexistingOvrSpaceDrag()
    {
        var runtime = new SeparateWorkingCopyRuntime();
        var coordinator = new OpenVrPoseCoordinator(runtime.Backend);
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.TryConnect());

        // OVR publishes its own preview after the leash client connected. Our working
        // copy remains at its original floor, but the active origin has moved/rotated.
        var ovrPose = new HmdMatrix34_t
        {
            m2 = 1f, m5 = 1f, m8 = -1f, m3 = 3f, m7 = -1.25f, m11 = 2f
        };
        runtime.ActivePose = ovrPose;
        Assert.AreEqual(0f, runtime.WorkingPose.m7);
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.RefreshBaseline());
        Assert.AreEqual(-1.25f, coordinator.ReferenceHeight);

        var height = new VerticalMotionState();
        height.Rebase(-0.8f);
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.ApplyOffset(height.Offset));
        Assert.AreEqual(-2.05f, runtime.ActivePose.m7, 0.0001f);
        for (int i = 0; i < 300 && height.Offset != 0f; i++)
        {
            height.ReturnToOrigin(9.81f, 1f, 0.016f);
            Assert.AreEqual(PoseUpdateResult.Success, coordinator.ApplyOffset(height.Offset));
        }
        coordinator.ReleaseZeroOffsetOwnership();
        Assert.AreEqual(0f, height.Offset);
        Assert.IsFalse(coordinator.OwnsPose);
        Assert.IsTrue(StandingPoseMath.ApproximatelyEquals(ovrPose, runtime.ActivePose));
    }

    [TestMethod]
    public void OvrTakeoverDuringReturnIsDetectedEvenWhenOurWorkingCopyStillMatches()
    {
        var runtime = new SeparateWorkingCopyRuntime();
        var coordinator = new OpenVrPoseCoordinator(runtime.Backend);
        coordinator.TryConnect();
        coordinator.ApplyOffset(-0.8f);
        var lastLeashWrite = runtime.WorkingPose;
        var ovrPose = IdentityPose(2f, -1.5f, 3f);
        runtime.ActivePose = ovrPose;
        int previousWrites = runtime.Writes;

        Assert.IsTrue(StandingPoseMath.ApproximatelyEquals(lastLeashWrite, runtime.WorkingPose));
        Assert.AreEqual(PoseUpdateResult.ExternalWriterActive, coordinator.ApplyOffset(-0.7f));
        Assert.AreEqual(previousWrites, runtime.Writes);
        Assert.IsFalse(coordinator.OwnsPose);
        Assert.AreEqual(PoseUpdateResult.NoChange, coordinator.RemoveOwnOffset());
        Assert.IsTrue(StandingPoseMath.ApproximatelyEquals(ovrPose, runtime.ActivePose));

        // A subsequent grab starts from the new OVR origin without restoring the old floor.
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.RefreshBaseline());
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.ApplyOffset(-0.2f));
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.RemoveOwnOffset());
        Assert.IsTrue(StandingPoseMath.ApproximatelyEquals(ovrPose, runtime.ActivePose));
    }

    [TestMethod]
    public void StopAfterExternalTakeoverDoesNotRestoreStaleBaseline()
    {
        var runtime = new SeparateWorkingCopyRuntime();
        var coordinator = new OpenVrPoseCoordinator(runtime.Backend);
        coordinator.TryConnect();
        coordinator.ApplyOffset(-0.5f);
        var external = IdentityPose(-1f, 2f, 4f);
        runtime.ActivePose = external;
        int previousWrites = runtime.Writes;
        Assert.AreEqual(PoseUpdateResult.ExternalWriterActive, coordinator.RemoveOwnOffset());
        Assert.AreEqual(previousWrites, runtime.Writes);
        Assert.IsTrue(StandingPoseMath.ApproximatelyEquals(external, runtime.ActivePose));
    }

    [TestMethod]
    public void InvalidOrUnavailableActivePoseNeverFallsBackToAnOldWorkingCopy()
    {
        HmdMatrix34_t? current = null;
        int writes = 0;
        var backend = new OpenVrStandingPoseBackend(() => current, _ => { writes++; return true; });
        var coordinator = new OpenVrPoseCoordinator(backend);
        Assert.AreEqual(PoseUpdateResult.ReadFailed, coordinator.TryConnect());
        current = default(HmdMatrix34_t); // Zero/singular runtime result.
        Assert.AreEqual(PoseUpdateResult.ReadFailed, coordinator.TryConnect());
        var invalid = IdentityPose();
        invalid.m7 = float.NaN;
        current = invalid;
        Assert.AreEqual(PoseUpdateResult.ReadFailed, coordinator.TryConnect());
        invalid = IdentityPose();
        invalid.m0 = 2f; // Not a rigid transform.
        current = invalid;
        Assert.AreEqual(PoseUpdateResult.ReadFailed, coordinator.TryConnect());
        Assert.AreEqual(0, writes);
    }

    private static HmdMatrix34_t IdentityPose(float x = 0f, float y = 0f, float z = 0f)
        => new() { m0 = 1f, m5 = 1f, m10 = 1f, m3 = x, m7 = y, m11 = z };

    private sealed class SeparateWorkingCopyRuntime
    {
        public HmdMatrix34_t ActivePose { get; set; } = IdentityPose();
        public HmdMatrix34_t WorkingPose { get; private set; } = IdentityPose();
        public int Writes { get; private set; }
        public OpenVrStandingPoseBackend Backend { get; }

        public SeparateWorkingCopyRuntime()
        {
            Backend = new OpenVrStandingPoseBackend(
                () =>
                {
                    Assert.IsTrue(StandingPoseMath.TryInvertRigidPose(ActivePose, out var rawToStanding));
                    return rawToStanding;
                },
                pose =>
                {
                    WorkingPose = pose;
                    ActivePose = pose;
                    Writes++;
                    return true;
                });
        }
    }
}
