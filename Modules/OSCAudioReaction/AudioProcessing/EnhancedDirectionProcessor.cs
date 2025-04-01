using System;
using System.Numerics;

namespace CrookedToe.Modules.OSCAudioReaction.AudioProcessing;

/// <summary>
/// Enhanced audio direction processor inspired by Steam Audio
/// </summary>
public class EnhancedDirectionProcessor
{
    // Constants for frequency weighting
    private const float DIRECTION_LOW_FREQ = 300f;
    private const float DIRECTION_HIGH_FREQ = 4000f;
    private const float MIN_PHASE_DIFF = 0f;
    private const float MAX_PHASE_DIFF = (float)Math.PI;

    // Frequency band weightings for direction detection
    // These weights prioritize frequencies where human ears are most sensitive to direction
    private readonly float[] _frequencyWeights = {
        0.2f,  // Sub Bass (20-60Hz) - Not very directional
        0.4f,  // Bass (60-250Hz) - Limited directionality
        0.7f,  // Low Mid (250-500Hz) - Moderate directionality
        1.0f,  // Mid (500Hz-2kHz) - Strong directionality
        0.9f,  // Upper Mid (2-4kHz) - Very good directionality
        0.6f,  // Presence (4-6kHz) - Good directionality
        0.3f   // Brilliance (6-20kHz) - Limited directionality at higher frequencies
    };

    // Configuration
    private readonly IAudioConfiguration _config;
    private float _magnitudeWeight = 0.7f;
    private float _phaseWeight = 0.3f;
    private bool _enablePhaseAnalysis = true;
    private float _directionBias = 0.5f; // Center bias (0.5)

    public EnhancedDirectionProcessor(IAudioConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Calculate enhanced audio direction using both magnitude and phase information
    /// </summary>
    public float CalculateDirection(Complex[] leftSpectrum, Complex[] rightSpectrum, float sampleRate, bool[] enabledBands)
    {
        if (leftSpectrum == null || rightSpectrum == null)
            return 0.5f;

        if (leftSpectrum.Length != rightSpectrum.Length)
            return 0.5f;

        int numBins = leftSpectrum.Length;
        if (numBins == 0)
            return 0.5f;

        float binWidth = sampleRate / (2f * (numBins - 1)); // Nyquist frequency / (N/2)
        
        // Initialize accumulators
        float totalPower = 0f;
        float weightedDirectionSum = 0f;
        float totalWeight = 0f;

        // Process each frequency band
        for (int band = 0; band < _frequencyWeights.Length; band++)
        {
            if (band < enabledBands.Length && !enabledBands[band])
                continue;

            // Get frequency range for this band
            var (startFreq, endFreq) = GetFrequencyRange(band);
            
            // Convert frequencies to bin indices
            int startBin = Math.Max(1, (int)Math.Floor(startFreq / binWidth));
            int endBin = Math.Min(numBins - 1, (int)Math.Ceiling(endFreq / binWidth));
            
            // Use our custom frequency weighting
            float bandWeight = _frequencyWeights[band];
            
            // Process each bin in this band
            for (int bin = startBin; bin <= endBin; bin++)
            {
                float binFreq = bin * binWidth;
                
                // Skip bins outside our band
                if (binFreq < startFreq || binFreq > endFreq)
                    continue;
                
                // Get complex values for left and right channels
                Complex left = leftSpectrum[bin];
                Complex right = rightSpectrum[bin];
                
                // Calculate bin weight based on frequency (emphasize mid-range frequencies)
                float frequencyFactor = CalculateFrequencyFactor(binFreq);
                float binWeight = bandWeight * frequencyFactor;
                
                // Calculate magnitude-based direction (traditional method)
                float leftMag = (float)left.Magnitude;
                float rightMag = (float)right.Magnitude;
                float totalMag = leftMag + rightMag;
                
                // Skip processing if magnitudes are too small
                if (totalMag < 1e-6f)
                    continue;
                
                // Magnitude-based direction (0 = left, 1 = right)
                float magDirection = rightMag / totalMag;
                
                // Calculate phase-based direction if enabled
                float phaseDirection = 0.5f;
                if (_enablePhaseAnalysis)
                {
                    // Calculate phase difference between channels (-π to π)
                    float leftPhase = (float)Math.Atan2(left.Imaginary, left.Real);
                    float rightPhase = (float)Math.Atan2(right.Imaginary, right.Real);
                    float phaseDiff = rightPhase - leftPhase;
                    
                    // Normalize phase difference to range -π to π
                    while (phaseDiff > Math.PI) phaseDiff -= 2 * (float)Math.PI;
                    while (phaseDiff < -Math.PI) phaseDiff += 2 * (float)Math.PI;
                    
                    // Convert phase difference to direction (0 = left, 1 = right)
                    // Positive phase difference means sound arrives at left ear first (source from right)
                    phaseDirection = (phaseDiff + (float)Math.PI) / (2 * (float)Math.PI);
                }
                
                // Combine magnitude and phase-based directions
                float combinedDirection = (_magnitudeWeight * magDirection) + 
                                          (_phaseWeight * phaseDirection);
                
                // Weight by the power of this bin
                float binPower = totalMag;
                weightedDirectionSum += combinedDirection * binPower * binWeight;
                totalWeight += binPower * binWeight;
                totalPower += binPower;
            }
        }
        
        // If no significant audio was detected, return center
        if (totalWeight < 1e-6f || totalPower < 1e-6f)
            return 0.5f;
        
        // Calculate weighted average direction
        float finalDirection = weightedDirectionSum / totalWeight;
        
        // Ensure the result is in the 0-1 range
        return Math.Clamp(finalDirection, 0f, 1f);
    }

    /// <summary>
    /// Calculate how important a specific frequency is for directional hearing
    /// </summary>
    private float CalculateFrequencyFactor(float frequency)
    {
        // Emphasize the frequency range most important for directional hearing
        if (frequency < DIRECTION_LOW_FREQ)
        {
            // Gradually increase importance from very low to low frequencies
            return Math.Max(0.2f, frequency / DIRECTION_LOW_FREQ);
        }
        else if (frequency > DIRECTION_HIGH_FREQ)
        {
            // Gradually decrease importance for very high frequencies
            return Math.Max(0.2f, 1.0f - ((frequency - DIRECTION_HIGH_FREQ) / (15000f - DIRECTION_HIGH_FREQ)));
        }
        else
        {
            // Maximum importance in the middle range (biased toward 1-3kHz, the most directional range)
            float midPoint = 2000f;
            float normalizedDist = Math.Abs((frequency - midPoint) / 1500f);
            return 1.0f - (0.2f * normalizedDist);
        }
    }

    private (float startFreq, float endFreq) GetFrequencyRange(int band)
    {
        // Define frequency ranges for each band (in Hz)
        switch (band)
        {
            case 0: return (20, 60);     // Sub Bass (20-60 Hz)
            case 1: return (60, 250);    // Bass (60-250 Hz)
            case 2: return (250, 500);   // Low Mids (250-500 Hz)
            case 3: return (500, 2000);  // Mids (500-2kHz)
            case 4: return (2000, 4000); // Upper Mids (2-4kHz)
            case 5: return (4000, 6000); // Presence (4-6kHz)
            case 6: return (6000, 25000);// Brilliance (6-25kHz)
            default: return (0, 0);
        }
    }

    #region Configuration Methods

    public void SetMagnitudeWeight(float weight)
    {
        _magnitudeWeight = Math.Clamp(weight, 0.1f, 1.0f);
        _phaseWeight = 1.0f - _magnitudeWeight;
    }

    public void SetPhaseAnalysisEnabled(bool enabled)
    {
        _enablePhaseAnalysis = enabled;
        if (!enabled)
        {
            _magnitudeWeight = 1.0f;
            _phaseWeight = 0.0f;
        }
        else if (Math.Abs(_magnitudeWeight - 1.0f) < 0.001f)
        {
            // Reset to default weights if previously disabled
            _magnitudeWeight = 0.7f;
            _phaseWeight = 0.3f;
        }
    }

    public void SetDirectionBias(float bias)
    {
        _directionBias = Math.Clamp(bias, 0.0f, 1.0f);
    }

    public void SetFrequencyWeights(float[] weights)
    {
        if (weights == null || weights.Length != _frequencyWeights.Length)
            return;
            
        for (int i = 0; i < _frequencyWeights.Length; i++)
        {
            _frequencyWeights[i] = Math.Clamp(weights[i], 0.0f, 1.0f);
        }
    }

    #endregion
} 