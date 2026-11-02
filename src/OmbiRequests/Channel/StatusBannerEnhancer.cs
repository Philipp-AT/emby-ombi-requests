using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using SkiaSharp;

namespace OmbiRequests.Channel
{
    /// <summary>
    /// Lays a status banner ("Not in Emby yet - mark as favorite to request", "Requested", ...) over the
    /// posters of Ombi channel items. Emby discovers image enhancers in plugins and caches the result
    /// per <see cref="GetConfigurationCacheKey"/>, so a status change produces a new image.
    /// </summary>
    public class StatusBannerEnhancer : IImageEnhancer
    {
        private static readonly ConcurrentDictionary<string, bool> ResourceExists = new ConcurrentDictionary<string, bool>();

        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem item, ImageType imageType)
        {
            return imageType == ImageType.Primary && GetBannerResource(item) != null;
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            // Bump the version whenever the banner images or their rendering change,
            // Emby caches enhanced images by this key.
            return "ombibanner4|" + GetBannerResource(item);
        }

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalImageSize)
        {
            var scale = PosterBanner.GetScale(originalImageSize.Width);
            return new ImageSize(originalImageSize.Width * scale, originalImageSize.Height * scale);
        }

        public EnhancedImageInfo GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex)
        {
            return new EnhancedImageInfo();
        }

        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex)
        {
            var resource = GetBannerResource(item);

            // Emby logs failures and falls back to the original image.
            return Task.Run(() => PosterBanner.Apply(inputFile, outputFile, resource));
        }

        private static string GetBannerResource(BaseItem item)
        {
            if (item?.ExternalId == null || !ChannelItemIds.IsOwn(item.ExternalId))
            {
                return null;
            }

            var plugin = Plugin.Instance;
            var status = item.GetProviderId(ChannelItemIds.StatusProviderKey);
            if (plugin == null || !plugin.Configuration.EnablePosterBanners || string.IsNullOrEmpty(status))
            {
                return null;
            }

            var name = $"{typeof(Plugin).Namespace}.Images.banner-{status}-{(plugin.RequestService.IsGerman ? "de" : "en")}.png";
            var exists = ResourceExists.GetOrAdd(name, n => typeof(Plugin).Assembly.GetManifestResourceInfo(n) != null);
            return exists ? name : null;
        }
    }

    /// <summary>Kept separate so that a missing SkiaSharp only fails the enhancement, not the plugin.</summary>
    internal static class PosterBanner
    {
        /// <summary>Small posters (older items still have w342 images) are enlarged so the banner text stays sharp.</summary>
        private const double MinWidth = 780;
        private const double MaxScale = 3;

        public static double GetScale(double width)
        {
            return width > 0 && width < MinWidth ? Math.Min(MinWidth / width, MaxScale) : 1;
        }

        public static void Apply(string inputFile, string outputFile, string bannerResource)
        {
            var tempFile = outputFile + ".tmp";
            try
            {
                using (var poster = SKBitmap.Decode(inputFile) ?? throw new InvalidDataException("Cannot decode " + inputFile))
                using (var bannerStream = typeof(Plugin).Assembly.GetManifestResourceStream(bannerResource))
                using (var banner = SKBitmap.Decode(bannerStream))
                {
                    var scale = GetScale(poster.Width);
                    var targetWidth = (int)Math.Round(poster.Width * scale);
                    var targetHeight = (int)Math.Round(poster.Height * scale);
                    Render(poster, banner, targetWidth, targetHeight, outputFile, tempFile);
                }

                // Emby reuses an existing output file as cache, so it must never be half written.
                if (File.Exists(outputFile))
                {
                    File.Delete(outputFile);
                }

                File.Move(tempFile, outputFile);
            }
            catch
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }

                throw;
            }
        }

        private static void Render(SKBitmap poster, SKBitmap banner, int targetWidth, int targetHeight, string outputFile, string tempFile)
        {
            using (var result = new SKBitmap(targetWidth, targetHeight))
            using (var canvas = new SKCanvas(result))
            using (var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High })
            {
                canvas.DrawBitmap(poster, SKRect.Create(0, 0, targetWidth, targetHeight), paint);

                // Full width on posters; on wide images capped at a quarter of the height and centered.
                float width = targetWidth;
                var height = width * banner.Height / banner.Width;
                if (height > targetHeight / 4f)
                {
                    height = targetHeight / 4f;
                    width = height * banner.Width / banner.Height;
                }

                canvas.DrawBitmap(banner, SKRect.Create((targetWidth - width) / 2, targetHeight - height, width, height), paint);
                canvas.Flush();

                var format = outputFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
                using (var image = SKImage.FromBitmap(result))
                using (var data = image.Encode(format, 92))
                using (var stream = File.Create(tempFile))
                {
                    data.SaveTo(stream);
                }
            }
        }
    }
}
