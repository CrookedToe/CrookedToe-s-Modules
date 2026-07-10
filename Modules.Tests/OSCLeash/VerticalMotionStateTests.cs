using CrookedToe.Modules.OSCLeash;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class VerticalMotionStateTests
{
    [TestMethod]
    public void PullIsClampedToTheConfiguredMaximumOffset()
    {
        var motion = new VerticalMotionState();

        motion.ApplyPull(100f, smoothing: 0f, deltaTime: 1f, maximumOffset: 3f);

        Assert.AreEqual(3f, motion.Offset, 0.0001f);
        Assert.AreEqual(0f, motion.Velocity, 0.0001f);
    }

    [TestMethod]
    public void ReturnMotionStopsExactlyAtTheOrigin()
    {
        var motion = new VerticalMotionState();
        motion.Rebase(1f);

        for (int i = 0; i < 1_000 && motion.Offset != 0f; i++)
            motion.ReturnToOrigin(gravityStrength: 9.81f, terminalVelocity: 15f, deltaTime: 0.01f);

        Assert.AreEqual(0f, motion.Offset);
        Assert.AreEqual(0f, motion.Velocity);
    }

    [TestMethod]
    public void TimeAdjustedSmoothingMatchesTwoBaseRateSteps()
    {
        const float smoothing = 0.8f;
        float oneLongStep = LeashMotionEngine.SmoothingAlpha(smoothing, LeashDefaults.BaseDeltaTimeSeconds * 2f);
        float oneBaseStep = LeashMotionEngine.SmoothingAlpha(smoothing, LeashDefaults.BaseDeltaTimeSeconds);
        float twoBaseSteps = 1f - MathF.Pow(1f - oneBaseStep, 2f);

        Assert.AreEqual(twoBaseSteps, oneLongStep, 0.000001f);
    }
}
