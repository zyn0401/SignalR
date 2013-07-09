using System.Collections.Generic;
using System.Diagnostics;
using System.Web.Cors;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Samples;
using Microsoft.AspNet.SignalR.Tracing;
using Owin;

namespace Microsoft.AspNet.SelfHost.Samples
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {

            var policy = new CorsPolicy
            {
                AllowAnyHeader = true,
                AllowAnyMethod = true,
                SupportsCredentials = true,
            };

            policy.Origins.Add("http://localhost:8081");

            app.UseCors(policy);

            app.MapConnection<RawConnection>("/raw-connection");
            app.MapHubs();

            // Turn tracing on programmatically
            GlobalHost.TraceManager.Switch.Level = SourceLevels.Information;
        }
    }
}
