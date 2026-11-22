namespace PPlusTracklistEditor.Core.Stages;

/// <summary>
/// What the hazard switch does on the stages where Project+ documents it (from the community "Project+ Complete
/// Stagelist", written for v3.1.5). Keyed by stage slot ID. Each note describes the stage's main version; its
/// alternate versions (L/R/Z) may differ.
/// </summary>
public static class StageHazards
{
    private static readonly Dictionary<int, string> Notes = new()
    {
        [0x0D] = "On: ghost platforms appear at the sides now and then. Off: they never appear.",                    // Yoshi's Island
        [0x1F] = "On: the side platforms rise and fall at random and can sink underwater. Off: they move together and never sink.", // Fountain of Dreams
        [0x21] = "On: the moving platform travels all the way off the stage. Off: it only goes halfway.",            // Smashville
        [0x23] = "On: the platform swings high and slowly. Off: it swings lower and faster.",                         // Green Hill Zone
        [0x2D] = "On: Whispy Woods blows wind that pushes players. Off: no wind.",                                    // Dream Land
        [0x44] = "On: the four side platforms shift sideways now and then. Off: they stay put.",                      // Dead Line
    };

    /// <summary>A short "On: ... Off: ..." note for the stage, or null when none is documented.</summary>
    public static string? NoteFor(int slotId) => Notes.GetValueOrDefault(slotId);
}
