using CrookedToe.Modules.OSCLeash;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class VerticalMotionStateTests
{
    [TestMethod]
    public void PullIsClampedToTheConfiguredMaximumOffset()
    {
        var motion = new VerticalMotionState();

        motion.ApplyPull(100f, deltaTime: 1f, maximumOffset: 3f);

        Assert.AreEqual(3f, motion.Offset, 0.0001f);
    }

    [TestMethod]
    public void ReturnMotionStopsExactlyAtTheOrigin()
    {
        var motion = new VerticalMotionState();
        motion.Rebase(1f);

        for (int i = 0; i < 1_000 && motion.Offset != 0f; i++)
            motion.ReturnToOrigin(acceleration: 9.81f, maximumSpeed: 1f, deltaTime: 0.01f);

        Assert.AreEqual(0f, motion.Offset);
    }

    [TestMethod]
    public void ReturnFromNegativeOffsetMovesTowardOriginWithoutOvershoot()
    {
        var motion = new VerticalMotionState();
        motion.Rebase(-0.005f);

        for (int i = 0; i < 100 && motion.Offset != 0f; i++)
        {
            float previousOffset = motion.Offset;
            motion.ReturnToOrigin(acceleration: 9.81f, maximumSpeed: 1f, deltaTime: 0.01f);
            Assert.IsTrue(motion.Offset >= previousOffset);
            Assert.IsTrue(motion.Offset <= 0f);
        }

        Assert.AreEqual(0f, motion.Offset);
    }

    [TestMethod]
    public void ReturnAcceleratesUntilItReachesMaximumSpeed()
    {
        var motion = new VerticalMotionState();
        motion.Rebase(1f);

        float previousOffset = motion.Offset;
        motion.ReturnToOrigin(acceleration: 1f, maximumSpeed: 0.5f, deltaTime: 0.05f);
        float firstStep = previousOffset - motion.Offset;

        previousOffset = motion.Offset;
        motion.ReturnToOrigin(acceleration: 1f, maximumSpeed: 0.5f, deltaTime: 0.05f);
        float secondStep = previousOffset - motion.Offset;

        Assert.AreEqual(0.0025f, firstStep, 0.0001f);
        Assert.AreEqual(0.005f, secondStep, 0.0001f);

        for (int i = 0; i < 10; i++)
        {
            previousOffset = motion.Offset;
            motion.ReturnToOrigin(acceleration: 1f, maximumSpeed: 0.5f, deltaTime: 0.05f);
            Assert.IsTrue(previousOffset - motion.Offset <= 0.025f + 0.0001f);
        }
    }

    [TestMethod]
    public void PullUsesTheCurrentVelocityWithoutLag()
    {
        var motion = new VerticalMotionState();

        motion.ApplyPull(0.5f, deltaTime: 0.02f, maximumOffset: 3f);

        Assert.AreEqual(0.01f, motion.Offset, 0.0001f);
    }

    [TestMethod]
    public void WeakerPullTakesEffectImmediately()
    {
        var motion = new VerticalMotionState();
        motion.ApplyPull(1f, deltaTime: 0.01f, maximumOffset: 3f);
        float previousOffset = motion.Offset;

        motion.ApplyPull(0.2f, deltaTime: 0.01f, maximumOffset: 3f);

        Assert.AreEqual(previousOffset + 0.002f, motion.Offset, 0.0001f);
    }

    [TestMethod]
    public void ReversalMovesInTheNewDirectionImmediately()
    {
        var motion = new VerticalMotionState();
        motion.ApplyPull(1f, deltaTime: 0.01f, maximumOffset: 3f);
        float previousOffset = motion.Offset;

        motion.ApplyPull(-1f, deltaTime: 0.01f, maximumOffset: 3f);

        Assert.IsTrue(motion.Offset < previousOffset);
    }

    [TestMethod]
    public void NeutralPullStopsWithoutChangingOffset()
    {
        var motion = new VerticalMotionState();
        motion.ApplyPull(1f, deltaTime: 0.01f, maximumOffset: 3f);
        float previousOffset = motion.Offset;

        bool changed = motion.ApplyPull(0f, deltaTime: 0.01f, maximumOffset: 3f);

        Assert.IsFalse(changed);
        Assert.AreEqual(previousOffset, motion.Offset, 0.0001f);
    }

    [TestMethod]
    public void LoweringTheHeightLimitConstrainsAnExistingOffset()
    {
        var motion = new VerticalMotionState();
        motion.Rebase(3f);

        bool changed = motion.Constrain(1f);

        Assert.IsTrue(changed);
        Assert.AreEqual(1f, motion.Offset, 0.0001f);
    }
}
