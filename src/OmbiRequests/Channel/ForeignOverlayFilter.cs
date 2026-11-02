using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;

namespace OmbiRequests.Channel
{
    /// <summary>
    /// Keeps the poster overlays of other plugins (e.g. Cover Art) off the requests channel. Emby applies
    /// all image enhancers one after another in load order, so their frames and bars would end up on the
    /// channel's posters, below or above the status banner. Every other enhancer is wrapped so that it
    /// does not take the channel's items; for everything else it works unchanged.
    /// </summary>
    public class ForeignOverlayFilter : IServerEntryPoint
    {
        private readonly IImageProcessor imageProcessor;

        public ForeignOverlayFilter(IImageProcessor imageProcessor)
        {
            this.imageProcessor = imageProcessor;
        }

        public void Run()
        {
            // Emby has registered all enhancers before the entry points run.
            var enhancers = imageProcessor.ImageEnhancers;
            if (enhancers.Any(e => e is ChannelExcludingEnhancer))
            {
                return;
            }

            imageProcessor.AddParts(enhancers.Select(e => e is StatusBannerEnhancer ? e : new ChannelExcludingEnhancer(e)).ToList());
        }

        public void Dispose()
        {
        }

        /// <summary>Items of the requests channel: titles, seasons, folders and the channel itself.</summary>
        internal static bool IsChannelItem(BaseItem item)
        {
            if (item == null)
            {
                return false;
            }

            if (item.ExternalId != null && ChannelItemIds.IsOwn(item.ExternalId))
            {
                return true;
            }

            return item is MediaBrowser.Controller.Channels.Channel && item.Name != null && item.Name == OmbiChannel.RegisteredName;
        }
    }

    /// <summary>Another plugin's enhancer, left out for the requests channel (setting "HideOtherOverlays").</summary>
    internal sealed class ChannelExcludingEnhancer : IImageEnhancer
    {
        public ChannelExcludingEnhancer(IImageEnhancer inner)
        {
            Inner = inner;
        }

        public IImageEnhancer Inner { get; }

        public MetadataProviderPriority Priority => Inner.Priority;

        private static bool Enabled => Plugin.Instance?.Configuration.HideOtherOverlays != false;

        public bool Supports(BaseItem item, ImageType imageType)
        {
            return !(Enabled && ForeignOverlayFilter.IsChannelItem(item)) && Inner.Supports(item, imageType);
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType) => Inner.GetConfigurationCacheKey(item, imageType);

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalImageSize) =>
            Inner.GetEnhancedImageSize(item, imageType, imageIndex, originalImageSize);

        public EnhancedImageInfo GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) =>
            Inner.GetEnhancedImageInfo(item, inputFile, imageType, imageIndex);

        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex) =>
            Inner.EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex);

        public override string ToString() => Inner.GetType().Name;
    }
}
