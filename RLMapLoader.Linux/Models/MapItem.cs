using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RLMapLoader.Linux.ViewModels;
using RLMapLoader.Services;

namespace RLMapLoader.Linux.Models
{
    public sealed class MapItem : ObservableObject
    {
        private static readonly (string a, string b)[] Palettes =
        {
            ("#1D4ED8", "#38BDF8"), ("#C2410C", "#FB923C"), ("#0F766E", "#2DD4BF"), ("#6D28D9", "#A78BFA"),
            ("#BE185D", "#F472B6"), ("#15803D", "#4ADE80"), ("#B45309", "#FBBF24"), ("#334155", "#94A3B8"),
            ("#0E7490", "#22D3EE"), ("#9F1239", "#FB7185"),
        };

        public MapItem(MapInfo info, MainViewModel owner)
        {
            Info = info;
            Owner = owner;
            int h = 0;
            foreach (char c in info.FileName.ToLowerInvariant()) h = unchecked(h * 31 + c);
            var (a, b) = Palettes[(int)((uint)h % Palettes.Length)];
            PlaceholderBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(a), 0), new GradientStop(Color.Parse(b), 1) },
            };
        }

        /// <summary>The view model, so menus inside cards can reach its commands.</summary>
        public MainViewModel Owner { get; }
        public MapInfo Info { get; private set; }
        public string Path => Info.Path;
        public string FileName => Info.FileName;
        public string DisplayName => Info.DisplayName;
        public string Extension => Info.Extension;
        public long Size => Info.Size;
        public DateTime Modified => Info.Modified;
        public string? ImagePath => Info.ImagePath;
        public bool HasCustomImage => Info.ImageIsCustom;
        public IBrush PlaceholderBrush { get; }

        public string SizeText
        {
            get
            {
                double s = Info.Size;
                string[] u = { "B", "KB", "MB", "GB" };
                int i = 0;
                while (s >= 1024 && i < u.Length - 1) { s /= 1024; i++; }
                return s.ToString(i >= 2 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + u[i];
            }
        }

        public string Subtitle => string.IsNullOrEmpty(Info.Author)
            ? $"{SizeText}  ·  {Info.Modified.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}"
            : $"by {Info.Author}  ·  {SizeText}";

        public string Tooltip => string.IsNullOrEmpty(Info.Description) ? Info.FileName : $"{Info.Description}\n\n{Info.FileName}";

        private Bitmap? _thumbnail;
        public Bitmap? Thumbnail
        {
            get => _thumbnail;
            set { if (Set(ref _thumbnail, value)) OnPropertyChanged(nameof(HasThumbnail)); }
        }
        public bool HasThumbnail => _thumbnail != null;

        private bool _isActive;
        public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

        private bool _isFavorite;
        public bool IsFavorite { get => _isFavorite; set => Set(ref _isFavorite, value); }

        public bool ThumbnailRequested { get; set; }

        public void UpdateInfo(MapInfo info)
        {
            bool imageChanged = !string.Equals(info.ImagePath, Info.ImagePath, StringComparison.Ordinal);
            Info = info;
            if (imageChanged) { Thumbnail = null; ThumbnailRequested = false; }
            OnPropertyChanged(string.Empty);
        }
    }
}
