namespace EasyImageImporter.Core.Naming;

/// <summary>
/// Suggestions for the animal label, before anything has been typed: common visitors to trail
/// cameras in Norway. Whatever the user types later is suggested too.
/// </summary>
public static class Species
{
    public static readonly IReadOnlyList<string> Common =
    [
        "Kongeørn", "Havørn", "Hønsehauk", "Musvåk", "Ravn", "Kråke", "Nøtteskrike", "Lavskrike", "Skjære",
        "Kjøttmeis", "Grønnspett", "Svartspett", "Storfugl", "Orrfugl", "Jerpe", "Ugle",
        "Rev", "Grevling", "Mår", "Røyskatt", "Mink", "Oter", "Gaupe", "Jerv", "Bjørn", "Ulv",
        "Elg", "Rådyr", "Hjort", "Rein", "Villsvin", "Hare", "Ekorn", "Mus", "Katt", "Hund", "Menneske",
    ];
}
