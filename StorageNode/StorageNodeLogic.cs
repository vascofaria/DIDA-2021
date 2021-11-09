﻿using DIDAStorage;
using Google.Protobuf.Collections;
using Grpc.Core;
using Grpc.Net.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Timers;
using CHashing;
using System.Threading.Tasks;

namespace StorageNode
{
    class StorageNodeLogic : IDIDAStorage
    {
        public static int MAX_VERSIONS_STORED = 5;

        private Dictionary<string, StorageNodeStruct> storageNodes = new Dictionary<string, StorageNodeStruct>();

        // if uivi cant write key during consensus
        private Dictionary<string, bool> consensusLock = new Dictionary<string, bool>();

        // Must be a queue instead of a list, in order to pop old values
        private Dictionary<string, List<DIDAStorage.DIDARecord>> storage = new Dictionary<string, List<DIDAStorage.DIDARecord>>();
        private ReplicaManager replicaManager;
        private string serverId;
        private int replicaId;
        private int gossipDelay;
        private Timer timer;
        private bool startedGossip = false;
        public StorageNodeLogic(string serverId, int replicaId, int gossipDelay)
        {
            this.serverId = serverId;
            this.replicaId = replicaId;
            this.gossipDelay = gossipDelay;
            this.replicaManager = new ReplicaManager(this.replicaId, MAX_VERSIONS_STORED);
            this.timer = new Timer();
            timer.Interval = gossipDelay;
            timer.Elapsed += new ElapsedEventHandler(OnTimedEvent);
            timer.AutoReset = true;
            timer.Enabled = false;
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        }


        public StatusReply Status()
        {
            lock (storage)
            {
                Console.WriteLine("This is my Status: " + replicaId);

                foreach (KeyValuePair<string, List<DIDAStorage.DIDARecord>> pair in storage)
                {
                    Console.WriteLine("Key: " + pair.Key);
                    foreach (DIDAStorage.DIDARecord rec in pair.Value)
                    {
                        Console.WriteLine("Value: " + rec.val + ", Version: (" + rec.version.versionNumber + ", " + rec.version.replicaId + ")");
                    }
                }
            }

            return new StatusReply { };
        }

        public DIDAStorage.DIDARecord Read(string id, DIDAStorage.DIDAVersion version)
        {
            List<DIDAStorage.DIDARecord> recordValues;
            DIDAStorage.DIDARecord value = new DIDAStorage.DIDARecord
            { // null value
                id = id,
                val = "",
                version = new DIDAStorage.DIDAVersion
                {
                    replicaId = -1,
                    versionNumber = -1
                }
            };

            lock (this)
            {
                Console.WriteLine("Reading... " + id);

                if (storage.TryGetValue(id, out recordValues))
                {
                    if (version.replicaId == -1 && version.versionNumber == -1)
                    { // Null version
                        int size = recordValues.Count;
                        value = recordValues[size - 1];
                        // We suppose the list is ordered
                    }
                    else
                    { // Specified version
                        foreach (DIDAStorage.DIDARecord record in recordValues)
                        {
                            // Get the most recent or the indicated version
                            if (
                                record.version.replicaId == version.replicaId &&
                                record.version.versionNumber == version.versionNumber)
                            {
                                value = record;
                                break;
                            }
                        }
                    }
                }
            }

            return value;
        }

        public DIDAStorage.DIDAVersion UpdateIfValueIs(string id, string oldvalue, string newvalue)
        {
            lock (this)
            {
                if (consensusLock[id])
                {
                    return new DIDAStorage.DIDAVersion
                    {
                        replicaId = -1,
                        versionNumber = -1
                    };
                }
                consensusLock[id] = true;

            }

            // storageNodes never edited
            List<string> storageIds = new List<string>();
            foreach (StorageNodeStruct sns in storageNodes.Values)
            {
                storageIds.Add(sns.serverId);
            }
            storageIds.Add(this.serverId);
            ConsistentHashing consistentHashing = new ConsistentHashing(storageIds);
            List<string> setOfReplicas = consistentHashing.ComputeSetOfReplicas(id);


            // List<AsyncUnaryCall<LockAndPullReply>> lockingTasks = new List<AsyncUnaryCall<LockAndPullReply>>();
            List<LockAndPullReply> replies = new List<LockAndPullReply>();
            LockAndPullRequest lockRequest;

            foreach (string sId in setOfReplicas)
            {
                // Dont request to ourselves
                if (sId == this.serverId) continue;

                lockRequest = new LockAndPullRequest
                {
                    Key = id
                };

                try
                {
                    replies.Add(storageNodes[sId].uiviClient.LockAndPull(lockRequest));
                }
                catch (RpcException e)
                {
                    Console.WriteLine("Status Code: " + e.StatusCode);
                    // if (e.StatusCode == StatusCode.)
                }

                // lockingTasks.Add(storageNodes[sId].uiviClient.LockAndPullAsync(request));
            }

            // TODO: TIMEOUTSS

            // await Task.WhenAll(lockingTasks.Select(res => res.ResponseAsync));

            bool abort = false;

            CommitPhaseRequest commitRequest;
            DIDAStorage.DIDAVersion nextVersion;
            lock (this)
            {
                // This replica starts being the max version
                DIDARecord maxRecord;
                if (this.storage.ContainsKey(id) && this.storage[id].Count > 0)
                {
                    DIDAStorage.DIDARecord record = this.storage[id][this.storage[id].Count - 1];
                    maxRecord = new DIDARecord
                    {
                        Id = record.id,
                        Val = record.val,
                        Version = new DIDAVersion
                        {
                            VersionNumber = record.version.versionNumber,
                            ReplicaId = record.version.replicaId
                        }
                    };
                }
                else
                {
                    maxRecord = new DIDARecord
                    {
                        Id = id,
                        Val = "1",
                        Version = new DIDAVersion
                        {
                            VersionNumber = -1,
                            ReplicaId = -1
                        }
                    };
                }


                foreach (LockAndPullReply reply in replies)
                {
                    if (reply.AlreadyLocked)
                    {
                        abort = true;
                        break;

                    }
                    if (this.replicaManager.IsVersionBigger(reply.Record.Version, maxRecord.Version))
                    {
                        maxRecord = reply.Record;
                    }
                }

                if (!abort)
                {
                    nextVersion = new DIDAStorage.DIDAVersion
                    {
                        versionNumber = maxRecord.Version.VersionNumber + 1,
                        replicaId = this.replicaId
                    };

                    DIDAStorage.DIDARecord nextRecord = new DIDAStorage.DIDARecord
                    {
                        id = maxRecord.Id,
                        val = newvalue,
                        version = nextVersion
                    };


                    if (maxRecord.Val == oldvalue) // If VALUE IS
                    {

                        if (storage.ContainsKey(id))
                        {
                            List<DIDAStorage.DIDARecord> items = storage[id];

                            // If Queue is full
                            if (items.Count == MAX_VERSIONS_STORED)
                            {
                                items.RemoveAt(0);
                            }
                            items.Add(nextRecord);
                            this.replicaManager.AddTimeStamp(this.replicaId, id, nextVersion);
                        }
                        else
                        {

                            //storage.Add(id, new List<DIDAStorage.DIDARecord>());
                            this.AddNewKey(id);
                            storage[id].Add(nextRecord);
                            this.replicaManager.CreateNewTimeStamp(
                                this.replicaId,
                                id,
                                nextVersion
                            );
                        }

                        commitRequest = new CommitPhaseRequest
                        {
                            CanCommit = true,
                            Record = maxRecord
                        };
                    }
                    else
                    {
                        commitRequest = new CommitPhaseRequest
                        {
                            CanCommit = false,
                            Record = maxRecord
                        };

                        nextVersion = new DIDAStorage.DIDAVersion
                        {
                            replicaId = -1,
                            versionNumber = -1
                        };
                    }
                }
                else
                {
                    commitRequest = new CommitPhaseRequest
                    {
                        CanCommit = false,
                        Record = maxRecord
                    };

                    nextVersion = new DIDAStorage.DIDAVersion
                    {
                        replicaId = -1,
                        versionNumber = -1
                    };

                }

            }

            foreach (string sId in setOfReplicas)
            {
                // Dont request to ourselves
                if (sId == this.serverId) continue;

                try
                {
                    // await ???
                    storageNodes[sId].uiviClient.CommitPhaseAsync(commitRequest);
                }
                catch (RpcException e)
                {
                    Console.WriteLine("Status Code: " + e.StatusCode);
                }
            }

            consensusLock[id] = false;

            return nextVersion;
        }

        public LockAndPullReply LockAndPull(LockAndPullRequest request)
        {
            DIDARecord maxRecord;

            //TODO add timeout
            lock (this)
            {
                if (consensusLock[request.Key])
                {
                    return new LockAndPullReply { AlreadyLocked = true };
                }
                consensusLock[request.Key] = true;

                if (this.storage.ContainsKey(request.Key) && this.storage[request.Key].Count > 0)
                {
                    DIDAStorage.DIDARecord record = this.storage[request.Key][this.storage[request.Key].Count - 1];
                    maxRecord = new DIDARecord
                    {
                        Id = record.id,
                        Val = record.val,
                        Version = new DIDAVersion
                        {
                            VersionNumber = record.version.versionNumber,
                            ReplicaId = record.version.replicaId
                        }
                    };
                }
                else
                {
                    maxRecord = new DIDARecord
                    {
                        Id = request.Key,
                        Val = "1",
                        Version = new DIDAVersion
                        {
                            VersionNumber = -1,
                            ReplicaId = -1
                        }
                    };
                }
            }

            return new LockAndPullReply { AlreadyLocked = false, Record = maxRecord };
        }

        public CommitPhaseReply CommitPhase(CommitPhaseRequest request)
        {
            lock (this)
            {
                consensusLock[request.Record.Id] = false;

                if (!this.storage.ContainsKey(request.Record.Id))
                {
                    //this.storage.Add(request.Record.Id, new List<DIDAStorage.DIDARecord>());
                    this.AddNewKey(request.Record.Id);

                    this.replicaManager.CreateNewTimeStamp(this.replicaId, request.Record.Id, new DIDAStorage.DIDAVersion { versionNumber = -1, replicaId = -1 });
                }

                for (int i = 0; i < this.storage[request.Record.Id].Count; i++)
                {
                    DIDAStorage.DIDAVersion newVersion = new DIDAStorage.DIDAVersion
                    {
                        replicaId = request.Record.Version.ReplicaId,
                        versionNumber = request.Record.Version.VersionNumber
                    };

                    if (this.replicaManager.IsVersionBigger(newVersion, this.storage[request.Record.Id][i].version))
                    {
                        DIDAStorage.DIDARecord newRecord = new DIDAStorage.DIDARecord
                        {
                            id = request.Record.Id,
                            version = newVersion,
                            val = request.Record.Val
                        };
                        this.storage[request.Record.Id].Insert(i, newRecord);
                        if (this.storage[request.Record.Id].Count > MAX_VERSIONS_STORED)
                        {
                            this.storage[request.Record.Id].RemoveAt(0);
                        }

                        this.replicaManager.AddTimeStamp(replicaId, request.Record.Id, newVersion);

                        break;
                    }
                }
            }

            return new CommitPhaseReply { Okay = true };
        }

        public DIDAStorage.DIDAVersion Write(string id, string val)
        {
            DIDAStorage.DIDAVersion didaVersion;
            List<DIDAStorage.DIDARecord> recordValues;

            lock (this)
            {

                if (consensusLock[id])
                {
                    return new DIDAStorage.DIDAVersion
                    {
                        replicaId = -1,
                        versionNumber = -1
                    };
                }

                if (!this.startedGossip)
                {
                    this.startedGossip = true;
                    timer.Start();
                }
                Console.WriteLine("Writing... " + id + " - " + val);

                // Get the greater version
                int greaterVersionNumber = 0;
                if (storage.TryGetValue(id, out recordValues))
                {
                    int size = recordValues.Count;
                    greaterVersionNumber = recordValues[size - 1].version.versionNumber;
                }

                didaVersion = new DIDAStorage.DIDAVersion
                {
                    replicaId = this.replicaId,
                    versionNumber = ++greaterVersionNumber
                };

                DIDAStorage.DIDARecord didaRecord = new DIDAStorage.DIDARecord
                {
                    id = id,
                    version = didaVersion,
                    val = val
                };

                if (storage.ContainsKey(id))
                {
                    List<DIDAStorage.DIDARecord> items = storage[id];

                    // If Queue is full
                    if (items.Count == MAX_VERSIONS_STORED)
                    {
                        items.RemoveAt(0);
                    }
                    items.Add(didaRecord);
                    this.replicaManager.AddTimeStamp(this.replicaId, id, didaVersion);
                }
                else
                {
                    //storage.Add(id, new List<DIDAStorage.DIDARecord>());
                    this.AddNewKey(id);
                    storage[id].Add(didaRecord);
                    this.replicaManager.CreateNewTimeStamp(
                        this.replicaId,
                        id,
                        didaVersion
                    );
                }
            }

            return didaVersion;
        }

        public PopulateReply Populate(RepeatedField<KeyValuePair> keyValuePairs)
        {
            foreach (KeyValuePair pair in keyValuePairs)
            {
                DIDAStorage.DIDAVersion version;
                version.replicaId = pair.ReplicaId;
                version.versionNumber = 1;

                DIDAStorage.DIDARecord record;
                record.id = pair.Key;
                record.val = pair.Value;
                record.version = version;

                //List<DIDAStorage.DIDARecord> listRecords = new List<DIDAStorage.DIDARecord>();
                //listRecords.Add(record);
                //storage.Add(pair.Key, listRecords);
                this.AddNewKey(record.id);
                this.storage[record.id].Add(record);

                this.replicaManager.CreateNewTimeStamp(
                    this.replicaId,
                    pair.Key,
                    new DIDAStorage.DIDAVersion
                    {
                        versionNumber = version.versionNumber,
                        replicaId = version.replicaId
                    }
                );

                Console.WriteLine("Stored!! Key: " + pair.Key + " Value: " + storage[pair.Key][0].val + " Id: " + storage[pair.Key][0].id + " Version Number: " + storage[pair.Key][0].version.versionNumber + " Replica Id: " + storage[pair.Key][0].version.replicaId);
            }

            return new PopulateReply { Okay = true };
        }

        public List<DIDAStorage.DIDARecord> Dump()
        {
            List<DIDAStorage.DIDARecord> data = new List<DIDAStorage.DIDARecord>();

            lock (storage)
            {
                foreach (string key in storage.Keys)
                {
                    var records = storage[key];
                    foreach (DIDAStorage.DIDARecord record in records)
                    {
                        data.Add(record);
                    }
                }
            }

            return data;
        }

        public AddStorageReply AddStorage(RepeatedField<StorageInfo> storageInfos)
        {
            foreach (StorageInfo si in storageInfos)
            {
                StorageNodeStruct node;
                node.serverId = si.ServerId;
                node.replicaId = si.ReplicaId;
                node.url = si.Url;
                node.channel = GrpcChannel.ForAddress(node.url);
                node.gossipClient = new GossipService.GossipServiceClient(node.channel);
                node.uiviClient = new UpdateIfValueIsService.UpdateIfValueIsServiceClient(node.channel);

                Console.WriteLine("Added Storage server Id: " + node.serverId);
                storageNodes.Add(node.serverId, node);

                this.replicaManager.AddStorageReplica(node.replicaId);
            }

            return new AddStorageReply { Okay = true };
        }

        private void OnTimedEvent(Object source, System.Timers.ElapsedEventArgs e)
        {
            Console.WriteLine("Calling Gossip...");
            this.Gossip();
        }

        public void Gossip()
        {
            lock (this)
            {
                List<string> keys = this.replicaManager.GetMyKeys();
                Dictionary<string, List<DIDAStorage.DIDAVersion>> myReplicaTimestamp = this.replicaManager.GetReplicaTimeStamp(this.replicaId);
                Dictionary<string, GossipRequest> gossipRequests = new Dictionary<string, GossipRequest>();

                List<string> storageIds = new List<string>();
                foreach (StorageNodeStruct sns in storageNodes.Values)
                {
                    storageIds.Add(sns.serverId);
                }
                storageIds.Add(this.serverId);

                foreach (string key in keys)
                {
                    ConsistentHashing consistentHashing = new ConsistentHashing(storageIds);
                    List<string> setOfReplicas = consistentHashing.ComputeSetOfReplicas(key);

                    foreach (string sId in setOfReplicas)
                    {
                        // Dont gossip to ourselves
                        if (sId == this.serverId) continue;

                        if (!gossipRequests.ContainsKey(sId))
                        {
                            gossipRequests.Add(sId, new GossipRequest { ReplicaId = this.replicaId });
                        }
                        StorageNodeStruct sns = this.storageNodes[sId];
                        Dictionary<string, List<DIDAStorage.DIDAVersion>> snsReplicaTimestamp = this.replicaManager.GetReplicaTimeStamp(sns.replicaId);
                        if (!snsReplicaTimestamp.ContainsKey(key))
                        {
                            snsReplicaTimestamp.Add(key, new List<DIDAStorage.DIDAVersion>());
                        }

                        List<DIDAStorage.DIDAVersion> timestampsToSend = this.replicaManager.ComputeGossipTimestampsDifferences(myReplicaTimestamp[key], snsReplicaTimestamp[key]);

                        foreach (DIDAStorage.DIDAVersion snsVersion in timestampsToSend)
                        {
                            foreach (DIDAStorage.DIDARecord record in this.storage[key])
                            {
                                if (record.version.versionNumber == snsVersion.versionNumber && record.version.replicaId == snsVersion.replicaId)
                                {
                                    gossipRequests[sId].UpdateLogs.Add(new DIDARecord
                                    {
                                        Id = key,
                                        Val = record.val,
                                        Version = new DIDAVersion
                                        {
                                            VersionNumber = record.version.versionNumber,
                                            ReplicaId = record.version.replicaId
                                        }
                                    });
                                    break;
                                }
                            }
                        }

                        var grpcTimestamp = new TimeStamp { Key = key };
                        foreach (DIDAStorage.DIDAVersion version in myReplicaTimestamp[key])
                        {
                            grpcTimestamp.Timestamp.Add(new DIDAVersion
                            {
                                VersionNumber = version.versionNumber,
                                ReplicaId = version.replicaId
                            });
                        }
                        gossipRequests[sId].ReplicaTimestamp.Add(grpcTimestamp);
                    }
                }

                foreach (string requestServerId in gossipRequests.Keys)
                {
                    try
                    {
                        GossipRequest request = gossipRequests[requestServerId];
                        if (request.UpdateLogs.Count > 0)
                        {
                            Console.WriteLine("============>>> REQUESTT <<<<========================= ServerId: " + requestServerId);
                            GossipReply reply = this.storageNodes[requestServerId].gossipClient.Gossip(request);
                            this.replicaManager.ReplaceTimeStamp(this.storageNodes[requestServerId].replicaId, reply.ReplicaTimestamp);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Gossip Requests exception: " + e.Message);
                    }
                }
            }
        }

        public GossipReply ReceiveGossip(GossipRequest request)
        {
            Console.WriteLine("Receive Gossip from: " + request.ReplicaId);
            Dictionary<string, List<DIDAStorage.DIDAVersion>> myReplicaTimestamp;
            lock (this)
            {
                int otherReplicaId = request.ReplicaId;
                myReplicaTimestamp = this.replicaManager.GetReplicaTimeStamp(this.replicaId);

                foreach (DIDARecord update in request.UpdateLogs)
                {
                    DIDAStorage.DIDAVersion updateVersion = new DIDAStorage.DIDAVersion
                    {
                        versionNumber = update.Version.VersionNumber,
                        replicaId = update.Version.ReplicaId
                    };

                    if (!this.storage.ContainsKey(update.Id))
                    {
                        //this.storage.Add(update.Id, new List<DIDAStorage.DIDARecord>());
                        this.AddNewKey(update.Id);
                        this.replicaManager.CreateNewTimeStamp(otherReplicaId, update.Id, new DIDAStorage.DIDAVersion { versionNumber = -1, replicaId = -1 });
                    }

                    for (int i = 0; i < this.storage[update.Id].Count; i++)
                    {
                        if (this.replicaManager.IsVersionBigger(updateVersion, this.storage[update.Id][i].version))
                        {
                            DIDAStorage.DIDARecord newRecord = new DIDAStorage.DIDARecord
                            {
                                id = update.Id,
                                version = updateVersion,
                                val = update.Val
                            };
                            this.storage[update.Id].Insert(i, newRecord);
                            if (this.storage[update.Id].Count > MAX_VERSIONS_STORED)
                            {
                                this.storage[update.Id].RemoveAt(0);
                            }

                            this.replicaManager.AddTimeStamp(replicaId, update.Id, updateVersion);

                            break;
                        }
                    }
                }

                // Update other Replica Timestamp
                this.replicaManager.ReplaceTimeStamp(otherReplicaId, request.ReplicaTimestamp);

                myReplicaTimestamp = this.replicaManager.GetReplicaTimeStamp(this.replicaId);
            }

            var reply = new GossipReply { };
            foreach (string key in myReplicaTimestamp.Keys)
            {
                TimeStamp grpcTimestamp = new TimeStamp { Key = key };
                foreach (DIDAStorage.DIDAVersion version in myReplicaTimestamp[key])
                {
                    grpcTimestamp.Timestamp.Add(new DIDAVersion
                    {
                        VersionNumber = version.versionNumber,
                        ReplicaId = version.replicaId
                    });
                }
                reply.ReplicaTimestamp.Add(grpcTimestamp);
            }

            return reply;
        }



        private void AddNewKey(string key)
        {
            this.storage.Add(key, new List<DIDAStorage.DIDARecord>());
            this.consensusLock.Add(key, false);
        }
    }
    public struct StorageNodeStruct
    {
        public string serverId;
        public string url;
        public int replicaId;
        public GrpcChannel channel;
        public GossipService.GossipServiceClient gossipClient;
        public UpdateIfValueIsService.UpdateIfValueIsServiceClient uiviClient;
    }
}
