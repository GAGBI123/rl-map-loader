using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using RLMapLoader.Services;
using RLMapLoader.ViewModels;

namespace RLMapLoader.Models
{
    public sealed class MapItem : ObservableObject
    {
        private static readonly (string a, string b)[] Palettes =
        {
            ("#1D4ED8", "#38BDF8"), ("#C2410C", "#FB923C"), ("#0F766E", "#2DD4BF"), ("#6D28D9", "#A78BFA"),
            ("#BE185D", "#F472B6"), ("#15803D", "#4ADE80"), ("#B45309", "#FBBF24"), ("#334155", "#94A3B8"),
            ("#0E7490", "#22D3EE"), ("#9F1239", "#FB7185"),
        };

        public MapItem(MapInfo info)
        {
            Info = info;
            var words = info.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Initials = string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
            int h = 0;
            foreach (char c in info.FileName.ToLowerInvariant()) h = unchecked(h * 31 + c);
            var (a, b) = Palettes[(int)((uint)h % Palettes.Length)];
            var brush = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            PlaceholderBrush = brush;
        }

        public MapInfo Info { get; private set; }
        public string Path => Info.Path;
        public string FileName => Info.FileName;
        public string DisplayName => Info.DisplayName;
        public string Extension => Info.Extension;
        public long Size => Info.Size;
        public DateTime Modified => Info.Modified;
        public string? ImagePath => Info.ImagePath;
        public bool HasCustomImage => Info.ImageIsCustom;
        public string Initials { get; }
        public Brush PlaceholderBrush { get; }

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

        public string DateText => Info.Modified.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        public string? Author => Info.Author;
        public string? Description => Info.Description;
        public string Subtitle => string.IsNullOrEmpty(Info.Author)
            ? $"{SizeText}  ·  {DateText}"
            : $"by {Info.Author}  ·  {SizeText}";
        public string Tooltip => string.IsNullOrEmpty(Info.Description)
            ? Info.FileName
            : $"{Info.Description}\n\n{Info.FileName}";

        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail
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
            bool imageChanged = !string.Equals(info.ImagePath, Info.ImagePath, StringComparison.OrdinalIgnoreCase);
            Info = info;
            if (imageChanged) { Thumbnail = null; ThumbnailRequested = false; }
            OnPropertyChanged(string.Empty);
        }
    }
}
