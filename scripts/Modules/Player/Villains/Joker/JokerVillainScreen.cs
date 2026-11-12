#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// What the game's own Joker sees. His gun puts the game's shotgun crosshair in the middle of the
// screen every frame it is up, and kicks it when he fires. With him on, the game's HUD takes the
// purple tint it has while he is played in the story.
//
// Detective mode looks as Harley Quinn's does in her pack, her Psychosis view. Batman's X-ray
// views stay as they are, and only the material their looks are made from is changed.
static partial class JokerVillain
{
    // The game's own names for the crosshair and its kick.
    const string Cursor = "main";
    const string CursorLook = "shotgun";
    const string CursorKick = "Fire";

    // The HUD's whole tint, which reaches every one of its movies, not just the main one. The
    // SDK only has the main movie's.
    const string TintPath = "BmGame.RGFxMovieModularHud.SetColorTransform";

    // Harley's Psychosis view, and the one that plays as it fades off.
    const string PsychosisPath = "postprocess_level_wbgm.BatXray.pp_Psychosis_INST";
    const string PsychosisOffPath = "postprocess_level_wbgm.Harley.pp_PsychosisTransition_mi";

    static readonly Held<MaterialInstance, MaterialInterface?> ViewLook = new(
        "joker xray look", look => look.Parent, (look, parent) => look.SetParent(parent!));

    static MaterialInterface? _psychosis;
    static MaterialInterface? _psychosisOff;

    // The tint the HUD had before, to put back. An untinted HUD reads all zeros, which as a tint
    // would turn it black, so that is put back as no tint at all.
    static readonly float[] JokerTint = { 64f, 0f, 128f, 0.6f, 0.5f, 0.7f };
    static RGFxMovieModularHud? _tinted;
    static float[] _untinted = Array.Empty<float>();

    // Both crosshair calls are script functions given kept strings. Their script and what it
    // calls only read and copy the strings and never assign to them, so the kept ones are safe.
    static void Crosshair(RPlayerController controller)
    {
        if (!Aim.On || Arms.Raised < Drawn || controller.ModularHud is not { IsValid: true } hud || hud.TargetsModule is not { IsValid: true } targets)
        {
            return;
        }

        targets.TargetCursorCentreScreen(GameText.Of(Cursor), GameText.Of(CursorLook), 0, -1, -1, GameText.Of(""), false, -1f);
    }

    static void KickCrosshair(RPlayerController controller)
    {
        if (controller.ModularHud is { IsValid: true } hud && hud.TargetsModule is { IsValid: true } targets)
        {
            targets.PlaySubAnimOnCursor3D(GameText.Of(Cursor), GameText.Of(CursorKick));
        }
    }

    static void Tint(RPlayerController controller)
    {
        if (controller.ModularHud is not { IsValid: true } hud)
        {
            return;
        }

        if (_tinted is null || _tinted.Ptr != hud.Ptr)
        {
            _tinted = hud;
            _untinted = new[] { hud.ColTrans_RedOffset, hud.ColTrans_GreenOffset, hud.ColTrans_BlueOffset, hud.ColTrans_RedScalar, hud.ColTrans_GreenScalar, hud.ColTrans_BlueScalar };
            // After a hot reload the HUD can still hold his own tint, which is no tint to go back to.
            if ((_untinted[3] == 0f && _untinted[4] == 0f && _untinted[5] == 0f) || _untinted.AsSpan().SequenceEqual(JokerTint))
            {
                _untinted = new[] { 0f, 0f, 0f, 1f, 1f, 1f };
            }
        }

        // The game's own call skips the work when the tint is already set.
        hud.SetJokerColorTransform();
    }

    static void LoadXRay()
    {
        _psychosis = Kept(Game.FindObject<MaterialInterface>(PsychosisPath));
        _psychosisOff = Kept(Game.FindObject<MaterialInterface>(PsychosisOffPath));
    }

    // The view as he looks with it on, and the one that plays as he turns it off.
    static void XRay(RPlayerController controller)
    {
        if (_psychosis is not { IsValid: true } || controller.CurrentForensicsDevice is not { IsValid: true } device)
        {
            return;
        }

        var views = device.ViewPP;
        for (var i = 0; i < views.Count; i++)
        {
            switch (views[i])
            {
                case RXrayOffViewPostProcess { ViewM: { IsValid: true } off } when _psychosisOff is { IsValid: true } fading:
                    ViewLook.Hold(off, fading);
                    break;
                case RXRayViewPostProcess { ViewM: { IsValid: true } on }:
                    ViewLook.Hold(on, _psychosis);
                    break;
            }
        }
    }

    // The city's billboards wear his face while he is about, and go back when he leaves.
    static void City()
    {
        if (JokerfyCity.On)
        {
            Joker.KeepJokerfied();
        }
        else
        {
            Joker.PutBackUnlessKept();
        }
    }

    static void Untint()
    {
        if (_tinted is { IsValid: true } hud && _untinted.Length == 6)
        {
            ScriptCall.Run(hud, TintPath, _untinted.Select(BitConverter.SingleToInt32Bits).ToArray());
        }

        _tinted = null;
    }
}
