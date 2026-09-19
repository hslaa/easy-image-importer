using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.Naming;
using EasyImageImporter.Core.Review;

namespace EasyImageImporter.App.ViewModels.Review;

/// <summary>
/// "Navn og tagger": one card per place, just before saving. Every field is stored as it is typed,
/// so nothing is lost if the app is closed here.
/// </summary>
public sealed partial class NamingScreen : Screen
{
    public override int Step => FlowStep.Naming;

    private readonly Func<Task> _save;

    public NamingScreen(IReadOnlyList<PlaceForm> places, Action back, Func<Task> save)
    {
        _save = save;
        Places = places;
        BackCommand = new RelayCommand(back);
        foreach (var place in places) place.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    public IReadOnlyList<PlaceForm> Places { get; }
    public IRelayCommand BackCommand { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _canSave;

    [ObservableProperty] private string? _missingText;

    private void Refresh()
    {
        var missing = Places.Count(p => string.IsNullOrWhiteSpace(p.Title));
        CanSave = missing == 0;
        MissingText = missing switch
        {
            0 => null,
            1 when Places.Count == 1 => "Gi stedet et navn før du lagrer.",
            1 => "Ett sted mangler navn.",
            _ => $"{missing} steder mangler navn.",
        };
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task Save() => _save();
}

public sealed partial class PlaceForm : ObservableObject
{
    private readonly Place _place;
    private readonly ImportStore _store;

    public PlaceForm(Place place, int number, IReadOnlyList<LazyThumbnail> covers, ImportStore store,
        IReadOnlyList<string> tagSuggestions)
    {
        _place = place;
        _store = store;
        Heading = $"Sted {number}";
        Details = $"{Rows.DateRangeText(place.Start, place.End)} · {place.Visits.Count:N0} " +
                  $"{(place.Visits.Count == 1 ? "hendelse" : "hendelser")}, {Rows.Count(place.ImageCount)}";
        Covers = covers;
        RecognisedText = place.Details.RecognisedName is { } known ? $"Dette ser ut som {known}." : null;
        AnimalsText = place.Animals.Count > 0
            ? string.Join(", ", place.Animals)
            : "Ingen dyr er merket ennå. Skriv hva som er på bildene i hver hendelse, eller svar «Ja» på forslagene.";
        TagSuggestions = tagSuggestions;
        Tags = new ObservableCollection<TagChip>(place.Details.Tags.Select(t => new TagChip(t, RemoveTag)));
        _title = place.Details.Title ?? "";
        _description = place.Details.Description ?? "";
        _customFolder = place.Details.FolderName;
    }

    public string Heading { get; }
    public string Details { get; }
    public IReadOnlyList<LazyThumbnail> Covers { get; }
    public string? RecognisedText { get; }
    public string AnimalsText { get; }
    public IReadOnlyList<string> TagSuggestions { get; }
    public ObservableCollection<TagChip> Tags { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderName), nameof(FolderPreview), nameof(IsCustomFolder))]
    private string _title;

    [ObservableProperty] private string _description;
    [ObservableProperty] private string _newTag = "";

    private string? _customFolder;

    partial void OnTitleChanged(string value) => _store.SetPlaceTitle(_place.Id, value);
    partial void OnDescriptionChanged(string value) => _store.SetPlaceDescription(_place.Id, value);

    private string Suggestion => Names.FolderSuggestion(
        string.IsNullOrWhiteSpace(Title) ? _place.Details.DefaultName : Title, _place.Start, _place.End, _place.Animals);

    /// <summary>The suggestion follows the name until the user types a folder name of their own.</summary>
    public string FolderName
    {
        get => _customFolder ?? Suggestion;
        set
        {
            var custom = string.IsNullOrWhiteSpace(value) || value.Trim() == Suggestion ? null : value.Trim();
            if (custom == _customFolder) return;
            _customFolder = custom;
            _store.SetPlaceFolderName(_place.Id, custom);
            OnPropertyChanged();
            OnPropertyChanged(nameof(FolderPreview));
            OnPropertyChanged(nameof(IsCustomFolder));
        }
    }

    public bool IsCustomFolder => _customFolder is not null;

    public string FolderPreview =>
        $"Bilder › Viltkamera › {(_place.Start.Year >= 2010 ? _place.Start.Year : DateTime.Now.Year)} › {Names.Clean(FolderName, 120)}";

    [RelayCommand]
    private void UseSuggestedFolder() => FolderName = Suggestion;

    [RelayCommand]
    private void AddTag()
    {
        foreach (var tag in NewTag.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!Tags.Any(t => string.Equals(t.Text, tag, StringComparison.OrdinalIgnoreCase)))
                Tags.Add(new TagChip(tag, RemoveTag));
        NewTag = "";
        SaveTags();
    }

    private void RemoveTag(TagChip chip)
    {
        Tags.Remove(chip);
        SaveTags();
    }

    private void SaveTags() => _store.SetPlaceTags(_place.Id, Tags.Select(t => t.Text));
}

public sealed partial class TagChip(string text, Action<TagChip> remove)
{
    public string Text { get; } = text;

    [RelayCommand]
    private void Remove() => remove(this);
}
