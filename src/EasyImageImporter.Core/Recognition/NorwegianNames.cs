namespace EasyImageImporter.Core.Recognition;

/// <summary>
/// Norwegian names for what SpeciesNet can answer in Norway: species, and the genera, families,
/// orders and classes it rolls up to when it isn't sure of the species. A taxon without a name
/// here gets its nearest named ancestor ("Spurvefugl", "Pattedyr"), never an English name.
/// Keys are scientific names; singular forms, since a name becomes a visit's label.
/// </summary>
public static class NorwegianNames
{
    public const string Empty = "Tomt bilde";
    public const string Unsure = "Usikker";

    public static string? For(string label)
    {
        switch (label)
        {
            case Taxonomy.Blank: return Empty;
            case Taxonomy.Animal: return "Dyr";
            case Taxonomy.Human: return "Menneske";
            case Taxonomy.Vehicle: return "Kjøretøy";
            case Taxonomy.Unknown: return Unsure;
        }

        var p = Taxonomy.Parts(label);
        // Most specific first: "genus species", genus, family, order, class.
        string[] keys = [p[4] != "" && p[5] != "" ? $"{p[4]} {p[5]}" : "", p[4], p[3], p[2], p[1]];
        foreach (var key in keys)
            if (key != "" && Names.TryGetValue(key, out var name)) return name;
        return null;
    }

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        // Classes
        ["aves"] = "Fugl", ["mammalia"] = "Pattedyr", ["reptilia"] = "Krypdyr", ["amphibia"] = "Amfibie",

        // Bird orders and families
        ["accipitriformes"] = "Rovfugl", ["accipitridae"] = "Rovfugl", ["falconiformes"] = "Falk", ["falconidae"] = "Falk",
        ["anseriformes"] = "Andefugl", ["anatidae"] = "Andefugl", ["charadriiformes"] = "Vade- eller måkefugl",
        ["laridae"] = "Måke", ["scolopacidae"] = "Snipe", ["charadriidae"] = "Lo", ["columbiformes"] = "Due",
        ["columbidae"] = "Due", ["cuculiformes"] = "Gjøk", ["cuculidae"] = "Gjøk", ["galliformes"] = "Hønsefugl",
        ["phasianidae"] = "Hønsefugl", ["gruiformes"] = "Tranefugl", ["gruidae"] = "Trane", ["rallidae"] = "Rikse",
        ["passeriformes"] = "Spurvefugl", ["corvidae"] = "Kråkefugl", ["paridae"] = "Meis", ["turdidae"] = "Trost",
        ["fringillidae"] = "Finkefugl", ["muscicapidae"] = "Fluesnapper", ["motacillidae"] = "Erle",
        ["hirundinidae"] = "Svale", ["sittidae"] = "Spettmeis", ["sturnidae"] = "Stær", ["passeridae"] = "Spurv",
        ["troglodytidae"] = "Gjerdesmett", ["alaudidae"] = "Lerke", ["laniidae"] = "Varsler", ["certhiidae"] = "Trekryper",
        ["sylviidae"] = "Sanger", ["acrocephalidae"] = "Sanger", ["pelecaniformes"] = "Hegre", ["ardeidae"] = "Hegre",
        ["piciformes"] = "Spett", ["picidae"] = "Spett", ["procellariiformes"] = "Stormfugl", ["strigiformes"] = "Ugle",
        ["strigidae"] = "Ugle", ["tytonidae"] = "Ugle", ["caprimulgiformes"] = "Nattravn",

        // Bird genera
        ["accipiter"] = "Hauk", ["aquila"] = "Ørn", ["haliaeetus"] = "Havørn", ["buteo"] = "Våk", ["circus"] = "Kjerrhauk",
        ["anas"] = "And", ["mareca"] = "And", ["spatula"] = "And", ["anser"] = "Gås", ["branta"] = "Gås", ["cygnus"] = "Svane",
        ["mergus"] = "Fiskand", ["larus"] = "Måke", ["gallinago"] = "Bekkasin", ["scolopax"] = "Rugde", ["tringa"] = "Snipe",
        ["columba"] = "Due", ["streptopelia"] = "Due", ["falco"] = "Falk", ["bonasa"] = "Jerpe", ["lagopus"] = "Rype",
        ["lyrurus"] = "Orrfugl", ["tetrao"] = "Storfugl", ["grus"] = "Trane", ["corvus"] = "Kråke eller ravn",
        ["garrulus"] = "Nøtteskrike", ["nucifraga"] = "Nøttekråke", ["perisoreus"] = "Lavskrike", ["pica"] = "Skjære",
        ["carduelis"] = "Stillits", ["fringilla"] = "Bokfink", ["pyrrhula"] = "Dompap", ["hirundo"] = "Svale",
        ["motacilla"] = "Erle", ["erithacus"] = "Rødstrupe", ["ficedula"] = "Fluesnapper", ["oenanthe"] = "Steinskvett",
        ["phoenicurus"] = "Rødstjert", ["saxicola"] = "Skvett", ["cyanistes"] = "Blåmeis", ["lophophanes"] = "Toppmeis",
        ["parus"] = "Kjøttmeis", ["periparus"] = "Svartmeis", ["poecile"] = "Meis", ["passer"] = "Spurv",
        ["sitta"] = "Spettmeis", ["sturnus"] = "Stær", ["sylvia"] = "Sanger", ["troglodytes"] = "Gjerdesmett",
        ["turdus"] = "Trost", ["ardea"] = "Hegre", ["dendrocopos"] = "Flaggspett", ["dryocopus"] = "Svartspett",
        ["picoides"] = "Tretåspett", ["picus"] = "Grønnspett", ["jynx"] = "Vendehals", ["asio"] = "Ugle", ["bubo"] = "Hubro",
        ["strix"] = "Ugle", ["tyto"] = "Tårnugle", ["otus"] = "Ugle",

        // Bird species
        ["accipiter gentilis"] = "Hønsehauk", ["accipiter nisus"] = "Spurvehauk", ["aquila chrysaetos"] = "Kongeørn",
        ["haliaeetus albicilla"] = "Havørn", ["buteo buteo"] = "Musvåk", ["buteo lagopus"] = "Fjellvåk",
        ["circus cyaneus"] = "Myrhauk", ["anas acuta"] = "Stjertand", ["anas platyrhynchos"] = "Stokkand",
        ["anas carolinensis"] = "Krikkand", ["anas crecca"] = "Krikkand", ["mareca strepera"] = "Snadderand",
        ["mergus merganser"] = "Laksand", ["branta canadensis"] = "Kanadagås", ["cygnus olor"] = "Knoppsvane",
        ["larus argentatus"] = "Gråmåke", ["larus canus"] = "Fiskemåke", ["larus fuscus"] = "Sildemåke",
        ["larus marinus"] = "Svartbak", ["larus ridibundus"] = "Hettemåke", ["scolopax rusticola"] = "Rugde",
        ["columba livia"] = "Klippedue", ["columba oenas"] = "Skogdue", ["columba palumbus"] = "Ringdue",
        ["streptopelia decaocto"] = "Tyrkerdue", ["falco peregrinus"] = "Vandrefalk", ["falco tinnunculus"] = "Tårnfalk",
        ["bonasa bonasia"] = "Jerpe", ["lagopus lagopus"] = "Lirype", ["lagopus muta"] = "Fjellrype",
        ["lyrurus tetrix"] = "Orrfugl", ["tetrao urogallus"] = "Storfugl", ["perdix perdix"] = "Rapphøne",
        ["phasianus colchicus"] = "Fasan", ["gallus gallus domesticus"] = "Høne", ["meleagris gallopavo"] = "Kalkun",
        ["grus grus"] = "Trane", ["gallinula chloropus"] = "Sivhøne", ["eremophila alpestris"] = "Fjellerke",
        ["corvus corax"] = "Ravn", ["corvus cornix"] = "Kråke", ["corvus frugilegus"] = "Kornkråke",
        ["corvus monedula"] = "Kaie", ["garrulus glandarius"] = "Nøtteskrike", ["nucifraga caryocatactes"] = "Nøttekråke",
        ["perisoreus infaustus"] = "Lavskrike", ["pica pica"] = "Skjære", ["carduelis carduelis"] = "Stillits",
        ["fringilla coelebs"] = "Bokfink", ["pyrrhula pyrrhula"] = "Dompap", ["hirundo rustica"] = "Låvesvale",
        ["motacilla cinerea"] = "Vintererle", ["motacilla flava"] = "Gulerle", ["erithacus rubecula"] = "Rødstrupe",
        ["ficedula hypoleuca"] = "Svarthvit fluesnapper", ["oenanthe oenanthe"] = "Steinskvett",
        ["phoenicurus ochruros"] = "Svartrødstjert", ["phoenicurus phoenicurus"] = "Rødstjert",
        ["lophophanes cristatus"] = "Toppmeis", ["parus major"] = "Kjøttmeis", ["parus minor"] = "Kjøttmeis",
        ["periparus ater"] = "Svartmeis", ["poecile montanus"] = "Granmeis", ["cyanistes caeruleus"] = "Blåmeis",
        ["passer domesticus"] = "Gråspurv", ["sitta europaea"] = "Spettmeis", ["sturnus vulgaris"] = "Stær",
        ["troglodytes troglodytes"] = "Gjerdesmett", ["turdus iliacus"] = "Rødvingetrost", ["turdus merula"] = "Svarttrost",
        ["turdus philomelos"] = "Måltrost", ["turdus pilaris"] = "Gråtrost", ["turdus torquatus"] = "Ringtrost",
        ["turdus viscivorus"] = "Duetrost", ["ardea cinerea"] = "Gråhegre", ["dendrocopos major"] = "Flaggspett",
        ["dryocopus martius"] = "Svartspett", ["jynx torquilla"] = "Vendehals", ["picus viridis"] = "Grønnspett",
        ["asio flammeus"] = "Jordugle", ["asio otus"] = "Hornugle", ["bubo bubo"] = "Hubro", ["strix aluco"] = "Kattugle",

        // Mammal orders and families
        ["carnivora"] = "Rovdyr", ["artiodactyla"] = "Klovdyr", ["cervidae"] = "Hjortedyr", ["bovidae"] = "Husdyr",
        ["suidae"] = "Svin", ["canidae"] = "Hundedyr", ["felidae"] = "Kattedyr", ["mustelidae"] = "Mårdyr",
        ["ursidae"] = "Bjørn", ["chiroptera"] = "Flaggermus", ["vespertilionidae"] = "Flaggermus",
        ["eulipotyphla"] = "Pinnsvin eller spissmus", ["erinaceidae"] = "Pinnsvin", ["soricidae"] = "Spissmus",
        ["lagomorpha"] = "Hare", ["leporidae"] = "Hare", ["perissodactyla"] = "Hest", ["equidae"] = "Hest",
        ["rodentia"] = "Gnager", ["sciuridae"] = "Ekorn", ["muridae"] = "Mus", ["cricetidae"] = "Smågnager",
        ["castoridae"] = "Bever", ["primates"] = "Primat", ["hominidae"] = "Menneske",

        // Mammal genera
        ["alces"] = "Elg", ["capreolus"] = "Rådyr", ["cervus"] = "Hjort", ["dama"] = "Dåhjort", ["rangifer"] = "Rein",
        ["sus"] = "Villsvin", ["canis"] = "Hund eller ulv", ["vulpes"] = "Rev", ["felis"] = "Katt", ["lynx"] = "Gaupe",
        ["gulo"] = "Jerv", ["lutra"] = "Oter", ["martes"] = "Mår", ["meles"] = "Grevling", ["mustela"] = "Røyskatt eller snømus",
        ["ursus"] = "Bjørn", ["eptesicus"] = "Flaggermus", ["myotis"] = "Flaggermus", ["nyctalus"] = "Flaggermus",
        ["plecotus"] = "Flaggermus", ["erinaceus"] = "Pinnsvin", ["lepus"] = "Hare", ["oryctolagus"] = "Kanin",
        ["equus"] = "Hest", ["castor"] = "Bever", ["myodes"] = "Markmus", ["apodemus"] = "Mus", ["mus"] = "Mus",
        ["rattus"] = "Rotte", ["sciurus"] = "Ekorn", ["bos"] = "Ku", ["capra"] = "Geit", ["ovis"] = "Sau", ["homo"] = "Menneske",

        // Mammal species
        ["alces alces"] = "Elg", ["capreolus capreolus"] = "Rådyr", ["cervus elaphus"] = "Hjort", ["dama dama"] = "Dåhjort",
        ["rangifer tarandus"] = "Rein", ["sus scrofa"] = "Villsvin", ["canis familiaris"] = "Hund", ["canis lupus"] = "Ulv",
        ["vulpes vulpes"] = "Rev", ["felis catus"] = "Katt", ["lynx lynx"] = "Gaupe", ["gulo gulo"] = "Jerv",
        ["lutra lutra"] = "Oter", ["martes martes"] = "Mår", ["meles meles"] = "Grevling", ["mustela erminea"] = "Røyskatt",
        ["mustela nivalis"] = "Snømus", ["mustela putorius"] = "Ilder", ["ursus arctos"] = "Bjørn",
        ["eptesicus nilssonii"] = "Nordflaggermus", ["erinaceus europaeus"] = "Pinnsvin", ["lepus timidus"] = "Hare",
        ["oryctolagus cuniculus"] = "Kanin", ["equus caballus"] = "Hest", ["castor fiber"] = "Bever",
        ["apodemus sylvaticus"] = "Skogmus", ["mus musculus"] = "Husmus", ["rattus norvegicus"] = "Brunrotte",
        ["sciurus vulgaris"] = "Ekorn", ["bos taurus"] = "Ku", ["ovis aries"] = "Sau", ["capra aegagrus"] = "Geit",
        ["homo sapiens"] = "Menneske",
    };
}
