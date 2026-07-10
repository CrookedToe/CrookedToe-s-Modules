using Valve.VR;

namespace CrookedToe.Modules.OSCLeash;

internal enum PoseUpdateResult
{
    Success,
    NoChange,
    ExternalWriterActive,
    OpenVrUnavailable,
    ReadFailed,
    WriteFailed
}

internal readonly record struct OpenVrPoseSnapshot(
    bool Connected,
    bool OwnsPose,
    float ReferenceHeight,
    float LastAppliedOffset,
    int ExternalConflictCount,
    PoseUpdateResult LastResult);

internal static class StandingPoseMath
{
    private const float RotationEpsilon = 0.0001f;
    private const float TranslationEpsilonMeters = 0.0005f;

    public static HmdMatrix34_t ApplyVerticalOffset(HmdMatrix34_t pose, float verticalOffset)
    {
        pose.m3 += pose.m1 * verticalOffset;
        pose.m7 += pose.m5 * verticalOffset;
        pose.m11 += pose.m9 * verticalOffset;
        return pose;
    }

    public static bool ApproximatelyEquals(HmdMatrix34_t left, HmdMatrix34_t right)
    {
        return Near(left.m0, right.m0, RotationEpsilon) &&
               Near(left.m1, right.m1, RotationEpsilon) &&
               Near(left.m2, right.m2, RotationEpsilon) &&
               Near(left.m3, right.m3, TranslationEpsilonMeters) &&
               Near(left.m4, right.m4, RotationEpsilon) &&
               Near(left.m5, right.m5, RotationEpsilon) &&
               Near(left.m6, right.m6, RotationEpsilon) &&
               Near(left.m7, right.m7, TranslationEpsilonMeters) &&
               Near(left.m8, right.m8, RotationEpsilon) &&
               Near(left.m9, right.m9, RotationEpsilon) &&
               Near(left.m10, right.m10, RotationEpsilon) &&
               Near(left.m11, right.m11, TranslationEpsilonMeters);
    }

    private static bool Near(float left, float right, float epsilon)
        => float.IsFinite(left) && float.IsFinite(right) && MathF.Abs(left - right) <= epsilon;
}

internal sealed class PoseOwnershipTracker
{
    private HmdMatrix34_t _baselinePose;
    private HmdMatrix34_t _lastWrittenPose;

    public bool HasBaseline { get; private set; }
    public bool OwnsPose { get; private set; }
    public float LastAppliedOffset { get; private set; }
    public float ReferenceHeight => HasBaseline ? _baselinePose.m7 : 0f;

    public void CaptureBaseline(HmdMatrix34_t pose)
    {
        _baselinePose = pose;
        _lastWrittenPose = pose;
        HasBaseline = true;
        OwnsPose = false;
        LastAppliedOffset = 0f;
    }

    public PoseUpdateResult TryCompose(HmdMatrix34_t livePose, float targetOffset, out HmdMatrix34_t poseToWrite)
    {
        poseToWrite = livePose;
        if (!HasBaseline)
            return PoseUpdateResult.ReadFailed;

        HmdMatrix34_t expectedLivePose = OwnsPose ? _lastWrittenPose : _baselinePose;
        if (!StandingPoseMath.ApproximatelyEquals(livePose, expectedLivePose))
            return PoseUpdateResult.ExternalWriterActive;

        poseToWrite = StandingPoseMath.ApplyVerticalOffset(_baselinePose, targetOffset);
        return PoseUpdateResult.Success;
    }

    public PoseUpdateResult TryBuildRestore(HmdMatrix34_t livePose, out HmdMatrix34_t poseToWrite)
    {
        poseToWrite = livePose;
        if (!OwnsPose)
            return PoseUpdateResult.NoChange;
        if (!StandingPoseMath.ApproximatelyEquals(livePose, _lastWrittenPose))
            return PoseUpdateResult.ExternalWriterActive;

        poseToWrite = _baselinePose;
        return PoseUpdateResult.Success;
    }

    public void AcceptWrite(HmdMatrix34_t pose, float appliedOffset)
    {
        _lastWrittenPose = pose;
        OwnsPose = true;
        LastAppliedOffset = appliedOffset;
    }

    public void ReleaseAtBaseline()
    {
        _lastWrittenPose = _baselinePose;
        OwnsPose = false;
        LastAppliedOffset = 0f;
    }

    public void YieldToExternalPose(HmdMatrix34_t livePose) => CaptureBaseline(livePose);

    public bool LivePoseMatchesLastWrite(HmdMatrix34_t livePose)
        => OwnsPose && StandingPoseMath.ApproximatelyEquals(livePose, _lastWrittenPose);

    public bool LivePoseMatchesBaseline(HmdMatrix34_t livePose)
        => HasBaseline && StandingPoseMath.ApproximatelyEquals(livePose, _baselinePose);

    public void Clear()
    {
        _baselinePose = new HmdMatrix34_t();
        _lastWrittenPose = new HmdMatrix34_t();
        HasBaseline = false;
        OwnsPose = false;
        LastAppliedOffset = 0f;
    }
}

internal sealed class OpenVrPoseCoordinator
{
    private readonly PoseOwnershipTracker _ownership = new();
    private bool _connected;
    private int _externalConflictCount;
    private PoseUpdateResult _lastResult = PoseUpdateResult.NoChange;

    public bool Connected => _connected;
    public bool OwnsPose => _ownership.OwnsPose;
    public float ReferenceHeight => _ownership.ReferenceHeight;
    public float LastAppliedOffset => _ownership.LastAppliedOffset;

    public PoseUpdateResult TryConnect()
    {
        PoseUpdateResult readResult = TryRead(out HmdMatrix34_t livePose);
        if (readResult != PoseUpdateResult.Success)
            return SetResult(readResult);

        _connected = true;
        if (_ownership.OwnsPose)
        {
            if (!_ownership.LivePoseMatchesLastWrite(livePose))
            {
                _externalConflictCount++;
                _ownership.YieldToExternalPose(livePose);
                return SetResult(PoseUpdateResult.ExternalWriterActive);
            }

            return SetResult(PoseUpdateResult.Success);
        }

        _ownership.CaptureBaseline(livePose);
        return SetResult(PoseUpdateResult.Success);
    }

    public PoseUpdateResult RefreshBaseline()
    {
        PoseUpdateResult readResult = TryRead(out HmdMatrix34_t livePose);
        if (readResult != PoseUpdateResult.Success)
            return SetResult(readResult);

        _connected = true;
        _ownership.CaptureBaseline(livePose);
        return SetResult(PoseUpdateResult.Success);
    }

    public PoseUpdateResult ApplyOffset(float targetOffset)
    {
        PoseUpdateResult readResult = TryRead(out HmdMatrix34_t livePose);
        if (readResult != PoseUpdateResult.Success)
            return SetResult(readResult);

        _connected = true;
        PoseUpdateResult compositionResult = _ownership.TryCompose(livePose, targetOffset, out HmdMatrix34_t poseToWrite);
        if (compositionResult == PoseUpdateResult.ExternalWriterActive)
        {
            _externalConflictCount++;
            _ownership.YieldToExternalPose(livePose);
            return SetResult(compositionResult);
        }
        if (compositionResult != PoseUpdateResult.Success)
            return SetResult(compositionResult);

        return PreviewPose(poseToWrite, targetOffset);
    }

    public PoseUpdateResult RemoveOwnOffset()
    {
        if (!_ownership.OwnsPose)
            return SetResult(PoseUpdateResult.NoChange);

        PoseUpdateResult readResult = TryRead(out HmdMatrix34_t livePose);
        if (readResult != PoseUpdateResult.Success)
            return SetResult(readResult);

        PoseUpdateResult restoreResult = _ownership.TryBuildRestore(livePose, out HmdMatrix34_t poseToWrite);
        if (restoreResult == PoseUpdateResult.ExternalWriterActive)
        {
            _externalConflictCount++;
            _ownership.YieldToExternalPose(livePose);
            return SetResult(restoreResult);
        }
        if (restoreResult != PoseUpdateResult.Success)
            return SetResult(restoreResult);

        PoseUpdateResult writeResult = PreviewPose(poseToWrite, 0f);
        if (writeResult == PoseUpdateResult.Success)
            _ownership.ReleaseAtBaseline();
        return writeResult;
    }

    public PoseUpdateResult ObserveExternalPose(out bool changed)
    {
        changed = false;
        PoseUpdateResult readResult = TryRead(out HmdMatrix34_t livePose);
        if (readResult != PoseUpdateResult.Success)
            return SetResult(readResult);

        _connected = true;
        if (_ownership.LivePoseMatchesBaseline(livePose))
            return SetResult(PoseUpdateResult.Success);

        changed = true;
        _ownership.CaptureBaseline(livePose);
        return SetResult(PoseUpdateResult.ExternalWriterActive);
    }

    public void ReleaseZeroOffsetOwnership()
    {
        if (_ownership.OwnsPose && MathF.Abs(_ownership.LastAppliedOffset) <= LeashDefaults.NormalizeEpsilon)
            _ownership.ReleaseAtBaseline();
    }

    public void Disconnect()
    {
        _connected = false;
        if (!_ownership.OwnsPose)
            _ownership.Clear();
    }

    public void Clear()
    {
        _connected = false;
        _externalConflictCount = 0;
        _lastResult = PoseUpdateResult.NoChange;
        _ownership.Clear();
    }

    public OpenVrPoseSnapshot Snapshot()
        => new(_connected, _ownership.OwnsPose, _ownership.ReferenceHeight, _ownership.LastAppliedOffset, _externalConflictCount, _lastResult);

    private PoseUpdateResult PreviewPose(HmdMatrix34_t pose, float appliedOffset)
    {
        try
        {
            CVRChaperoneSetup? setup = OpenVR.ChaperoneSetup;
            if (setup is null)
            {
                _connected = false;
                return SetResult(PoseUpdateResult.OpenVrUnavailable);
            }

            setup.SetWorkingStandingZeroPoseToRawTrackingPose(ref pose);
            _ownership.AcceptWrite(pose, appliedOffset);
            setup.ShowWorkingSetPreview();
            return SetResult(PoseUpdateResult.Success);
        }
        catch
        {
            _connected = false;
            return SetResult(PoseUpdateResult.WriteFailed);
        }
    }

    private PoseUpdateResult TryRead(out HmdMatrix34_t pose)
    {
        pose = new HmdMatrix34_t();
        try
        {
            CVRChaperoneSetup? setup = OpenVR.ChaperoneSetup;
            if (setup is null)
            {
                _connected = false;
                return PoseUpdateResult.OpenVrUnavailable;
            }

            if (setup.GetWorkingStandingZeroPoseToRawTrackingPose(ref pose))
                return PoseUpdateResult.Success;

            _connected = false;
            return PoseUpdateResult.ReadFailed;
        }
        catch
        {
            _connected = false;
            return PoseUpdateResult.ReadFailed;
        }
    }

    private PoseUpdateResult SetResult(PoseUpdateResult result)
    {
        _lastResult = result;
        return result;
    }
}
