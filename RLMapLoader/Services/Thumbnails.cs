using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RLMapLoader.Services
{
    public static class Thumbnails
    {
        private static readonly SemaphoreSlim Gate = new(3);

        /// <summary>Decodes an image off the UI thread at a reduced size; never locks the file.</summary>
        public static async Task<ImageSource?> LoadAsync(string path, int decodeWidth)
        {
            await Gate.WaitAsync();
            try
            {
                return await Task.Run<ImageSource?>(() =>
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(path);
                        using var ms = new MemoryStream(bytes);
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        bi.DecodePixelWidth = decodeWidth;
                        bi.StreamSource = ms;
                        bi.EndInit();
                        bi.Freeze();
                        return bi;
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"Thumbnail failed {path}: {ex.Message}");
                        return null;
                    }
                });
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
