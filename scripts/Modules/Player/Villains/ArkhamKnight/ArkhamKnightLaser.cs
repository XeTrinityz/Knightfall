#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// The Arkham Knight's own laser, the beam his rifle throws in the cutscene before the Joker's last
// act. It is a long sprite held along its line and drawn out from where it is placed. The cutscene
// sets its length through a size parameter. That parameter, how it maps onto the sprite's size and
// the sprite's own size are read from the effect, so the beam can be stretched to any length.
static partial class ArkhamKnight
{
    static FName _lengthParameter = FName.None;
    static Vector3 _baseSize;
    static DistributionVectorParticleParameter? _lengthMapping;

    static void LearnLaser()
    {
        ForgetLaser();
        if (_sightLook is not { IsValid: true } look)
        {
            return;
        }

        var emitters = look.Emitters;
        for (var e = 0; e < emitters.Count && _lengthMapping is null; e++)
        {
            if (emitters[e] is not { IsValid: true } emitter || emitter.LODLevels.Count == 0 || emitter.LODLevels[0] is not { IsValid: true } level)
            {
                continue;
            }

            var size = Vector3.Zero;
            DistributionVectorParticleParameter? scaled = null;
            var modules = level.Modules;
            for (var m = 0; m < modules.Count; m++)
            {
                switch (modules[m])
                {
                    case ParticleModuleSizeScale { IsValid: true } scale when scale.SizeScale.Distribution is DistributionVectorParticleParameter { IsValid: true } parameter:
                        scaled = parameter;
                        break;
                    case ParticleModuleSize { IsValid: true } sized:
                        size = Base(ref sized.StartSize);
                        break;
                }
            }

            if (scaled is not null)
            {
                _lengthMapping = scaled;
                _lengthParameter = scaled.ParameterName;
                _baseSize = size;
            }
        }

        if (_lengthMapping is null || _baseSize.X <= 0f)
        {
            Log.Warn("arkham_knight.beam", "The Arkham Knight's laser can't be stretched, so it keeps the length it was made with.");
        }
    }

    // A size's first value. Cooked sizes keep only their table: two range values, then the
    // values themselves.
    static Vector3 Base(ref DistributionVector.FRawDistributionVector size)
    {
        switch (size.Distribution)
        {
            case DistributionVectorConstant { IsValid: true } constant:
                return constant.Constant;
            case DistributionVectorUniform { IsValid: true } uniform:
                return uniform.Max;
        }

        var table = size.LookupTable;
        return table.Count >= 5 ? new Vector3(table[2], table[3], table[4]) : Vector3.Zero;
    }

    // Stretches the beam's length, its first size, to the given length, through the parameter's
    // own mapping onto the size scale.
    static void Stretch(ParticleSystemComponent beam, float length)
    {
        if (_lengthMapping is not { IsValid: true } mapping || _baseSize.X <= 0f)
        {
            return;
        }

        // Only the length changes. The width keeps a scale of one.
        var value = new Vector3(
            Unmap(length / _baseSize.X, mapping.ParamModes_0, mapping.MinInput.X, mapping.MaxInput.X, mapping.MinOutput.X, mapping.MaxOutput.X),
            Unmap(1f, mapping.ParamModes_1, mapping.MinInput.Y, mapping.MaxInput.Y, mapping.MinOutput.Y, mapping.MaxOutput.Y),
            Unmap(1f, mapping.ParamModes_2, mapping.MinInput.Z, mapping.MaxInput.Z, mapping.MinOutput.Z, mapping.MaxOutput.Z));
        beam.SetVectorParameter(_lengthParameter, value);
    }

    // The input a parameter's mapping turns into the wanted output. A direct one passes it as is.
    static float Unmap(float wanted, DistributionFloatParameterBase.DistributionParamMode mode, float minIn, float maxIn, float minOut, float maxOut)
    {
        if (mode == DistributionFloatParameterBase.DistributionParamMode.DPM_Direct)
        {
            return wanted;
        }

        var across = maxOut - minOut;
        return MathF.Abs(across) < 1e-6f ? minIn : minIn + (wanted - minOut) / across * (maxIn - minIn);
    }

    static void ForgetLaser()
    {
        _lengthMapping = null;
        _lengthParameter = FName.None;
        _baseSize = Vector3.Zero;
    }
}
