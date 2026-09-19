using System.Text.Json;

namespace EasyImageImporter.Core.Recognition;

/// <summary>
/// SpeciesNet's labels ("uuid;class;order;family;genus;species;common name"), the full taxonomy
/// used to roll a label up to its genus, family, …, and which taxa are not found in Norway.
/// Loaded from norway.json, which tools/models/convert.py writes next to the models.
/// </summary>
public sealed class Taxonomy
{
    public const string Blank = "f1856211-cfb7-4a5b-9158-c0f72fd09ee6;;;;;;blank";
    public const string Animal = "1f689929-883d-4dae-958c-3d57ab5b6c16;;;;;;animal";
    public const string Human = "990ae9dd-7a59-4344-afcb-1b7b21368000;mammalia;primates;hominidae;homo;sapiens;human";
    public const string Vehicle = "e2895ed5-780b-48f6-8a11-9e27cb594511;;;;;;vehicle";
    public const string Unknown = "f2efdae9-efb8-48fb-8a91-eccf79ab4ffb;no cv result;no cv result;no cv result;no cv result;no cv result;no cv result";

    public static readonly string[] Levels = ["species", "genus", "family", "order", "class", "kingdom"];

    private readonly Dictionary<string, string> _byKey;
    private readonly HashSet<string> _blocked;

    private Taxonomy(string[] labels, IEnumerable<string> taxonomy, IEnumerable<string> blocked)
    {
        Labels = labels;
        _byKey = taxonomy.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        _blocked = blocked.ToHashSet();
    }

    /// <summary>Classifier output index → label.</summary>
    public IReadOnlyList<string> Labels { get; }

    public static Taxonomy Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        string[] Strings(string name) => root.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();
        return new Taxonomy(Strings("labels"), Strings("taxonomy"), Strings("blocked"));
    }

    /// <summary>"class;order;family;genus;species", the key SpeciesNet's taxonomy and geofence use.</summary>
    public static string Key(string label) => string.Join(';', Parts(label)[1..6]);

    public static string[] Parts(string label)
    {
        var parts = label.Split(';');
        if (parts.Length != 7) throw new ArgumentException($"Expected 7 parts: {label}");
        return parts;
    }

    public static string CommonName(string label) => label[(label.LastIndexOf(';') + 1)..];

    /// <summary>The label one taxonomy level up (e.g. the family of a species), or null.</summary>
    public string? Ancestor(string label, string level)
    {
        var p = Parts(label);
        string[] key;
        switch (level)
        {
            case "species": if (p[5] == "") return null; key = p[1..6]; break;
            case "genus": if (p[4] == "") return null; key = [.. p[1..5], ""]; break;
            case "family": if (p[3] == "") return null; key = [.. p[1..4], "", ""]; break;
            case "order": if (p[2] == "") return null; key = [.. p[1..3], "", "", ""]; break;
            case "class": if (p[1] == "") return null; key = [p[1], "", "", "", ""]; break;
            case "kingdom": if (p[1] == "" && label != Animal) return null; key = ["", "", "", "", ""]; break;
            default: return null;
        }
        return _byKey.GetValueOrDefault(string.Join(';', key));
    }

    /// <summary>True if this taxon is not found in Norway (SpeciesNet's geofence).</summary>
    public bool IsBlocked(string label) => _blocked.Contains(Key(label));
}
