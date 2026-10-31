using System.Windows;
using System.Windows.Media;
using PhotoTwins.Core.Imaging;

namespace PhotoTwins;

/// <summary>The full-size viewer: one photo of a group at a time, only ever read, never changed.</summary>
public sealed class ViewerViewModel : ObservableObject
{
    private readonly IReadOnlyList<CardVm> _cards;
    private readonly AppServices _s;
    private int _index;
    private int _loadId;
    private ImageSource? _image;
    private string _status = "";

    public ViewerViewModel(IReadOnlyList<CardVm> cards, int index, AppServices services)
    {
        _cards = cards;
        _s = services;
        _index = Math.Clamp(index, 0, Math.Max(0, cards.Count - 1));
        PreviousCommand = new RelayCommand(() => _ = GoAsync(-1), () => _index > 0);
        NextCommand = new RelayCommand(() => _ = GoAsync(1), () => _index < _cards.Count - 1);
    }

    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }

    public CardVm Current => _cards[_index];
    public string PositionText => _cards.Count > 1 ? $"{_index + 1} of {_cards.Count} in this group" : "";
    public string Heading => Current.NameText;
    public string Details => $"{Current.SizeAndDims} · {Current.TakenText}";
    public string Folder => Current.FolderText;
    public string Badge => Current.Badge;
    public Visibility BadgeVisibility => Current.BadgeVisibility;

    public ImageSource? Image { get => _image; private set => Set(ref _image, value); }
    public string Status { get => _status; private set { Set(ref _status, value); Raise(nameof(StatusVisibility)); } }
    public Visibility StatusVisibility => _status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Task GoAsync(int delta)
    {
        int next = _index + delta;
        if (next < 0 || next >= _cards.Count) return Task.CompletedTask;
        _index = next;
        foreach (var n in new[] { nameof(Current), nameof(PositionText), nameof(Heading), nameof(Details), nameof(Folder), nameof(Badge), nameof(BadgeVisibility) }) Raise(n);
        PreviousCommand.Changed(); NextCommand.Changed();
        return LoadCurrentAsync();
    }

    /// <summary>Shows the small picture straight away (if it has been drawn), then the full-size one when it's ready.</summary>
    public async Task LoadCurrentAsync()
    {
        int id = ++_loadId;
        var card = Current;
        Image = _s.Thumbnails.TryGet(card.ThumbnailKey, card.FullPath);
        Status = "Loading the full-size picture…";
        try
        {
            var image = await Task.Run(() =>
            {
                var bytes = PhotoPictures.ReadBytes(_s.Fs, card.FullPath);
                var bmp = PhotoPictures.MakeBmp(bytes, 4096, out _, out _);
                return ThumbnailService.ToImage(bmp);
            });
            if (id != _loadId) return;   // the user already moved on
            Image = image;
            Status = "";
        }
        catch (Exception ex)
        {
            if (id != _loadId) return;
            var why = PhotoPictures.WhyNotShown(ex);
            _s.Log.Warn($"Couldn't show {card.FullPath} full size: {why}");
            Image = null;
            Status = "Couldn't show this picture. " + why;
        }
    }
}
