using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using EasyImageImporter.App;
using EasyImageImporter.App.ViewModels;
using EasyImageImporter.App.ViewModels.Review;
using EasyImageImporter.App.Views;

// Usage: dotnet run -- <card folder> <work folder> <models folder or ->
//
// First run: copies the card, imports it with a fresh data folder and waits for the animal
// recognition, then keeps that state as a snapshot. Later runs start from the snapshot, so a
// full walk through the screens takes seconds. Delete <work>/snapshot to start over.
var (card, work, models) = (Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), args[2]);
var shots = Path.Combine(work, "shots");
var snapshot = Path.Combine(work, "snapshot");
var live = Path.Combine(work, "live");

var clock = Stopwatch.StartNew();
var norwegian = CultureInfo.GetCultureInfo("nb-NO");
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = norwegian;
CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = norwegian;

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

// The app also watches for real cards; one mounted now would be imported (and erased) instead.
if (DriveInfo.GetDrives().Any(d => d.IsReady && Directory.Exists(Path.Combine(d.RootDirectory.FullName, "DCIM"))))
{
    Console.Error.WriteLine("Eject memory cards (and card images) first.");
    return;
}

Directory.CreateDirectory(shots);
foreach (var old in Directory.GetFiles(shots, "*.png")) File.Delete(old);

if (!Directory.Exists(snapshot)) Prepare();
Fresh();
Walk(withModels: false, only: ["review"], prefix: "a");
Walk(withModels: true, only: null, prefix: "b");
Console.WriteLine($"Screenshots in {shots}");
return;

void Prepare()
{
    Reset(live);
    Directory.CreateDirectory(live);
    CopyDir(card, Path.Combine(live, "card"));
    LinkModels(true);
    var (vm, window) = Open();
    Shot(window, "0-idle");
    var idle = (IdleScreen)vm.Screen;
    vm.PickFolder = () => Task.FromResult<string?>(Path.Combine(live, "card"));
    idle.ChooseFolderCommand.Execute(null);
    Pump(() => vm.Screen is WorkingScreen { IsIndeterminate: false, Progress: > 30 });
    Shot(window, "0-copying");
    Pump(() => vm.Screen is ReviewScreen, 600_000);
    var review = (ReviewScreen)vm.Screen;
    Pump(() => review.Recognition.Status is RecognitionStatus.Analysing && review.Recognition.Progress > 10, 600_000);
    Shot(window, "0-review-analysing");
    Pump(() => review.Recognition.Status is RecognitionStatus.Done, 1_800_000);
    window.Close();
    vm.Dispose();
    Reset(snapshot);
    CopyDir(live, snapshot);
}

// A first start: nothing imported yet.
void Fresh()
{
    Reset(live);
    var (vm, window) = Open();
    Shot(window, "idle");
    vm.ShowImports();
    Shot(window, "imports-empty");
    window.Close();
    vm.Dispose();
}

void Walk(bool withModels, string[]? only, string prefix)
{
    Reset(live);
    CopyDir(snapshot, live);
    LinkModels(withModels);
    var (vm, window) = Open();
    Pump(() => vm.Screen is ReviewScreen, 120_000);
    var shot = (string name) => Shot(window, $"{prefix}-{name}");

    var review = (ReviewScreen)vm.Screen;
    Pump(() => review.Recognition.Status is RecognitionStatus.Done or RecognitionStatus.NotInstalled);
    shot("review");
    Small(window, () => shot("review-small"));
    if (only is not null) { window.Close(); vm.Dispose(); return; }

    // A filter, then back to all.
    if (review.Filters.FirstOrDefault(f => f.Text.StartsWith("Tomme")) is { } empty)
    {
        empty.SelectCommand.Execute(null);
        Pump(() => vm.Screen is ReviewScreen r && r != review);
        shot("review-filter-empty");
        var filtered = (ReviewScreen)vm.Screen;
        filtered.DiscardShownCommand.Execute(null);
        if (vm.Screen != filtered) throw new InvalidOperationException("The list was rebuilt; it should update in place.");
        shot("review-empty-discarded");
        filtered.Filters[0].SelectCommand.Execute(null);
        Pump(() => vm.Screen is ReviewScreen r && r.FilterText is null);
        review = (ReviewScreen)vm.Screen;
    }

    // A visit with an animal suggestion, its frames, and one frame large.
    var cards = review.Rows.OfType<VisitRow>().SelectMany(r => r.Items).ToList();
    var visit = cards.FirstOrDefault(c => c.HasQuestion) ?? cards[0];
    visit.OpenCommand.Execute(null);
    Pump(() => vm.Screen is VisitScreen);
    var visitScreen = (VisitScreen)vm.Screen;
    shot("visit");
    Small(window, () => shot("visit-small"));
    visitScreen.Tiles[Math.Min(1, visitScreen.Tiles.Count - 1)].OpenCommand.Execute(null);
    Pump(() => visitScreen.Viewer?.Image is not null);
    shot("viewer");
    Small(window, () => shot("viewer-small"));
    visitScreen.Viewer!.CloseCommand.Execute(null);
    visitScreen.BackCommand.Execute(null);
    Pump(() => vm.Screen is ReviewScreen);

    // Naming, saving, done, erasing.
    ((ReviewScreen)vm.Screen).NextCommand.Execute(null);
    Pump(() => vm.Screen is NamingScreen);
    var naming = (NamingScreen)vm.Screen;
    shot("naming");
    Small(window, () => shot("naming-small"));
    foreach (var place in naming.Places.Where(p => string.IsNullOrWhiteSpace(p.Title)).Select((p, i) => (p, i)))
        place.p.Title = $"Sted {place.i + 1}";
    naming.SaveCommand.Execute(null);
    Pump(() => vm.Screen is WorkingScreen { Progress: > 20 } or DoneScreen, 300_000);
    if (vm.Screen is WorkingScreen) shot("saving");
    Pump(() => vm.Screen is DoneScreen, 300_000);
    var done = (DoneScreen)vm.Screen;
    shot("done");
    Small(window, () => shot("done-small"));
    done.Erase.AskCommand.Execute(null);
    shot("erase-ask");
    Settle(1200); // the confirm button arms after a moment
    done.Erase.ConfirmCommand.Execute(null);
    Pump(() => vm.Screen is MessageScreen, 300_000);
    shot("erased");

    vm.ShowImports();
    shot("imports");
    window.Close();
    vm.Dispose();
}

(MainViewModel, Window) Open()
{
    Environment.SetEnvironmentVariable("EASYIMAGEIMPORTER_DATA", Path.Combine(live, "data"));
    Environment.SetEnvironmentVariable("EASYIMAGEIMPORTER_ARCHIVE", Path.Combine(live, "archive"));
    var vm = new MainViewModel();
    var window = new MainWindow { DataContext = vm, Width = 1040, Height = 760 };
    window.Show();
    vm.Start();
    Pump(() => vm.Screen is not WorkingScreen { Title: "Starter…" });
    return (vm, window);
}

void Small(Window window, Action shoot)
{
    (window.Width, window.Height) = (780, 540);
    Settle(400);
    shoot();
    (window.Width, window.Height) = (1040, 760);
    Settle(400);
}

void Shot(Window window, string name)
{
    Settle(1500); // thumbnails load in the background
    var frame = window.CaptureRenderedFrame();
    using (var file = File.Create(Path.Combine(shots, name + ".png"))) frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    Console.WriteLine($"  {name} ({clock.Elapsed.TotalSeconds:N0} s)");
}

void Settle(int ms)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms) Tick();
}

void Pump(Func<bool> until, int timeoutMs = 60_000)
{
    var sw = Stopwatch.StartNew();
    var lastNote = 0L;
    while (!until())
    {
        if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Waited too long for the app.");
        if (sw.ElapsedMilliseconds - lastNote > 10_000)
        {
            lastNote = sw.ElapsedMilliseconds;
            Console.WriteLine($"    waiting ({sw.Elapsed.TotalSeconds:N0} s)");
        }
        Tick();
    }
}

static void Tick()
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Thread.Sleep(15);
}

void LinkModels(bool on)
{
    var target = Path.Combine(live, "data", "models");
    if (Directory.Exists(target)) Directory.Delete(target, true);
    if (!on || models == "-") return;
    Directory.CreateDirectory(target);
    foreach (var name in new[] { "detector.onnx", "classifier.onnx", "norway.json" })
        Process.Start("ln", [Path.Combine(models, name), Path.Combine(target, name)]).WaitForExit();
}

static void Reset(string dir)
{
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
}

static void CopyDir(string from, string to) =>
    Process.Start("cp", ["-Rc", from, to]).WaitForExit(); // clone on APFS: instant, no extra space
