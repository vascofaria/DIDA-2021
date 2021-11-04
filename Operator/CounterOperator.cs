﻿using System;
using System.Collections.Generic;
using DIDAStorageClient;
using DIDAWorker;
using Grpc.Net.Client;

namespace Operator
{
    /*
    public class CounterOperator : IDIDAOperator
    {
        Dictionary<string, DIDAStorageService.DIDAStorageServiceClient> _storageServers =
            new Dictionary<string, DIDAStorageService.DIDAStorageServiceClient>();
        Dictionary<string, GrpcChannel> _storageChannels =
             new Dictionary<string, GrpcChannel>();
        delLocateStorageId _locationFunction;

        public CounterOperator()
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        }

        // this operator increments the storage record identified in the metadata record every time it is called.
        string IDIDAOperator.ProcessRecord(DIDAMetaRecord meta, string input, string previousOperatorOutput)
        {
            Console.WriteLine("input string was: " + input);
            Console.Write("reading data record: " + meta.id + " with value: ");
            string storageServer = _locationFunction(meta.id.ToString(), OperationType.ReadOp).serverId;
            var val = _storageServers[storageServer].read(new DIDAReadRequest { Id = meta.id.ToString(), Version = new DIDAStorageClient.DIDAVersion { VersionNumber = -1, ReplicaId = -1 } });
            string storedString = val.Val;
            Console.WriteLine(storedString);
            int requestCounter = Int32.Parse(storedString);
            requestCounter++;
            storageServer = _locationFunction(meta.id.ToString(), OperationType.WriteOp).serverId;
            _storageServers[storageServer].write(new DIDAWriteRequest { Id = meta.id.ToString(), Val = requestCounter.ToString() });
            Console.WriteLine("writing data record:" + meta.id + "with new value: " + requestCounter.ToString());
            return requestCounter.ToString();
        }

        void IDIDAOperator.ConfigureStorage(DIDAStorageNode[] storageReplicas, delLocateStorageId locationFunction)
        {
            DIDAStorageService.DIDAStorageServiceClient client;
            GrpcChannel channel;

            _locationFunction = locationFunction;

            foreach (DIDAStorageNode n in storageReplicas)
            {
                channel = GrpcChannel.ForAddress("http://" + n.host + ":" + n.port + "/");
                client = new DIDAStorageService.DIDAStorageServiceClient(channel);
                _storageServers.Add(n.serverId, client);
                _storageChannels.Add(n.serverId, channel);
            }
        }
    }
    */
}
