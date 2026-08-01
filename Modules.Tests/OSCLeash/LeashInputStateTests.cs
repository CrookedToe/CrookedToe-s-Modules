using CrookedToe.Modules.OSCLeash;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class LeashInputStateTests
{
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
}
