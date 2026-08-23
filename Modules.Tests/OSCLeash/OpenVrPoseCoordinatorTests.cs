using CrookedToe.Modules.OSCLeash;
using Valve.VR;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class OpenVrPoseCoordinatorTests
{
    [TestMethod]
    public void FailedPreviewDoesNotClaimPoseOwnership()
    {
        var backend = new FakeStandingPoseBackend(IdentityPose(y: 1f));
        var coordinator = new OpenVrPoseCoordinator(backend);
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.TryConnect());
        backend.FailPreview = true;

        PoseUpdateResult result = coordinator.ApplyOffset(0.5f);

        Assert.AreEqual(PoseUpdateResult.WriteFailed, result);
        Assert.IsFalse(coordinator.OwnsPose);
        Assert.AreEqual(0f, coordinator.LastAppliedOffset);
    }

    [TestMethod]
    public void FailedCleanupPreservesOwnershipAndCanBeRetried()
    {
        HmdMatrix34_t baseline = IdentityPose(y: 1f);
        var backend = new FakeStandingPoseBackend(baseline);
        var coordinator = new OpenVrPoseCoordinator(backend);
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.TryConnect());
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.ApplyOffset(0.5f));
        Assert.IsTrue(coordinator.OwnsPose);

        backend.FailRead = true;
        Assert.AreEqual(PoseUpdateResult.ReadFailed, coordinator.RemoveOwnOffset());
        Assert.IsTrue(coordinator.OwnsPose);
        coordinator.Disconnect();
        Assert.IsTrue(coordinator.OwnsPose);

        backend.FailRead = false;
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.TryConnect());
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.RemoveOwnOffset());
        Assert.IsFalse(coordinator.OwnsPose);
        Assert.IsTrue(StandingPoseMath.ApproximatelyEquals(baseline, backend.LivePose));
    }

    [TestMethod]
    public void ExternalWriterIsNeverOverwritten()
    {
        var backend = new FakeStandingPoseBackend(IdentityPose(y: 1f));
        var coordinator = new OpenVrPoseCoordinator(backend);
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.TryConnect());
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.ApplyOffset(0.5f));
        int writesBeforeConflict = backend.PreviewCount;

        HmdMatrix34_t externalPose = backend.LivePose;
        externalPose.m3 += 0.1f;
        backend.LivePose = externalPose;

        Assert.AreEqual(PoseUpdateResult.ExternalWriterActive, coordinator.ApplyOffset(0.6f));
        Assert.AreEqual(writesBeforeConflict, backend.PreviewCount);
        Assert.IsFalse(coordinator.OwnsPose);
    }

    [TestMethod]
    public void RefreshBaselineNeverAdoptsAnOwnedOffset()
    {
        HmdMatrix34_t baseline = IdentityPose(y: 1f);
        var backend = new FakeStandingPoseBackend(baseline);
        var coordinator = new OpenVrPoseCoordinator(backend);
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.TryConnect());
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.ApplyOffset(-0.6f));

        PoseUpdateResult refreshResult = coordinator.RefreshBaseline();

        Assert.AreEqual(PoseUpdateResult.NoChange, refreshResult);
        Assert.IsTrue(coordinator.OwnsPose);
        Assert.AreEqual(1f, coordinator.ReferenceHeight, 0.0001f);
        Assert.AreEqual(-0.6f, coordinator.LastAppliedOffset, 0.0001f);
        Assert.AreEqual(PoseUpdateResult.Success, coordinator.RemoveOwnOffset());
        Assert.IsTrue(StandingPoseMath.ApproximatelyEquals(baseline, backend.LivePose));
    }

    private static HmdMatrix34_t IdentityPose(float x = 0f, float y = 0f, float z = 0f)
        => new()
        {
            m0 = 1f,
            m5 = 1f,
            m10 = 1f,
            m3 = x,
            m7 = y,
            m11 = z
        };

    private sealed class FakeStandingPoseBackend(HmdMatrix34_t initialPose) : IStandingPoseBackend
    {
        public HmdMatrix34_t LivePose { get; set; } = initialPose;
        public bool FailRead { get; set; }
        public bool FailPreview { get; set; }
        public int PreviewCount { get; private set; }

        public bool TryRead(out HmdMatrix34_t pose)
        {
            pose = LivePose;
            return !FailRead;
        }

        public bool TryPreview(HmdMatrix34_t pose)
        {
            PreviewCount++;
            if (FailPreview)
                return false;

            LivePose = pose;
            return true;
        }
    }
}
