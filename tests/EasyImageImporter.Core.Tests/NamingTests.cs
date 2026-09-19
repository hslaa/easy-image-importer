using System.Text;
using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.Metadata;
using EasyImageImporter.Core.Naming;

namespace EasyImageImporter.Core.Tests;

public sealed class NamesTests
{
    [Theory]
    [InlineData("Høgfjellåsen", "Høgfjellåsen")]
    [InlineData("  Store   Høgfjellet ", "Store Høgfjellet")]
    [InlineData("Ved: bekken/åsen?", "Ved bekken åsen")]
    [InlineData("Enden...", "Enden")]
    [InlineData("CON", "CON_")]
    [InlineData("", "")]
    public void Clean_makes_windows_safe_names(string input, string expected) =>
        Assert.Equal(expected, Names.Clean(input));

    [Fact]
    public void Clean_normalises_macos_style_letters()
    {
        var decomposed = "Høgfjellåsen"; // "å" as a + ring, the way macOS stores it
        Assert.Equal("Høgfjellåsen", Names.Clean(decomposed));
        Assert.True(Names.Clean(decomposed).IsNormalized(NormalizationForm.FormC));
    }

    [Theory]
    [InlineData("2026-06-01", "2026-06-28", "Juni 2026")]
    [InlineData("2026-06-20", "2026-07-03", "Juni–Juli 2026")]
    [InlineData("2025-12-20", "2026-01-10", "Desember 2025–Januar 2026")]
    [InlineData("2000-01-01", "2000-01-03", "")]
    public void Period_is_readable(string start, string end, string expected) =>
        Assert.Equal(expected, Names.Period(DateTime.Parse(start), DateTime.Parse(end)));

    [Fact]
    public void Folder_suggestion_has_place_period_and_the_three_most_photographed_animals()
    {
        var name = Names.FolderSuggestion("Høgfjellåsen", new DateTime(2026, 6, 1), new DateTime(2026, 6, 20),
            ["Kongeørn", "Ravn", "Rev", "Grevling"]);
        Assert.Equal("Høgfjellåsen Juni 2026 – Kongeørn, Ravn, Rev", name);
    }

    [Fact]
    public void File_name_follows_the_spec()
    {
        var start = new DateTime(2026, 9, 18, 7, 12, 3);
        Assert.Equal("2026-09-18_Høgfjellåsen_0712_Nøtteskrike_001.jpg", Names.FileName(start, "Høgfjellåsen", "Nøtteskrike", 1, ".JPG"));
        Assert.Equal("2026-09-18_Høgfjellåsen_0712_014.jpg", Names.FileName(start, "Høgfjellåsen", null, 14, ".JPG"));
    }
}

public sealed class SavingWithPlacesTests : IDisposable
{
    private readonly TestEnv _env = new();
    public void Dispose() => _env.Dispose();

    private static readonly DateTime Start = new(2026, 6, 3, 6, 0, 0, DateTimeKind.Utc);
    private int _frames; // every synthetic frame is different, or re-imports would be skipped as duplicates

    /// <summary>Two visits of 4 frames at <paramref name="place"/>, starting <paramref name="hours"/> in.</summary>
    private void AddPlace(int place, double hours, string folder = "DCIM/100MEDIA")
    {
        for (var visit = 0; visit < 2; visit++)
        for (var frame = 0; frame < 4; frame++)
        {
            var n = Directory.Exists(Path.Combine(_env.Card, folder)) ? Directory.GetFiles(Path.Combine(_env.Card, folder)).Length + 1 : 1;
            _env.AddCardFile($"{folder}/IMAG{n:0000}.JPG", TestImages.Scene(place, ++_frames),
                Start.AddHours(hours + visit * 5).AddSeconds(frame * 3));
        }
    }

    private void Name(long sessionId, string title, int place = 0)
    {
        var p = _env.Review.GetOverview(sessionId).Places[place];
        _env.Store.SetPlaceTitle(p.Id, title);
    }

    [Fact]
    public void Each_place_gets_its_own_folder_with_readable_file_names()
    {
        AddPlace(1, 0);
        AddPlace(2, 72);
        var session = _env.CopyAndPrepare();
        var overview = _env.Review.GetOverview(session.Id);
        Assert.Equal(2, overview.Places.Count);
        Name(session.Id, "Høgfjellåsen", 0);
        Name(session.Id, "Bekken", 1);
        _env.Review.SetLabel(_env.Review.GetOverview(session.Id).Places[0].Visits[0], "Kongeørn");
        _env.Store.SetPlaceDescription(overview.Places[0].Id, "Revekadaver ved stien");
        _env.Store.SetPlaceTags(overview.Places[0].Id, ["åte", "høst"]);
        _env.Review.SetKeep(_env.Review.GetOverview(session.Id).Places[1].Visits[1], keep: false);

        var import = _env.Finalizer.Run(session.Id)!;

        var folders = _env.Store.GetImportFolders(import.Id);
        Assert.Equal(["Høgfjellåsen Juni 2026 – Kongeørn", "Bekken Juni 2026"], folders.Select(f => Path.GetFileName(f.FolderPath)));
        Assert.All(folders, f => Assert.Equal("2026", Path.GetFileName(Path.GetDirectoryName(f.FolderPath))));

        var first = Directory.GetFiles(folders[0].FolderPath, "*.jpg").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(8, first.Count);
        Assert.Matches(@"^2026-06-03_Høgfjellåsen_\d{4}_Kongeørn_001\.jpg$", first[0]);

        Assert.Equal(4, Directory.GetFiles(folders[1].FolderPath, "*.jpg").Length);
        Assert.Equal(4, Directory.GetFiles(Path.Combine(folders[1].FolderPath, Finalizer.DiscardedFolderName)).Length);

        var summary = File.ReadAllText(Path.Combine(folders[0].FolderPath, Finalizer.SummaryFileName));
        Assert.Contains("Høgfjellåsen", summary);
        Assert.Contains("Revekadaver ved stien", summary);
        Assert.Contains("Dyr:          Kongeørn", summary);
        Assert.Contains("Stikkord:     åte, høst", summary);
    }

    [Fact]
    public void Folder_name_can_be_overridden()
    {
        AddPlace(1, 0);
        var session = _env.CopyAndPrepare();
        var place = _env.Review.GetOverview(session.Id).Places[0];
        _env.Store.SetPlaceTitle(place.Id, "Høgfjellåsen");
        _env.Store.SetPlaceFolderName(place.Id, "Ørnene på åsen");

        var import = _env.Finalizer.Run(session.Id)!;

        Assert.Equal("Ørnene på åsen", Path.GetFileName(import.FolderPath));
    }

    [Fact]
    public void Undo_removes_every_folder_of_the_import()
    {
        AddPlace(1, 0);
        AddPlace(2, 72);
        var session = _env.CopyAndPrepare();
        Name(session.Id, "A", 0);
        Name(session.Id, "B", 1);
        var import = _env.Finalizer.Run(session.Id)!;
        var folders = _env.Store.GetImportFolders(import.Id);

        _env.Undo.Run(import.Id);

        Assert.All(folders, f => Assert.False(Directory.Exists(f.FolderPath)));
        Assert.Equal(16, Directory.GetFiles(_env.Paths.StagingDir(session.Id)).Length);
    }

    [Fact]
    public void A_saved_place_is_recognised_on_the_next_card()
    {
        AddPlace(1, 0);
        var first = _env.CopyAndPrepare();
        Name(first.Id, "Høgfjellåsen");
        _env.Finalizer.Run(first.Id);
        _env.Eraser.Erase(first.Id);

        // Next trip: same spot (place 1) and a new one (place 2).
        AddPlace(1, 24 * 30, "DCIM/101MEDIA");
        AddPlace(2, 24 * 33, "DCIM/101MEDIA");
        var second = _env.CopyAndPrepare();

        var places = _env.Review.GetOverview(second.Id).Places;
        Assert.Equal(2, places.Count);
        Assert.Equal("Høgfjellåsen", places[0].Details.Title);
        Assert.Equal("Høgfjellåsen", places[0].Details.RecognisedName);
        Assert.Null(places[1].Details.Title);
    }

    [Fact]
    public void Tags_are_written_for_explorer_and_the_image_itself_is_unchanged()
    {
        var exe = ExifTool.Locate();
        Assert.SkipWhen(exe is null, "ExifTool is not installed");

        AddPlace(1, 0);
        var session = _env.CopyAndPrepare();
        var place = _env.Review.GetOverview(session.Id).Places[0];
        _env.Store.SetPlaceTitle(place.Id, "Høgfjellåsen");
        _env.Store.SetPlaceDescription(place.Id, "Revekadaver");
        _env.Store.SetPlaceTags(place.Id, ["åte"]);
        _env.Review.SetLabel(place.Visits[0], "Kongeørn");
        using var tool = new ExifTool(exe!);
        var staged = _env.Review.StagedPath(place.Visits[0].Frames[0]);
        var imageBefore = tool.ImageDataHash(staged);

        var finalizer = new Finalizer(_env.Fs, _env.Store, _env.Paths, _env.Time, () => new ExifTool(exe!));
        var import = finalizer.Run(session.Id)!;

        var photo = Directory.GetFiles(import.FolderPath, "*_Kongeørn_001.jpg").Single();
        var tags = tool.Read(photo, "XPKeywords", "XPTitle", "XPComment", "Subject");
        Assert.Contains("XPKeywords                      : Høgfjellåsen;Kongeørn;åte", tags);
        Assert.Contains("XPTitle                         : Høgfjellåsen", tags);
        Assert.Contains("XPComment                       : Revekadaver", tags);
        Assert.Equal(imageBefore, tool.ImageDataHash(photo));
        Assert.Empty(Directory.GetFiles(import.FolderPath, "*.exiftool-tmp"));
    }
}
