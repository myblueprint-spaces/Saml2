using System.Collections.Generic;
using Sustainsys.Saml2.Configuration;
using Sustainsys.Saml2.Metadata;

namespace Sustainsys.Saml2.Internal
{
    static class OptionsExtensions
    {
        /// <summary>
        /// Looks up an idp through the GetIdentityProvider notification, returning
        /// null instead of throwing if the idp is unknown.
        /// </summary>
        public static IdentityProvider TryGetIdentityProvider(this IOptions options, EntityId entityId)
        {
            try
            {
                return options.Notifications.GetIdentityProvider(entityId, new Dictionary<string, string>(), options);
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        }
    }
}
