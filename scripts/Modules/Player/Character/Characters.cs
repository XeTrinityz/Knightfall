#nullable enable

namespace Knightfall;

// PLAYER > CHARACTER, the SWITCH rows.
static class Characters
{
    const string Batman = "Playable_Batman";

    // Robin, Nightwing and Catwoman each come twice. The names ending in 0 are the complete ones
    // the challenge maps use. The others are the story's fight partners.
    static readonly (string Name, string Label)[] Known =
    {
        (Batman, "BATMAN"),
        ("Playable_Robin0", "ROBIN"),
        ("Playable_Nightwing0", "NIGHTWING"),
        ("Playable_Catwoman0", "CATWOMAN"),
        ("Playable_Azrael", "AZRAEL"),
        ("Playable_DLCBatgirl", "BATGIRL"),
        ("Playable_DLCRedHood", "RED HOOD"),
        ("Playable_DLCHarleyQuinn", "HARLEY QUINN"),
        ("Playable_BruceWayne", "BRUCE WAYNE"),
        ("Playable_Gordon", "GORDON"),
    };

    public static readonly Choice PlayAs = new(
        "player.character.play_as", "PLAY AS",
        "Who SWITCH CHARACTER turns you into. Only the characters you have installed are listed.",
        Installed())
    {
        Preference = true,
    };

    public static readonly Command Switch = new(
        "player.character.switch", "SWITCH CHARACTER",
        "Turns you into the picked character right where you stand, with the same health and speed. Your save still records the story's character, so it loads as normal.",
        () => Switcher.To(PlayAs.CurrentId, Switch!))
    {
        Block = Car.InCar,
    };

    public static readonly Command Back = new(
        "player.character.back", "BACK TO STORY CHARACTER",
        "Turns you back into whoever the story has you playing. A reload does the same.",
        () => Switcher.Back(Back!))
    {
        Block = Car.InCar,
    };

    // Batman's data ships inside the game's own packages, so he has no file to look for.
    public static (string Id, string Label)[] Installed()
    {
        var list = new List<(string Id, string Label)>();
        foreach (var (name, label) in Known)
        {
            if (name == Batman || GameFiles.HasPackage(name))
            {
                list.Add((name, label));
            }
        }

        return list.ToArray();
    }
}
