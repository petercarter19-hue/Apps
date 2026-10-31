using System.Windows;
using System.Windows.Media;
using PhotoTwins.Core.Cache;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins;

/// <summary>One photo shown as a card: its picture (drawn when it comes into view), the star, and the details under it.</summary>
public sealed class CardVm : ObservableObject
{
    private readonly ThumbnailService _thumbs;
    private readonly string _key;
    private readonly PhotoFile _file;

    public CardVm(PhotoFile file, bool identicalCopy, ThumbnailService thumbs, GroupVm group)
    {
        _thumbs = thumbs; _file = file; _key = ThumbnailStore.KeyOf(file.Size, file.Sha256); Group = group;
        FullPath = file.Path;
        Name = file.Name;
        NameText = Format.Breakable(file.Name);
        FolderText = Format.Breakable(file.Folder);
        var dims = file.Fingerprint is { } f ? $"{f.Width:N0} × {f.Height:N0}" : "size in pixels unknown";
        SizeAndDims = $"{Format.Size(file.Size)} · {dims}";
        TakenText = file.Info.Taken is { } t ? "Taken " + t.ToString("MMM d, yyyy h:mm tt") : "No date taken";
        var badges = new List<string>();
        if (identicalCopy) badges.Add("Identical copy");
        if (group.Model.IsShared(file)) badges.Add("Shared folder");
        if (group.Model.CloudService(file) is { } svc) badges.Add(svc);
        Badge = string.Join(" · ", badges);
        MakeKeeperCommand = new RelayCommand(() => Group.Owner.SetKeeper(Group, this), () => Group.Owner.CanChangeThings);
    }

    public GroupVm Group { get; }
    public PhotoFile File => _file;
    public string FullPath { get; }
    public string Name { get; }
    public string NameText { get; }
    public string FolderText { get; }
    public string SizeAndDims { get; }
    public string TakenText { get; }
    public string Badge { get; }
    public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ThumbnailKey => _key;

    // ---- the star
    public bool IsKeeper => string.Equals(Group.Model.Keeper.Path, FullPath, StringComparison.OrdinalIgnoreCase);
    public Visibility KeeperVisibility => IsKeeper ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>"Make this the keeper": offered on every other photo, except where a shared copy must stay.</summary>
    public bool CanBeKeeper => !IsKeeper && (!Group.Model.HasSharedCopies || Group.Model.IsShared(_file));
    public Visibility MakeKeeperVisibility => CanBeKeeper ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SharedMustStayVisibility => !IsKeeper && !CanBeKeeper ? Visibility.Visible : Visibility.Collapsed;
    public string SharedMustStayText => "The copy in the shared folder always stays.";
    public string KeeperExplanation => Group.Model.KeeperChosenByUser ? "★ Keeper (your choice)" : "★ Keeper";
    public RelayCommand MakeKeeperCommand { get; }

    public void RefreshStar()
    {
        foreach (var n in new[] { nameof(IsKeeper), nameof(KeeperVisibility), nameof(CanBeKeeper), nameof(MakeKeeperVisibility), nameof(SharedMustStayVisibility), nameof(KeeperExplanation) }) Raise(n);
        MakeKeeperCommand.Changed();
    }

    /// <summary>The picture. Reading this starts drawing it (once) and raises a change when it's ready.</summary>
    public ImageSource? Picture
    {
        get
        {
            var img = _thumbs.TryGet(_key, FullPath);
            if (img == null && _thumbs.FailureOf(_key, FullPath) == null)
                _thumbs.Request(_key, FullPath, () => { Raise(nameof(Picture)); Raise(nameof(PictureStatus)); Raise(nameof(PictureStatusVisibility)); });
            return img;
        }
    }

    public string PictureStatus => _thumbs.FailureOf(_key, FullPath) ?? (_thumbs.TryGet(_key, FullPath) == null ? "Drawing the picture…" : "");
    public Visibility PictureStatusVisibility => PictureStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>One group on screen: a title, its photos side by side with the star on the keeper, and the button to remove the others.</summary>
public sealed class GroupVm : ObservableObject
{
    private IReadOnlyList<CardVm>? _cards;
    private readonly ThumbnailService _thumbs;

    public GroupVm(DecidedGroup model, MainViewModel owner, ThumbnailService thumbs)
    {
        Model = model; Owner = owner; _thumbs = thumbs;
        RemoveCommand = new RelayCommand(() => _ = Owner.RemoveGroupAsync(this), () => Owner.CanChangeThings && Model.StillAGroup);
    }

    public DecidedGroup Model { get; }
    public MainViewModel Owner { get; }
    public RelayCommand RemoveCommand { get; }

    public string Title => Model.Kind switch
    {
        GroupKind.Exact => $"{Model.Files.Count} identical copies, {Format.Size(Model.Files[0].Size)} each",
        GroupKind.LookAlike => $"{Model.Files.Count} look-alike photos",
        _ => $"{Model.Files.Count} copies of the same photo",
    };

    public string Subtitle => Model.Kind switch
    {
        GroupKind.Exact => $"Keeping one and removing the other {Model.ExtraPhotos} would free {Format.Size(Model.FreeableBytes)}.",
        GroupKind.LookAlike => $"Different photos taken close together or that look very much alike. Keeping one and removing the other {Model.ExtraPhotos} would free {Format.Size(Model.FreeableBytes)}.",
        _ => $"Saved in {Format.Plural(DifferentWays, "different way", "different ways")} (resized, re-saved or another type). " +
             $"Keeping one and removing the other {Model.ExtraPhotos} would free {Format.Size(Model.FreeableBytes)}.",
    };

    /// <summary>Look-alikes are not copies: say so, every time.</summary>
    public string LookAlikeNote => "These are different photos, not copies. Look at each one before you remove any.";
    public Visibility LookAlikeNoteVisibility => Model.Kind == GroupKind.LookAlike ? Visibility.Visible : Visibility.Collapsed;

    private int DifferentWays => Model.Files.Select(f => $"{f.Size}-{Convert.ToHexString(f.Sha256)}").Distinct().Count();

    /// <summary>One plain line when nothing can confirm these are really the same photo (a burst, say).</summary>
    public string NoDateWarning => "Some of these have no date info. Check they're really the same photo before removing.";
    public Visibility NoDateWarningVisibility => Model.SomeHaveNoDateInfo ? Visibility.Visible : Visibility.Collapsed;

    public string RemoveButtonText => $"Remove the others ({Format.Plural(Model.ExtraPhotos, "photo", "photos")}, {Format.Size(Model.FreeableBytes)})";

    /// <summary>Keeper first, then the rest in order.</summary>
    public IReadOnlyList<CardVm> Cards => _cards ??= BuildCards();

    private IReadOnlyList<CardVm> BuildCards()
    {
        var counts = Model.Files.GroupBy(f => $"{f.Size}-{Convert.ToHexString(f.Sha256)}").ToDictionary(g => g.Key, g => g.Count());
        bool identical(PhotoFile f) => Model.Kind == GroupKind.Same && counts[$"{f.Size}-{Convert.ToHexString(f.Sha256)}"] > 1;
        var ordered = new[] { Model.Keeper }.Concat(Model.Files.Where(f => !string.Equals(f.Path, Model.Keeper.Path, StringComparison.OrdinalIgnoreCase)));
        return ordered.Select(f => new CardVm(f, identical(f), _thumbs, this)).ToList();
    }

    /// <summary>Buttons switch on and off with the app's state (only the cards already on screen need it).</summary>
    public void RefreshCommands()
    {
        RemoveCommand.Changed();
        if (_cards != null) foreach (var c in _cards) c.MakeKeeperCommand.Changed();
    }

    /// <summary>Called after the keeper moves or photos are removed.</summary>
    public void Refresh()
    {
        _cards = null;
        foreach (var n in new[] { nameof(Title), nameof(Subtitle), nameof(Cards), nameof(RemoveButtonText), nameof(NoDateWarningVisibility) }) Raise(n);
        RemoveCommand.Changed();
    }
}

/// <summary>What the main screen shows and does. Every state of a scan or removal has its own wording here, so the screen is never blank or silent.</summary>
public sealed class MainViewModel : ObservableObject, IRestoreTarget
{
    // every group of the current scan in its original order (the lists on screen are these, minus groups that are no longer a group)
    private IReadOnlyList<GroupVm> _allExact = Array.Empty<GroupVm>();
    private IReadOnlyList<GroupVm> _allSame = Array.Empty<GroupVm>();
    private IReadOnlyList<GroupVm> _allLook = Array.Empty<GroupVm>();

    private readonly AppServices _s;
    private readonly IDialogs _dialogs;
    private ScanControl? _control;
    private CancellationTokenSource? _removeCts;
    private Task<ScanResult>? _running;
    private TaskCompletionSource? _scanCompletion;
    private Task? _removing;
    private ScanResult? _result;

    private bool _isScanning;
    private bool _isPaused;
    private bool _isStopping;
    private bool _isBusy;
    private string _statusText = "";
    private double _progressValue;
    private double _progressMax = 1;
    private bool _progressIndeterminate;
    private string _headline = "";
    private string _subline = "";
    private string _hint = "";
    private string _errorText = "";
    private string _banner = "";
    private string _resultText = "";
    private bool _resultIsProblem;
    private string _folderSummary = "";
    private string _problemsText = "";
    private string _leftOutText = "";
    private IReadOnlyList<GroupVm> _exactGroups = Array.Empty<GroupVm>();
    private IReadOnlyList<GroupVm> _sameGroups = Array.Empty<GroupVm>();
    private IReadOnlyList<GroupVm> _lookAlikeGroups = Array.Empty<GroupVm>();
    private int _selectedTab;
    private bool _lastScanUnfinished;
    private bool _foundDuplicates;

    public RelayCommand ScanCommand { get; }
    public RelayCommand PauseResumeCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RemoveAllExactCommand { get; }
    public RelayCommand RemoveAllSamePhotoCommand { get; }

    public MainViewModel(AppServices services, IDialogs? dialogs = null)
    {
        _s = services;
        _dialogs = dialogs ?? new WindowDialogs();
        ScanCommand = new RelayCommand(() => _ = StartScanAsync(), () => !IsWorking && HasFolders);
        PauseResumeCommand = new RelayCommand(TogglePause, () => IsScanning && !_isStopping);
        StopCommand = new RelayCommand(RequestStop, () => IsWorking && !_isStopping);
        RemoveAllExactCommand = new RelayCommand(() => _ = RemoveAllExactAsync(), () => CanChangeThings && RemovableExactCount > 0);
        RemoveAllSamePhotoCommand = new RelayCommand(() => _ = RemoveAllSamePhotoAsync(), () => CanChangeThings && RemovableSameCount > 0);
        _lastScanUnfinished = _s.Cache.LastScanUnfinished;
        RefreshFolders(clearResults: false);
        var notes = _s.StartupNotes();
        Banner = string.Join(" ", notes);
        if (_lastScanUnfinished) Banner = (Banner + " Your last scan didn't finish. Click Continue to pick up where it left off.").Trim();
        UpdateHint();
    }

    public bool IsScanning { get => _isScanning; private set { if (Set(ref _isScanning, value)) WorkChanged(); } }
    /// <summary>True while a removal is running (or being asked about).</summary>
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) WorkChanged(); } }
    public bool IsWorking => IsScanning || IsBusy;
    public bool IsPaused { get => _isPaused; private set { if (Set(ref _isPaused, value)) Raise(nameof(PauseButtonText)); } }
    public bool CanChangeFolders => !IsWorking;
    public bool CanUndo => !IsWorking;
    /// <summary>Remove and star buttons only work when nothing else is going on.</summary>
    public bool CanChangeThings => !IsWorking;
    public bool HasFolders => _s.Folders.Folders.Count > 0;

    private IEnumerable<GroupVm> AllGroups => _exactGroups.Concat(_sameGroups).Concat(_lookAlikeGroups);
    /// <summary>The groups that count as duplicates in the headline: look-alikes never do.</summary>
    private IEnumerable<GroupVm> DuplicateGroups => _exactGroups.Concat(_sameGroups);

    private void WorkChanged()
    {
        Raise(nameof(IsWorking)); Raise(nameof(CanChangeFolders)); Raise(nameof(CanUndo)); Raise(nameof(CanChangeThings));
        Raise(nameof(ProgressVisibility)); Raise(nameof(ScanButtonText));
        ChangeCommands();
        foreach (var g in AllGroups) g.RefreshCommands();
    }

    public string PauseButtonText => IsPaused ? "Resume" : "Pause";
    public string ScanButtonText => _result == null && !_lastScanUnfinished ? "Scan" : (_result is { Completed: false } || _lastScanUnfinished ? "Continue" : "Scan again");

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }
    public double ProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => Set(ref _progressIndeterminate, value); }
    public Visibility ProgressVisibility => IsWorking ? Visibility.Visible : Visibility.Collapsed;

    public string Headline { get => _headline; private set { Set(ref _headline, value); Raise(nameof(HeadlineVisibility)); } }
    public Visibility HeadlineVisibility => string.IsNullOrEmpty(_headline) ? Visibility.Collapsed : Visibility.Visible;
    public string Subline { get => _subline; private set { Set(ref _subline, value); Raise(nameof(SublineVisibility)); } }
    public Visibility SublineVisibility => string.IsNullOrEmpty(_subline) ? Visibility.Collapsed : Visibility.Visible;
    public string Hint { get => _hint; private set { Set(ref _hint, value); Raise(nameof(HintVisibility)); } }
    /// <summary>The welcome card's title: what to do before the first scan, or what is happening during it.</summary>
    public string HintTitle { get => _hintTitle; private set => Set(ref _hintTitle, value); }
    private string _hintTitle = "Find your duplicate photos";
    public Visibility HintVisibility => string.IsNullOrEmpty(_hint) ? Visibility.Collapsed : Visibility.Visible;
    public string ErrorText { get => _errorText; private set { Set(ref _errorText, value); Raise(nameof(ErrorVisibility)); } }
    public Visibility ErrorVisibility => string.IsNullOrEmpty(_errorText) ? Visibility.Collapsed : Visibility.Visible;
    public string Banner { get => _banner; private set { Set(ref _banner, value); Raise(nameof(BannerVisibility)); } }
    public Visibility BannerVisibility => string.IsNullOrEmpty(_banner) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>What the last removal did, in plain words.</summary>
    public string ResultText { get => _resultText; private set { Set(ref _resultText, value); Raise(nameof(ResultVisibility)); } }
    public Visibility ResultVisibility => string.IsNullOrEmpty(_resultText) ? Visibility.Collapsed : Visibility.Visible;
    public bool ResultIsProblem { get => _resultIsProblem; private set { Set(ref _resultIsProblem, value); Raise(nameof(ResultBorderBrushKey)); } }
    public string ResultBorderBrushKey => _resultIsProblem ? "SystemFillColorCriticalBrush" : "CardStrokeColorDefaultBrush";
    public RemovalResult? LastRemoval { get; private set; }
    public Visibility ResultDetailsVisibility => LastRemoval is { } r && (r.Failed.Count > 0 || r.Dropped.Count > 0) ? Visibility.Visible : Visibility.Collapsed;

    public string FolderSummary { get => _folderSummary; private set => Set(ref _folderSummary, value); }
    public string ProblemsText { get => _problemsText; private set { Set(ref _problemsText, value); Raise(nameof(ProblemsVisibility)); } }
    public Visibility ProblemsVisibility => string.IsNullOrEmpty(_problemsText) ? Visibility.Collapsed : Visibility.Visible;
    public string LeftOutText { get => _leftOutText; private set { Set(ref _leftOutText, value); Raise(nameof(LeftOutVisibility)); } }
    public Visibility LeftOutVisibility => string.IsNullOrEmpty(_leftOutText) ? Visibility.Collapsed : Visibility.Visible;

    // ---- the two kinds of match, each on its own tab
    public IReadOnlyList<GroupVm> ExactGroups { get => _exactGroups; private set { Set(ref _exactGroups, value); Raise(nameof(ExactTabHeader)); Raise(nameof(ExactEmptyVisibility)); BulkChanged(); } }
    public IReadOnlyList<GroupVm> SameGroups { get => _sameGroups; private set { Set(ref _sameGroups, value); Raise(nameof(SameTabHeader)); Raise(nameof(SameEmptyVisibility)); BulkChanged(); } }
    public IReadOnlyList<GroupVm> LookAlikeGroups { get => _lookAlikeGroups; private set { Set(ref _lookAlikeGroups, value); Raise(nameof(LookAlikeTabHeader)); Raise(nameof(LookAlikeEmptyVisibility)); } }
    public string LookAlikeTabHeader => $"Look-alikes ({_lookAlikeGroups.Count:N0})";
    public Visibility LookAlikeEmptyVisibility => _lookAlikeGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>The Look-alikes tab only exists when Pete has switched look-alikes on in Settings.</summary>
    public Visibility LookAlikeTabVisibility => _s.Folders.LookAlikesOn ? Visibility.Visible : Visibility.Collapsed;
    public string ExactTabHeader => $"Exact copies ({_exactGroups.Count:N0})";
    public string SameTabHeader => $"Same photo ({_sameGroups.Count:N0})";
    public Visibility ExactEmptyVisibility => _exactGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SameEmptyVisibility => _sameGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }
    /// <summary>The tabs only show once there is a result to look at (even a stopped one), and not when no photos were found at all.</summary>
    public Visibility ResultsVisibility => _result != null && !(_result.Completed && _result.PhotosFound == 0) ? Visibility.Visible : Visibility.Collapsed;

    // ---- "Remove all exact copies" (only ever exact copies)
    private int RemovableExactCount => _exactGroups.Sum(g => g.Model.StillAGroup ? g.Model.ExtraPhotos : 0);
    private long RemovableExactBytes => _exactGroups.Sum(g => g.Model.StillAGroup ? g.Model.FreeableBytes : 0);
    public string RemoveAllExactText => $"Remove all exact copies ({Format.Plural(RemovableExactCount, "photo", "photos")}, {Format.Size(RemovableExactBytes)})";
    public Visibility RemoveAllExactVisibility => RemovableExactCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---- "Remove all same-photo copies": same-photo groups only, and never the ones with a missing date (those are checked by eye, one by one)
    private IEnumerable<GroupVm> RemovableSameGroups => _sameGroups.Where(g => g.Model.StillAGroup && !g.Model.SomeHaveNoDateInfo);
    private int RemovableSameCount => RemovableSameGroups.Sum(g => g.Model.ExtraPhotos);
    private long RemovableSameBytes => RemovableSameGroups.Sum(g => g.Model.FreeableBytes);
    /// <summary>Same-photo groups the bulk button leaves alone because some photo has no date info.</summary>
    private int SameGroupsLeftForYou => _sameGroups.Count(g => g.Model.StillAGroup && g.Model.SomeHaveNoDateInfo);
    public string RemoveAllSamePhotoText => $"Remove all same-photo copies ({Format.Plural(RemovableSameCount, "photo", "photos")}, {Format.Size(RemovableSameBytes)})";
    public Visibility RemoveAllSamePhotoVisibility => RemovableSameCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void BulkChanged()
    {
        Raise(nameof(RemoveAllExactText)); Raise(nameof(RemoveAllExactVisibility)); RemoveAllExactCommand.Changed();
        Raise(nameof(RemoveAllSamePhotoText)); Raise(nameof(RemoveAllSamePhotoVisibility)); RemoveAllSamePhotoCommand.Changed();
    }

    public ScanResult? Result => _result;
    public AppServices Services => _s;

    private void ChangeCommands() { ScanCommand.Changed(); PauseResumeCommand.Changed(); StopCommand.Changed(); RemoveAllExactCommand.Changed(); RemoveAllSamePhotoCommand.Changed(); }

    // ---------------------------------------------------------------- folders

    /// <summary>Called when the folder list changes. Old results are cleared so they never show folders that are no longer in the list.</summary>
    public void RefreshFolders(bool clearResults = true)
    {
        var f = _s.Folders.Folders;
        if (f.Count == 0) FolderSummary = "No folders yet. Click \"Settings\" to add one.";
        else
        {
            string Short(string p) => Path.GetFileName(p.TrimEnd('\\')) is { Length: > 0 } n ? n : p;
            FolderSummary = f.Count == 1 ? $"{Short(f[0])}  ({f[0]})" : $"{Short(f[0])} and {f.Count - 1} more";
        }
        Raise(nameof(HasFolders));
        if (clearResults) ClearResults();
        UpdateHint();
        ChangeCommands();
    }

    private void ClearResults()
    {
        _result = null;
        _allExact = _allSame = _allLook = Array.Empty<GroupVm>();
        ExactGroups = Array.Empty<GroupVm>();
        SameGroups = Array.Empty<GroupVm>();
        LookAlikeGroups = Array.Empty<GroupVm>();
        Raise(nameof(LookAlikeTabVisibility));
        Raise(nameof(ResultsVisibility));
        Headline = ""; Subline = ""; ProblemsText = ""; LeftOutText = ""; ErrorText = ""; ResultText = "";
        Raise(nameof(ScanButtonText));
    }

    private void UpdateHint()
    {
        if (IsScanning && _result == null)
        {
            HintTitle = "Looking through your photos";
            Hint = "The groups appear here when the scan is done. You can pause or stop at any time, and the next scan picks up where this one left off.";
            return;
        }
        HintTitle = "Find your duplicate photos";
        if (IsWorking || _result != null) { Hint = ""; return; }
        Hint = HasFolders ? "Click Scan to look for duplicate photos in your folders." : "Add a folder to begin: click \"Settings\".";
    }

    // ---------------------------------------------------------------- scanning

    public async Task StartScanAsync()
    {
        if (IsWorking || !HasFolders) return;
        var folders = _s.Folders.Folders.ToList();
        var control = new ScanControl();
        _control = control;
        _isStopping = false;
        IsPaused = false;
        ErrorText = ""; ResultText = ""; Banner = string.Join(" ", _s.StartupNotes());
        _allExact = _allSame = _allLook = Array.Empty<GroupVm>();
        ExactGroups = Array.Empty<GroupVm>(); SameGroups = Array.Empty<GroupVm>(); LookAlikeGroups = Array.Empty<GroupVm>();
        Raise(nameof(LookAlikeTabVisibility));
        _result = null; Raise(nameof(ResultsVisibility));
        Headline = ""; Subline = ""; ProblemsText = ""; LeftOutText = "";
        ProgressValue = 0; ProgressMax = 1; ProgressIndeterminate = true;
        StatusText = "Starting…";
        _scanCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IsScanning = true;
        UpdateHint();

        var engine = new ScanEngine(_s.Fs, _s.Cache, _s.Log, _s.Policy, null, _s.DecisionContext, _s.ThumbStore);
        if (_s.MaxParallelReads is { } par) engine.MaxParallelReads = par;
        var progress = new Progress<ScanProgress>(OnProgress);
        _running = Task.Run(() => engine.Run(folders, control, progress));
        try
        {
            var result = await _running;
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            _s.Log.Error("Scan failed", ex);
            ErrorText = "The scan stopped because of an unexpected problem: " + ex.Message + " Details were saved to the log.";
            StatusText = "";
            Headline = "";
            _lastScanUnfinished = true;
        }
        finally
        {
            ProgressIndeterminate = false;
            IsPaused = false;
            _isStopping = false;
            IsScanning = false;
            _control = null;
            Raise(nameof(ScanButtonText));
            UpdateHint();
            _scanCompletion?.TrySetResult();
        }
    }

    private void OnProgress(ScanProgress p)
    {
        if (!IsScanning || _isStopping) return;   // late reports must not undo "Paused" or "Stopping…"
        bool paused = _control?.IsPaused ?? false;
        IsPaused = paused;
        switch (p.Phase)
        {
            case ScanPhase.Finding:
                ProgressIndeterminate = true;
                StatusText = $"Finding photos… {p.PhotosFound:N0} so far";
                break;
            case ScanPhase.Reading:
                ProgressIndeterminate = false;
                ProgressMax = Math.Max(1, p.PhotosTotal);
                ProgressValue = p.PhotosDone;
                string tail = paused ? "Paused" : p.Remaining is { } r ? Format.TimeLeft(r) : "working out the time left…";
                StatusText = $"Reading {p.PhotosDone:N0} of {p.PhotosTotal:N0} photos · {tail}";
                break;
            case ScanPhase.Grouping:
                ProgressIndeterminate = true;
                StatusText = "Matching up the copies…";
                break;
        }
    }

    internal void ApplyResult(ScanResult r)
    {
        _result = r;
        _lastScanUnfinished = !r.Completed;
        var decided = r.Decided.Count > 0 ? r.Decided
            : GroupDecider.Decide(new MatchResult { ExactGroups = r.ExactGroups, SameGroups = r.SameGroups }, null, null, null);
        _allExact = decided.Where(d => d.Kind == GroupKind.Exact).Select(d => new GroupVm(d, this, _s.Thumbnails)).ToList();
        _allSame = decided.Where(d => d.Kind == GroupKind.Same).Select(d => new GroupVm(d, this, _s.Thumbnails)).ToList();
        _allLook = r.LookAlikes.Select(d => new GroupVm(d, this, _s.Thumbnails)).ToList();
        _s.Thumbnails.ClearFailures();   // a picture that failed for a passing reason is tried again with these results
        ExactGroups = _allExact; SameGroups = _allSame; LookAlikeGroups = _allLook;
        Raise(nameof(LookAlikeTabVisibility));
        SelectedTab = _exactGroups.Count > 0 ? 0 : _sameGroups.Count > 0 ? 1 : _lookAlikeGroups.Count > 0 ? 2 : 0;
        Raise(nameof(ResultsVisibility));
        StatusText = "";
        UpdateHeadline();
        // start getting the pictures of the first groups right away, so they are there when the cards appear
        var firstTab = SelectedTab == 0 ? _exactGroups : SelectedTab == 1 ? _sameGroups : _lookAlikeGroups;
        _s.Thumbnails.Prefetch(firstTab.Take(10).SelectMany(g => g.Cards).Take(30).Select(c => (c.ThumbnailKey, c.FullPath)));

        _foundDuplicates = DuplicateGroups.Any(g => g.Model.StillAGroup);
        var parts = new List<string> { $"{Format.Plural(r.PhotosFound, "photo", "photos")} looked at" };
        if (r.SkippedOnline > 0) parts.Add($"{Format.Plural(r.SkippedOnline, "online-only photo", "online-only photos")} skipped (never downloaded)");
        if (_lookAlikeGroups.Count > 0) parts.Add($"Also: {Format.Plural(_lookAlikeGroups.Count, "group", "groups")} of look-alikes");
        Subline = string.Join(" · ", parts);
        ProblemsText = r.Problems.Count > 0 ? $"Couldn't read {Format.Plural(r.Problems.Count, "photo", "photos")}. See the list." : "";
        LeftOutText = r.LeftOut.Count > 0 ? $"{Format.Plural(r.LeftOut.Count, "folder or link", "folders and links")} left out. See why." : "";
        if (!r.Completed) Banner = ("Scan stopped before it finished. Click Continue to pick up where it left off. " + string.Join(" ", _s.StartupNotes())).Trim();
        else Banner = string.Join(" ", _s.StartupNotes());
        if (r.CacheWarning != null) Banner = (Banner + " " + r.CacheWarning).Trim();
    }

    /// <summary>The headline from what is left on screen right now (so it stays true after photos are removed).</summary>
    private void UpdateHeadline()
    {
        var r = _result;
        if (r == null) return;
        int n = DuplicateGroups.Where(g => g.Model.StillAGroup).Sum(g => g.Model.ExtraPhotos);
        long free = DuplicateGroups.Where(g => g.Model.StillAGroup).Sum(g => g.Model.FreeableBytes);
        if (r.Completed && r.Roots.Count == 0) Headline = "None of your folders could be scanned.";
        else if (r.Completed && r.PhotosFound == 0) Headline = "No photos found in your folders.";
        else if (r.Completed) Headline = n == 0 ? (_foundDuplicates && LastRemoval != null ? "No duplicates left." : "No duplicates found.") : $"{Format.Plural(n, "duplicate", "duplicates")} found. You can free {Format.Size(free)}.";
        else Headline = n == 0 ? "Stopped. No duplicates found so far." : $"Stopped. So far: {Format.Plural(n, "duplicate", "duplicates")} found, {Format.Size(free)} could be freed.";
    }

    private void TogglePause()
    {
        if (_control == null) return;
        if (_control.IsPaused) { _control.Resume(); IsPaused = false; StatusText = "Resuming…"; }
        else { _control.Pause(); IsPaused = true; StatusText = "Paused"; }
    }

    private void RequestStop()
    {
        if (_isStopping) return;
        if (IsScanning && _control != null)
        {
            _isStopping = true;
            StatusText = "Stopping and saving what's done…";
            _control.Stop();
            ChangeCommands();
        }
        else if (IsBusy && _removeCts != null)
        {
            _isStopping = true;
            StatusText = "Stopping after the photo being moved. What's done stays done and can be undone…";
            _removeCts.Cancel();
            ChangeCommands();
        }
    }

    /// <summary>Used when the window is closed mid-scan or mid-removal: stop, let everything save, then return.</summary>
    public async Task StopAndWaitAsync()
    {
        if (!IsWorking) return;
        RequestStop();
        try
        {
            if (_scanCompletion is { } scan && IsScanning) await scan.Task;
            if (_removing is { } rt && IsBusy) await rt;
        }
        catch { /* the scan's and the removal's own handlers report problems */ }
    }

    // ---------------------------------------------------------------- the star

    public void SetKeeper(GroupVm group, CardVm card)
    {
        if (!CanChangeThings) return;
        if (!group.Model.TrySetKeeper(card.File))
        {
            ResultText = "The copy in the shared folder always stays, so the star can't move off it.";
            ResultIsProblem = true;
            return;
        }
        _s.RememberKeeper(group.Model.Key, card.File.Path);
        group.Refresh();
        UpdateHeadline();
        BulkChanged();
    }

    // ---------------------------------------------------------------- removing

    private IReadOnlyList<string> Roots => _result?.Roots is { Count: > 0 } r ? r : _s.Folders.Folders;

    public async Task RemoveGroupAsync(GroupVm group)
    {
        if (!CanChangeThings || !group.Model.StillAGroup) return;
        await RunPlanAsync(RemovalPlan.ForGroup(group.Model, Roots));
    }

    public async Task RemoveAllExactAsync()
    {
        if (!CanChangeThings) return;
        var groups = _exactGroups.Select(g => g.Model).Where(g => g.StillAGroup).ToList();
        if (groups.Count == 0) return;
        await RunPlanAsync(RemovalPlan.ForAllExactCopies(groups, Roots));
    }

    /// <summary>The button on the Same photo tab. Groups with a missing date are left out and named in the question; Pete removes those one by one.</summary>
    public async Task RemoveAllSamePhotoAsync()
    {
        if (!CanChangeThings) return;
        var groups = RemovableSameGroups.Select(g => g.Model).ToList();
        if (groups.Count == 0) return;
        await RunPlanAsync(RemovalPlan.ForAllSamePhoto(groups, Roots), SameGroupsLeftForYou);
    }

    private const string WhereRemovedPhotosGo = "Removed photos go to the Recycle Bin, or to a \"Duplicates to delete\" folder on drives that don't have one. You can undo it from the Undo list.";

    private static string JoinWithAnd(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    private async Task RunPlanAsync(RemovalPlan plan, int groupsLeftOut = 0)
    {
        if (plan.PhotoCount == 0) { ResultText = "There is nothing to remove."; ResultIsProblem = false; return; }
        ResultText = ""; ResultIsProblem = false;
        IsBusy = true;   // blocks other clicks while the questions are asked
        _isStopping = false;
        _removeCts = new CancellationTokenSource();
        _removing = RunPlanCoreAsync(plan, groupsLeftOut);
        await _removing;
    }

    private async Task RunPlanCoreAsync(RemovalPlan plan, int groupsLeftOut)
    {
        try
        {
            var token = _removeCts!.Token;
            var needs = await Task.Run(() => _s.Gate.Needs(plan));
            token.ThrowIfCancellationRequested();
            var confirmed = new Confirmations();

            // 0. Look-alikes are different photos, not copies: ask once before removing the others of a look-alike group.
            if (plan.Groups.Any(g => g.Group.Kind == GroupKind.LookAlike))
            {
                int n = plan.PhotoCount;
                var choice = await _dialogs.ConfirmAsync(new ConfirmRequest(
                    "Remove look-alike photos?",
                    $"These are different photos that only look alike. Remove the other {(n == 1 ? "photo" : $"{n:N0} photos")} and keep the one with the star?",
                    $"Remove {(n == 1 ? "1 photo" : $"{n:N0} photos")}", null, "Cancel", DialogChoice.Cancel,
                    WhereRemovedPhotosGo));
                token.ThrowIfCancellationRequested();
                if (choice != DialogChoice.Primary) { ResultText = "Nothing was removed."; return; }
            }

            // 1. Removing many exact copies at once: show the summary and ask.
            if (needs.BulkExactCopies)
            {
                var choice = await _dialogs.ConfirmAsync(new ConfirmRequest(
                    "Remove all exact copies?",
                    $"Remove {plan.PhotoCount:N0} exact copies ({Format.Size(plan.Bytes)})? Every group keeps one photo.",
                    $"Remove {plan.PhotoCount:N0} photos", null, "Cancel", DialogChoice.Cancel,
                    WhereRemovedPhotosGo));
                token.ThrowIfCancellationRequested();
                if (choice != DialogChoice.Primary) { ResultText = "Nothing was removed."; return; }
                confirmed = confirmed with { BulkExactCopies = true };
            }

            // 1b. Removing many same-photo copies at once: always asked, even for one group. These are different files that show the
            //     same picture, so the question says so. Groups with a missing date were left out and are named here. Cancel is the default.
            if (needs.BulkSamePhoto)
            {
                int n = plan.PhotoCount;
                var message = $"Remove {Format.Plural(n, "copy", "copies")} ({Format.Size(plan.Bytes)})? These are the same picture saved in different ways, not identical files. Each group keeps the photo with the star.";
                if (groupsLeftOut > 0)
                    message += $" {Format.Plural(groupsLeftOut, "group", "groups")} with missing date info {(groupsLeftOut == 1 ? "is" : "are")} left for you to check one by one.";
                var choice = await _dialogs.ConfirmAsync(new ConfirmRequest(
                    "Remove all same-photo copies?", message,
                    $"Remove {Format.Plural(n, "photo", "photos")}", null, "Cancel", DialogChoice.Cancel,
                    WhereRemovedPhotosGo));
                token.ThrowIfCancellationRequested();
                if (choice != DialogChoice.Primary) { ResultText = "Nothing was removed."; return; }
                confirmed = confirmed with { BulkSamePhoto = true };
            }

            // 2. Photos in a shared folder: a second, clearly worded question. "Skip" is the safe default.
            if (needs.SharedPhotos.Count > 0)
            {
                var folders = needs.SharedPhotos.Select(f => Path.GetFileName(f.Folder.TrimEnd('\\')) is { Length: > 0 } n ? n : f.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                int k = needs.SharedPhotos.Count;
                var where = folders.Count == 1 ? $" (\"{folders[0]}\")" : "";
                var choice = await _dialogs.ConfirmAsync(new ConfirmRequest(
                    "Photos in a shared folder",
                    $"{(k == 1 ? "1 of these photos is" : $"{k:N0} of these photos are")} in a shared folder{where}. Other people who use that folder will lose {(k == 1 ? "it" : "them")} too. Remove {(k == 1 ? "it" : "them")} as well?",
                    "Remove from shared folder too", "Skip shared photos", "Cancel", DialogChoice.Secondary,
                    folders.Count > 1 ? "Shared folders: " + string.Join(", ", folders) : null));
                token.ThrowIfCancellationRequested();
                if (choice == DialogChoice.Cancel) { ResultText = "Nothing was removed."; return; }
                if (choice == DialogChoice.Secondary)
                {
                    plan = plan.Without((pg, t) => pg.Group.IsShared(t) || _s.Shared.IsShared(t.Path));
                    if (plan.PhotoCount == 0)
                    {
                        ResultText = "Nothing was removed: all the photos to remove are in shared folders, which you chose to skip.";
                        return;
                    }
                    needs = await Task.Run(() => _s.Gate.Needs(plan));
                    token.ThrowIfCancellationRequested();
                }
                else confirmed = confirmed with { SharedFolders = true };
            }

            // 3. Cloud folders: one line, asked once for the whole action.
            if (needs.CloudServices.Count > 0)
            {
                var services = JoinWithAnd(needs.CloudServices.Select(c => c.Service).ToList());
                var choice = await _dialogs.ConfirmAsync(new ConfirmRequest(
                    "Photos in the cloud",
                    $"These photos are in {services}. Removing them here also removes them from {services} online and from your other devices. Remove them?",
                    "Remove", null, "Cancel", DialogChoice.Cancel));
                token.ThrowIfCancellationRequested();
                if (choice != DialogChoice.Primary) { ResultText = "Nothing was removed."; return; }
                confirmed = confirmed with { Cloud = true };
            }

            // 4. Go.
            token.ThrowIfCancellationRequested();
            ProgressIndeterminate = false; ProgressValue = 0; ProgressMax = Math.Max(1, plan.PhotoCount);
            StatusText = $"Removing {plan.PhotoCount:N0} {(plan.PhotoCount == 1 ? "photo" : "photos")}…";
            ChangeCommands();
            var progress = new Progress<RemovalProgress>(p => { if (!_isStopping) { ProgressMax = Math.Max(1, p.Total); ProgressValue = p.Done; StatusText = $"Removing {Math.Min(p.Done + 1, p.Total):N0} of {p.Total:N0}…"; } });
            var result = await Task.Run(() => _s.Removal.Execute(plan, confirmed, progress, token));
            ApplyRemoval(result);
        }
        catch (OperationCanceledException) { ResultText = "Nothing was removed."; }
        catch (Exception ex)
        {
            _s.Log.Error("Removal failed unexpectedly", ex);
            ResultText = "Something unexpected went wrong while removing: " + ex.Message + " Some photos may have been removed: check the Undo list. Details were saved to the log.";
            ResultIsProblem = true;
        }
        finally
        {
            StatusText = "";
            _isStopping = false;
            _removeCts?.Dispose(); _removeCts = null;
            IsBusy = false;
        }
    }

    private void ApplyRemoval(RemovalResult result)
    {
        LastRemoval = result;
        Raise(nameof(LastRemoval)); Raise(nameof(ResultDetailsVisibility));
        if (result.Refusal != null)
        {
            ResultText = "PhotoTwins did not remove anything. " + result.Refusal.Replace(" Nothing was removed.", "");
            ResultIsProblem = true;
            return;
        }

        var gone = result.Removed.Select(e => e.OriginalPath).ToList();
        if (gone.Count > 0)
        {
            try { _s.Cache.RemovePaths(gone); } catch (Exception ex) { _s.Log.Warn("Couldn't update the memory of scans after a removal: " + ex.Message); }
            foreach (var g in _allExact.Concat(_allSame).Concat(_allLook)) { g.Model.RemoveFiles(gone); g.Refresh(); }
            ShowStillGroups();
            UpdateHeadline();
        }

        var parts = new List<string>();
        if (gone.Count > 0) parts.Add($"Removed {Format.Plural(gone.Count, "photo", "photos")} ({Format.Size(result.BytesRemoved)}).");
        else parts.Add("Nothing was removed.");
        if (result.RecycledCount > 0) parts.Add(result.MovedToFolderCount == 0 ? "They're in the Recycle Bin: the space comes back when you empty it." : $"{Format.Plural(result.RecycledCount, "photo is", "photos are")} in the Recycle Bin: the space comes back when you empty it.");
        foreach (var why in result.Removed.Where(e => e.Method == RemoveMethod.Folder).GroupBy(e => e.Reason ?? "their drive has no Recycle Bin"))
            parts.Add($"{Format.Plural(why.Count(), "photo was", "photos were")} moved to a \"Duplicates to delete\" folder in the folder {(why.Count() == 1 ? "it" : "they")} came from, because {why.Key}.");
        if (result.Failed.Count > 0) parts.Add($"Couldn't remove {Format.Plural(result.Failed.Count, "photo", "photos")}.");
        if (result.Dropped.Count > 0) parts.Add($"{Format.Plural(result.Dropped.Count, "photo was", "photos were")} left out because {(result.Dropped.Count == 1 ? "it" : "they")} changed or disappeared after the scan.");
        if (result.Cancelled) parts.Add("You stopped it part way: what was done stays done.");
        if (gone.Count > 0) parts.Add("You can put them back from Undo.");
        ResultText = string.Join(" ", parts);
        ResultIsProblem = result.Failed.Count > 0 || result.Dropped.Count > 0;
    }

    /// <summary>The lists on screen: every group of this scan, in its original order, that still has two or more photos.</summary>
    private void ShowStillGroups()
    {
        ExactGroups = _allExact.Where(g => g.Model.StillAGroup).ToList();
        SameGroups = _allSame.Where(g => g.Model.StillAGroup).ToList();
        LookAlikeGroups = _allLook.Where(g => g.Model.StillAGroup).ToList();
    }

    /// <summary>
    /// Photos put back by Undo reappear in the group, the tab and the place they were removed from (no rescan), with the star where it
    /// was and the headline updated. A photo whose group isn't on this screen any more (a newer scan replaced it) is counted as such.
    /// </summary>
    public RestoreOutcome RestorePhotos(IReadOnlyList<RestoredPhoto> restoredPhotos)
    {
        var restored = restoredPhotos.Select(r => r.Entry).ToList();
        var paths = restored.Select(e => e.OriginalPath).ToList();
        var now = restoredPhotos.Where(r => r.Now != null).GroupBy(r => r.Entry.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Now!, StringComparer.OrdinalIgnoreCase);
        var back = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The photo is back on disk and was already looked at (off the screen thread, by the Undo window). It only goes back into its group if it
        // is the very photo the scan saw: same size AND same SHA-256. Then it takes its up-to-date Windows file ID and time, so a later removal's
        // safety check sees the same file. Anything else (changed, online-only, unreadable) is left for the next scan. Nothing is read here.
        PhotoFile? Fresh(PhotoFile old)
        {
            if (!now.TryGetValue(old.Path, out var n)) return null;
            if (n.Size != old.Size || !n.Sha256.AsSpan().SequenceEqual(old.Sha256)) return null;
            return old with { Identity = n.Identity, LastWriteUtcTicks = n.LastWriteUtcTicks };
        }
        foreach (var g in _allExact.Concat(_allSame).Concat(_allLook))
        {
            var done = g.Model.RestoreFiles(paths, Fresh);
            foreach (var p in done) back.Add(p);
            if (done.Count > 0) g.Refresh();
        }
        ShowStillGroups();
        UpdateHeadline();
        int inGroups = restored.Count(e => back.Contains(e.OriginalPath));
        int notInList = restored.Count - inGroups;
        ResultText = $"Put back {Format.Plural(restored.Count, "photo", "photos")}." + (notInList == 0
            ? (restored.Count == 1 ? " It is back in its group." : " They are back in their groups.")
            : $" {inGroups} are back in their groups; {notInList} aren't on this list any more, so scan again to see {(notInList == 1 ? "it" : "them")}.");
        ResultIsProblem = false;
        return new RestoreOutcome(inGroups, notInList);
    }

    /// <summary>After the Undo window closes: photos that were put back aren't in the groups on screen until the next scan.</summary>
    public void AfterUndo(int restored)
    {
        if (restored <= 0) return;
        ResultText = $"Put back {Format.Plural(restored, "photo", "photos")}. Scan again to see {(restored == 1 ? "it" : "them")} in the groups.";
        ResultIsProblem = false;
        LastRemoval = null;
        Raise(nameof(ResultDetailsVisibility));
    }
}
