using CrookedToe.Modules.OSCLeash;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class NonOvershootingAxisFilterTests
{
    [TestMethod]
    public void OutputStopsImmediatelyWhenCurrentPullIsZero()
    {
        var filter = new NonOvershootingAxisFilter();
        filter.Update(1f, smoothing: 0.7f, deltaTime: 0.008f);

        float output = filter.Update(0f, smoothing: 0.7f, deltaTime: 0.008f);

        Assert.AreEqual(0f, output);
    }

    [TestMethod]
    public void WeakerPullCannotBeExceededBySmoothedOutput()
    {
        var filter = new NonOvershootingAxisFilter();
        for (int i = 0; i < 20; i++)
            filter.Update(1f, smoothing: 0.7f, deltaTime: 0.008f);

        float output = filter.Update(0.2f, smoothing: 0.7f, deltaTime: 0.008f);

        Assert.AreEqual(0.2f, output, 0.0001f);
    }

    [TestMethod]
    public void DirectionReversalBrakesBeforeDrivingTheOtherDirection()
    {
        var filter = new NonOvershootingAxisFilter();
        for (int i = 0; i < 20; i++)
            filter.Update(1f, smoothing: 0.7f, deltaTime: 0.008f);

        float firstReverseFrame = filter.Update(-1f, smoothing: 0.7f, deltaTime: 0.008f);

        Assert.AreEqual(0f, firstReverseFrame);
    }

    [TestMethod]
    public void OppositeDirectionMustRemainStableBeforeItIsAccepted()
    {
        var filter = new NonOvershootingAxisFilter();
        filter.Update(1f, smoothing: 0f, deltaTime: 0.008f);

        for (int i = 0; i < 14; i++)
            Assert.AreEqual(0f, filter.Update(-1f, smoothing: 0f, deltaTime: 0.008f));

        float acceptedOutput = filter.Update(-1f, smoothing: 0f, deltaTime: 0.008f);
        Assert.IsTrue(acceptedOutput < 0f);
    }
}
