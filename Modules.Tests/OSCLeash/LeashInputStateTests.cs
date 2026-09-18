using CrookedToe.Modules.OSCLeash;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class LeashInputStateTests
{
    [TestMethod]
    public void SnapshotKeepsSignalAndGatesFromTheSameInstant()
    {
        var input = new LeashInputState();
        input.Set(OSCLeashParameter.IsGrabbed, true);
        input.Set(OSCLeashParameter.XPositive, 0.75f);
        input.Set(OSCLeashParameter.Stretch, 1f);
        var snapshot = input.Snapshot;
        input.Reset();
        Assert.IsTrue(snapshot.GrabbedForMotion);
        Assert.AreEqual(0.75f, snapshot.Signal.NetX);
        Assert.IsFalse(input.Snapshot.GrabbedForMotion);
        Assert.AreEqual(0f, input.Snapshot.Signal.NetX);
    }

    [TestMethod]
    public void OutputFreshnessGuardDoesNotRequireTheControlLoopToNoticeASilence()
    {
        var input = new LeashInputState();
        input.Set(OSCLeashParameter.IsGrabbed, true, timestamp: 1);
        Assert.IsTrue(input.GrabbedForMotion);
        Assert.IsFalse(input.CanContinueMotion());
        input.Set(OSCLeashParameter.Stretch, 1f);
        Assert.IsTrue(input.CanContinueMotion());
        input.Set(OSCLeashParameter.IsGrabbed, false);
        Assert.IsFalse(input.CanContinueMotion());
    }

    [TestMethod]
    public void ConcurrentNewerInputCannotProduceANegativeDiagnosticAge()
    {
        var input = new LeashInputState();
        input.Set(OSCLeashParameter.Stretch, 1f, timestamp: 200);
        Assert.AreEqual(0f, input.InputAgeSeconds(100));
    }

    [TestMethod]
    public void ResetInvalidatesEveryValueFromThePreviousAvatar()
    {
        var input = new LeashInputState();
        input.Set(OSCLeashParameter.IsGrabbed, true);
        input.Set(OSCLeashParameter.LeashEnable, false);
        input.Set(OSCLeashParameter.Stretch, 1f);
        input.Set(OSCLeashParameter.XPositive, 1f);
        input.Set(OSCLeashParameter.YNegative, 1f);
        input.Set(OSCLeashParameter.ZNegative, 1f);

        input.Reset();

        Assert.IsFalse(input.GrabbedForMotion);
        Assert.AreEqual(LeashSignal.From(0f, 0f, 0f, 0f), input.Signal);

        input.Set(OSCLeashParameter.IsGrabbed, true);
        Assert.IsTrue(input.GrabbedForMotion, "The optional enable parameter did not return to its enabled default.");
    }

    [TestMethod]
    public void NonFiniteParameterValuesFailClosed()
    {
        var input = new LeashInputState();
        input.Set(OSCLeashParameter.IsGrabbed, true);
        input.Set(OSCLeashParameter.Stretch, float.NaN);
        input.Set(OSCLeashParameter.XPositive, float.PositiveInfinity);

        Assert.AreEqual(0f, input.Signal.Stretch);
        Assert.AreEqual(0f, input.Signal.NetX);
    }

    [TestMethod]
    public void StaleGrabNeutralizesAndRequiresReleaseBeforeRegrab()
    {
        var input = new LeashInputState();
        input.Set(OSCLeashParameter.IsGrabbed, true, timestamp: 100);
        input.Set(OSCLeashParameter.Stretch, 1f, timestamp: 100);

        Assert.IsTrue(input.ExpireIfStale(now: 201, timeoutTicks: 100));
        Assert.IsFalse(input.GrabbedForMotion);
        Assert.IsTrue(input.RequiresGrabRelease);
        Assert.AreEqual(LeashSignal.From(0f, 0f, 0f, 0f), input.Signal);

        input.Set(OSCLeashParameter.IsGrabbed, true, timestamp: 202);
        Assert.IsFalse(input.GrabbedForMotion);

        input.Set(OSCLeashParameter.IsGrabbed, false, timestamp: 203);
        input.Set(OSCLeashParameter.IsGrabbed, true, timestamp: 204);
        Assert.IsTrue(input.GrabbedForMotion);
        Assert.IsFalse(input.RequiresGrabRelease);
    }

    [TestMethod]
    public void FreshInputDoesNotExpire()
    {
        var input = new LeashInputState();
        input.Set(OSCLeashParameter.Stretch, 1f, timestamp: 100);

        Assert.IsFalse(input.ExpireIfStale(now: 200, timeoutTicks: 100));
        Assert.AreEqual(1f, input.Signal.Stretch);
    }

    [TestMethod]
    public void MissingOptionalEnableParameterKeepsLeashEnabled()
    {
        var input = new LeashInputState();
        input.ConfigureOptionalGates(hasLeashEnableParameter: false, hasLeashDisableParameter: false);

        input.Set(OSCLeashParameter.LeashEnable, false);
        input.Set(OSCLeashParameter.IsGrabbed, true);

        Assert.IsTrue(input.GrabbedForMotion);
        Assert.IsFalse(input.Snapshot.HasLeashEnableParameter);
        Assert.IsTrue(input.Snapshot.LeashEnabled);
    }

    [TestMethod]
    public void PresentOptionalEnableParameterCanDisableLeash()
    {
        var input = new LeashInputState();
        input.ConfigureOptionalGates(hasLeashEnableParameter: true, hasLeashDisableParameter: false);
        input.Set(OSCLeashParameter.IsGrabbed, true);
        input.Set(OSCLeashParameter.LeashEnable, false);

        Assert.IsFalse(input.GrabbedForMotion);

        input.Set(OSCLeashParameter.LeashEnable, true);
        Assert.IsTrue(input.GrabbedForMotion);
    }

    [TestMethod]
    public void DisableParameterUsesSafeFalseDefault()
    {
        var input = new LeashInputState();
        input.ConfigureOptionalGates(hasLeashEnableParameter: false, hasLeashDisableParameter: true);
        input.Set(OSCLeashParameter.IsGrabbed, true);
        input.Set(OSCLeashParameter.LeashDisable, false);

        Assert.IsTrue(input.GrabbedForMotion);

        input.Set(OSCLeashParameter.LeashDisable, true);
        Assert.IsFalse(input.GrabbedForMotion);
    }

    [TestMethod]
    public void ShortTransportSilenceSuppressesMotionAndClearsOldVectorOnRecovery()
    {
        var input = new LeashInputState();
        input.Set(OSCLeashParameter.IsGrabbed, true, timestamp: 100);
        input.Set(OSCLeashParameter.Stretch, 1f, timestamp: 100);
        input.Set(OSCLeashParameter.XPositive, 1f, timestamp: 100);

        Assert.IsTrue(input.SuppressMotionIfSilent(now: 131, timeoutTicks: 30));
        Assert.IsFalse(input.GrabbedForMotion);
        Assert.IsTrue(input.LeashEngaged, "A soft transport gap must not synthesize a leash release.");
        Assert.IsTrue(input.MotionSuppressed);

        input.Set(OSCLeashParameter.XNegative, 0.4f, timestamp: 132);

        Assert.IsTrue(input.GrabbedForMotion);
        Assert.IsTrue(input.LeashEngaged, "Recovery must not synthesize a second grab transition.");
        Assert.IsFalse(input.MotionSuppressed);
        Assert.AreEqual(0f, input.Signal.Stretch);
        Assert.AreEqual(-0.4f, input.Signal.NetX, 0.0001f);
    }

    [TestMethod]
    public void LongTransportSilenceStillRequiresReleaseAfterSoftSuppression()
    {
        var input = new LeashInputState();
        input.Set(OSCLeashParameter.IsGrabbed, true, timestamp: 100);

        Assert.IsTrue(input.SuppressMotionIfSilent(now: 131, timeoutTicks: 30));
        Assert.IsTrue(input.ExpireIfStale(now: 301, timeoutTicks: 200));
        Assert.IsTrue(input.RequiresGrabRelease);

        input.Set(OSCLeashParameter.IsGrabbed, true, timestamp: 302);
        Assert.IsFalse(input.GrabbedForMotion);
    }
}
