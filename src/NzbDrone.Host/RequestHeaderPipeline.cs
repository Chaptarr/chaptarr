using Microsoft.AspNetCore.Builder;
using Chaptarr.Http.Authentication;

namespace NzbDrone.Host
{
    public static class RequestHeaderPipeline
    {
        public static IApplicationBuilder UseValidatedForwardedHeaders(this IApplicationBuilder app)
        {
            // Validate both names: a LAN client must not bypass the allow-list with a forged forwarded host.
            app.Use((context, next) =>
            {
                TrustedNetworkPolicy.CaptureRequestOrigin(context);
                return next(context);
            });

            return app.UseHostFiltering()
                      .UseForwardedHeaders()
                      .UseHostFiltering();
        }
    }
}
