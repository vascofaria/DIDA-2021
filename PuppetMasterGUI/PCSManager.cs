using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;


namespace PuppetMasterGUI
{
    class PCSManager
    {

        private string url;
        private PCSService.PCSServiceClient client;

        private string schedulerUrl;
        private SchedulerService.SchedulerServiceClient schClient;

        public PCSManager(string url)
        {
            this.url = url;
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            GrpcChannel channel = GrpcChannel.ForAddress(url);
            client = new PCSService.PCSServiceClient(channel);
        }

        public void SetScheduler(string schedulerUrl)
        {
            this.schedulerUrl = schedulerUrl;
            GrpcChannel channelScheduler = GrpcChannel.ForAddress(schedulerUrl);
            schClient = new SchedulerService.SchedulerServiceClient(channelScheduler);
        }

        public void createWorkerNode(string serverId, string url, int gossipDelay, bool debug, string logURL) // return the Node
        {
            // GRPC call to PCS in order to create ...
            try
            {
                var reply = client.CreateWorkerNode(
                    new CreateWorkerNodeRequest
                    {
                        ServerId = serverId,
                        Url = url,
                        Delay = gossipDelay,
                        Debug = debug,
                        LogURL = logURL
                    }
                );
                if (reply.Okay)
                {
                    Console.WriteLine("Okay");
                }
                else
                {
                    Console.WriteLine("Not Okay");
                }

                var request = new AddWorkerNodeRequest
                {
                    ServerId = serverId,
                    Url = url,
                };
                Console.WriteLine(request.ServerId + " " + request.Url);

                var replyScheduler = schClient.AddWorkerNode(request);
                if (replyScheduler.Okay)
                {
                    Console.WriteLine("Okay Scheduler");
                }
                else
                {
                    Console.WriteLine("Not Okay Scheduler");
                }
            } catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        public void createStorageNode(string serverId, string url, int gossipDelay, int replicaId)
        {
            try
            {
                var reply = client.CreateStorageNode(
                    new CreateStorageNodeRequest
                    {
                        ServerId = serverId,
                        Url = url,
                        GossipDelay = gossipDelay,
                        ReplicaId = replicaId
                    }
                );
                if (reply.Okay)
                {
                    Console.WriteLine("Okay");
                }
                else
                {
                    Console.WriteLine("Not Okay");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        public void Crash(string serverId)
        {
            var reply = client.NukeStorage(
                new NukeRequest
                {
                    ServerId = serverId
                }
                );
            if (reply.Okay)
            {
                Console.WriteLine("Nuked Storage: " + serverId);
            }
            else
            {
                Console.WriteLine("Not Nuked Storage: " + serverId);
            }
        }

        public void exit()
        {
            client.Nuke(new NukeAllRequest{});
        }

        public static string GetLocalIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            throw new Exception("No network adapters with an IPv4 address in the system!");
        }
    }
}
