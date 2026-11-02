using System;

namespace OmbiRequests.Channel
{
    /// <summary>
    /// Thrown by the channel's constructor when the channel is switched off, so that Emby does not
    /// register it. Emby logs it as "Error creating ...", hence the explanatory message.
    /// </summary>
    public class ChannelDisabledException : Exception
    {
        public ChannelDisabledException()
            : base("The requests channel is switched off in the Ombi Requests plugin settings, so it is not loaded. This is intended, not an error.")
        {
        }
    }
}
