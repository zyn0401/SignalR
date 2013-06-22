using System.Threading.Tasks;
using Microsoft.AspNet.SignalR.Hubs;

namespace Microsoft.AspNet.SignalR.Tests.Common.Hubs
{
    public class EchoHub : Hub
    {
        public override Task OnConnected()
        {
            return Clients.Caller.echo("message");
        }

        public Task EchoCallback(string message)
        {
            return Clients.Caller.echo(message);
        }

        public string EchoReturn(string message)
        {
            return message;
        }
    }
}
