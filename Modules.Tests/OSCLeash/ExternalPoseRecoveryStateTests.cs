using CrookedToe.Modules.OSCLeash;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class ExternalPoseRecoveryStateTests
{
    [TestMethod]
    public void OneTimeConflictAutomaticallyResumesAfterQuietPeriod()
    {
        var recovery = new ExternalPoseRecoveryState();

        Assert.IsTrue(recovery.Suspend(grabbedForMotion: true, nowSeconds: 1d));
        Assert.IsFalse(recovery.Observe(true, poseChanged: false, nowSeconds: 1.2d, quietSeconds: 0.25d));
        Assert.IsTrue(recovery.Observe(true, poseChanged: false, nowSeconds: 1.25d, quietSeconds: 0.25d));
        Assert.IsFalse(recovery.Suspended);
    }

    [TestMethod]
    public void ExternalMovementRestartsTheQuietPeriod()
    {
        var recovery = new ExternalPoseRecoveryState();
        recovery.Suspend(grabbedForMotion: true, nowSeconds: 1d);

        Assert.IsFalse(recovery.Observe(true, poseChanged: true, nowSeconds: 1.2d, quietSeconds: 0.25d));
        Assert.IsFalse(recovery.Observe(true, poseChanged: false, nowSeconds: 1.4d, quietSeconds: 0.25d));
        Assert.IsTrue(recovery.Observe(true, poseChanged: false, nowSeconds: 1.45d, quietSeconds: 0.25d));
    }

    [TestMethod]
    public void RepeatedConflictLocksUntilNextGrab()
    {
        var recovery = new ExternalPoseRecoveryState();
        recovery.Suspend(grabbedForMotion: true, nowSeconds: 1d);
        recovery.Observe(true, poseChanged: false, nowSeconds: 1.3d, quietSeconds: 0.25d);

        Assert.IsFalse(recovery.Suspend(grabbedForMotion: true, nowSeconds: 1.4d));
        Assert.IsTrue(recovery.LockedUntilRegrab);

        recovery.Reset();
        Assert.IsFalse(recovery.Suspended);
        Assert.IsFalse(recovery.LockedUntilRegrab);
    }
}
