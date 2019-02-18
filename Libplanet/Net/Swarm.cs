using System;
using System.Collections;
using System.Collections.Async;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Libplanet.Action;
using Libplanet.Blockchain;
using Libplanet.Blocks;
using Libplanet.Crypto;
using Libplanet.Net.Messages;
using Libplanet.Tx;
using NetMQ;
using NetMQ.Sockets;
using Nito.AsyncEx;
using Serilog;
using Serilog.Events;

namespace Libplanet.Net
{
    [Uno.GeneratedEquality]
    public partial class Swarm : ICollection<Peer>, IDisposable
    {
        private readonly IDictionary<Peer, DateTime> _peers;
        private readonly IDictionary<Peer, DateTime> _removedPeers;

        private readonly PrivateKey _privateKey;
        private readonly RouterSocket _router;
        private readonly IDictionary<Address, DealerSocket> _dealers;

        private readonly Uri _listenUrl;
        private readonly TimeSpan _dialTimeout;
        private readonly AsyncLock _distributeMutex;
        private readonly AsyncLock _receiveMutex;

        private readonly ILogger _logger;

        public Swarm(
            PrivateKey privateKey,
            Uri listenUrl,
            int millisecondsDialTimeout,
            DateTime? createdAt = null)
            : this(
                  privateKey,
                  listenUrl,
                  TimeSpan.FromMilliseconds(millisecondsDialTimeout),
                  createdAt)
        {
        }

        public Swarm(
            PrivateKey privateKey,
            Uri listenUrl,
            TimeSpan? dialTimeout = null,
            DateTime? createdAt = null)
        {
            _privateKey = privateKey;
            _listenUrl = listenUrl;
            _dialTimeout = dialTimeout ?? TimeSpan.FromMilliseconds(15000);
            _peers = new Dictionary<Peer, DateTime>();
            _removedPeers = new Dictionary<Peer, DateTime>();
            LastSeenTimestamps = new Dictionary<Peer, DateTime>();

            DateTime now = createdAt.GetValueOrDefault(DateTime.UtcNow);
            LastDistributed = now;
            LastReceived = now;
            DeltaDistributed = new AsyncAutoResetEvent();
            DeltaReceived = new AsyncAutoResetEvent();
            TxReceived = new AsyncAutoResetEvent();

            _dealers = new Dictionary<Address, DealerSocket>();
            _router = new RouterSocket();

            _distributeMutex = new AsyncLock();
            _receiveMutex = new AsyncLock();

            _logger = Log.ForContext<Swarm>()
                .ForContext("Swarm_listenUrl", _listenUrl.ToString());
            Running = false;
        }

        ~Swarm()
        {
            // FIXME If possible, we should stop Swarm appropriately here.
            if (Running)
            {
                _logger.Warning(
                    "Swarm is scheduled to destruct, but it's still running.");
            }
        }

        public int Count => _peers.Count;

        public bool IsReadOnly => false;

        [Uno.EqualityKey]
        public Peer AsPeer => new Peer(
            _privateKey.PublicKey,
            (_listenUrl != null) ? new[] { _listenUrl } : new Uri[] { });

        [Uno.EqualityIgnore]
        public AsyncAutoResetEvent DeltaReceived { get; }

        [Uno.EqualityIgnore]
        public AsyncAutoResetEvent DeltaDistributed { get; }

        [Uno.EqualityIgnore]
        public AsyncAutoResetEvent TxReceived { get; }

        public DateTime LastReceived { get; private set; }

        public DateTime LastDistributed { get; private set; }

        public IDictionary<Peer, DateTime> LastSeenTimestamps
        {
            get;
            private set;
        }

        public bool Running { get; private set; }

        public async Task<ISet<Peer>> AddPeersAsync(
            IEnumerable<Peer> peers,
            DateTime? timestamp = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (timestamp == null)
            {
                timestamp = DateTime.UtcNow;
            }

            foreach (Peer peer in peers)
            {
                if (_removedPeers.ContainsKey(peer))
                {
                    _removedPeers.Remove(peer);
                }
            }

            var existingKeys = new HashSet<PublicKey>(
                _peers.Keys.Select(p => p.PublicKey)
            );
            PublicKey publicKey = _privateKey.PublicKey;
            var addedPeers = new HashSet<Peer>();

            foreach (Peer peer in peers)
            {
                Peer addedPeer = peer;
                if (peer.PublicKey == publicKey)
                {
                    continue;
                }

                if (existingKeys.Contains(peer.PublicKey))
                {
                    continue;
                }

                if (Running)
                {
                    try
                    {
                        _logger.Debug($"Trying to DialPeerAsync({peer})...");
                        addedPeer = await DialPeerAsync(
                            peer,
                            cancellationToken
                        );
                        _logger.Debug($"DialPeerAsync({peer}) is complete.");
                    }
                    catch (IOException e)
                    {
                        _logger.Error(
                            e,
                            $"IOException occured in DialPeerAsync ({peer})."
                        );
                        continue;
                    }
                }

                _peers[addedPeer] = timestamp.Value;
                addedPeers.Add(addedPeer);
            }

            return addedPeers;
        }

        public void Add(Peer item)
        {
            if (Running)
            {
                var task = DialPeerAsync(item, CancellationToken.None);
                Peer dialed = task.Result;
                _peers[dialed] = DateTime.UtcNow;
            }
            else
            {
                _peers[item] = DateTime.UtcNow;
            }
        }

        public void Clear()
        {
            _peers.Clear();
        }

        public bool Contains(Peer item)
        {
            return _peers.ContainsKey(item);
        }

        public void CopyTo(Peer[] array, int arrayIndex)
        {
            if (array == null)
            {
                throw new ArgumentNullException(nameof(array));
            }

            if (arrayIndex < 0)
            {
                throw new ArgumentOutOfRangeException();
            }

            if (array.Length < Count + arrayIndex)
            {
                throw new ArgumentException();
            }

            int index = arrayIndex;
            foreach (Peer peer in this)
            {
                array[index] = peer;
                index++;
            }
        }

        public async Task StopAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            _logger.Debug("Stopping...");
            if (Running)
            {
                _removedPeers[AsPeer] = DateTime.UtcNow;
                await DistributeDeltaAsync(false, cancellationToken);

                _router.Dispose();
                foreach (DealerSocket s in _dealers.Values)
                {
                    s.Dispose();
                }

                _dealers.Clear();

                Running = false;
            }

            _logger.Debug("Stopped.");
        }

        public void Dispose()
        {
            StopAsync().Wait();
        }

        public IEnumerator<Peer> GetEnumerator()
        {
            return _peers.Keys.GetEnumerator();
        }

        public bool Remove(Peer item)
        {
            return _peers.Remove(item);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public async Task StartAsync<T>(
            BlockChain<T> blockChain,
            int millisecondsDistributeInterval = 1500,
            CancellationToken cancellationToken = default(CancellationToken))
            where T : IAction
        {
            await StartAsync(
                blockChain,
                TimeSpan.FromMilliseconds(millisecondsDistributeInterval),
                cancellationToken
            );
        }

        public async Task StartAsync<T>(
            BlockChain<T> blockChain,
            TimeSpan distributeInterval,
            CancellationToken cancellationToken = default(CancellationToken))
            where T : IAction
        {
            if (Running)
            {
                throw new SwarmException("Swarm is already running.");
            }

            Running = true;
            _router.Bind(_listenUrl.ToString());

            try
            {
                foreach (Peer peer in _peers.Keys)
                {
                    try
                    {
                        Peer replacedPeer = await DialPeerAsync(
                            peer,
                            cancellationToken
                        );
                        if (replacedPeer != peer)
                        {
                            _peers[replacedPeer] = _peers[peer];
                            _peers.Remove(peer);
                        }
                    }
                    catch (IOException e)
                    {
                        _logger.Error(
                            e,
                            $"IOException occured in DialPeerAsync ({peer})."
                        );
                        continue;
                    }
                }

                await Task.WhenAll(
                    RepeatDeltaDistributionAsync(
                        distributeInterval, cancellationToken),
                    ReceiveMessageAsync(blockChain, cancellationToken));
            }
            finally
            {
                await StopAsync();
            }
        }

        internal async Task<IEnumerable<HashDigest<SHA256>>>
            GetBlockHashesAsync(
                Peer peer,
                BlockLocator locator,
                HashDigest<SHA256>? stop,
                CancellationToken token = default(CancellationToken)
            )
        {
            CheckStarted();

            if (!_dealers.TryGetValue(peer.Address, out DealerSocket sock))
            {
                throw new PeerNotFoundException(
                    $"The peer[{peer.Address}] could not be found.");
            }

            return await GetBlockHashesAsync(sock, locator, stop, token);
        }

        internal async Task<IEnumerable<HashDigest<SHA256>>>
            GetBlockHashesAsync(
                DealerSocket sock,
                BlockLocator locator,
                HashDigest<SHA256>? stop,
                CancellationToken cancellationToken
            )
        {
            var request = new GetBlockHashes(locator, stop);
            await sock.SendMultipartMessageAsync(
                request.ToNetMQMessage(_privateKey),
                cancellationToken: cancellationToken);

            NetMQMessage response = await sock.ReceiveMultipartMessageAsync();
            Message parsedMessage = Message.Parse(response, reply: true);
            if (parsedMessage is BlockHashes blockHashes)
            {
                return blockHashes.Hashes;
            }

            throw new InvalidMessageException(
                $"The response of GetBlockHashes isn't BlockHashes. " +
                $"but {parsedMessage}");
        }

        internal IAsyncEnumerable<Block<T>> GetBlocksAsync<T>(
            Peer peer,
            IEnumerable<HashDigest<SHA256>> blockHashes,
            CancellationToken token = default(CancellationToken))
            where T : IAction
        {
            CheckStarted();

            if (!_dealers.TryGetValue(peer.Address, out DealerSocket sock))
            {
                throw new PeerNotFoundException(
                    $"The peer[{peer.Address}] could not be found.");
            }

            return GetBlocksAsync<T>(sock, blockHashes, token);
        }

        internal IAsyncEnumerable<Block<T>> GetBlocksAsync<T>(
            DealerSocket sock,
            IEnumerable<HashDigest<SHA256>> blockHashes,
            CancellationToken cancellationToken)
            where T : IAction
        {
            return new AsyncEnumerable<Block<T>>(async yield =>
            {
                var request = new GetBlocks(blockHashes);
                await sock.SendMultipartMessageAsync(
                    request.ToNetMQMessage(_privateKey),
                    cancellationToken: cancellationToken);

                int hashCount = blockHashes.Count();
                while (hashCount > 0)
                {
                    NetMQMessage response =
                    await sock.ReceiveMultipartMessageAsync(
                        cancellationToken: cancellationToken);
                    Message parsedMessage = Message.Parse(response, true);
                    if (parsedMessage is Block blockMessage)
                    {
                        Block<T> block = Block<T>.FromBencodex(
                            blockMessage.Payload);
                        await yield.ReturnAsync(block);
                        hashCount--;
                    }
                    else
                    {
                        throw new InvalidMessageException(
                            $"The response of getdata isn't block. " +
                            $"but {parsedMessage}");
                    }
                }
            });
        }

        internal IAsyncEnumerable<Transaction<T>> GetTxsAsync<T>(
            Peer peer,
            IEnumerable<TxId> txIds,
            CancellationToken cancellationToken = default(CancellationToken))
            where T : IAction
        {
            CheckStarted();

            if (!_dealers.TryGetValue(peer.Address, out DealerSocket sock))
            {
                throw new PeerNotFoundException(
                    $"The peer[{peer.Address}] could not be found.");
            }

            return GetTxsAsync<T>(sock, txIds, cancellationToken);
        }

        internal IAsyncEnumerable<Transaction<T>> GetTxsAsync<T>(
            DealerSocket socket,
            IEnumerable<TxId> txIds,
            CancellationToken cancellationToken = default(CancellationToken))
            where T : IAction
        {
            return new AsyncEnumerable<Transaction<T>>(async yield =>
            {
                var request = new GetTxs(txIds);
                await socket.SendMultipartMessageAsync(
                    request.ToNetMQMessage(_privateKey),
                    cancellationToken: cancellationToken);

                int hashCount = txIds.Count();
                while (hashCount > 0)
                {
                    NetMQMessage response =
                    await socket.ReceiveMultipartMessageAsync(
                        cancellationToken: cancellationToken);
                    Message parsedMessage = Message.Parse(response, true);
                    if (parsedMessage is Messages.Tx parsed)
                    {
                        Transaction<T> tx = Transaction<T>.FromBencodex(
                            parsed.Payload);
                        await yield.ReturnAsync(tx);
                        hashCount--;
                    }
                    else
                    {
                        throw new InvalidMessageException(
                            $"The response of getdata isn't block. " +
                            $"but {parsedMessage}");
                    }
                }
            });
        }

        internal async Task BroadcastBlocksAsync<T>(
            IEnumerable<Block<T>> blocks,
            CancellationToken cancellationToken = default(CancellationToken))
            where T : IAction
        {
            _logger.Debug("Broadcast Blocks.");
            var message = new BlockHashes(blocks.Select(b => b.Hash));
            await BroadcastMessage(
                message.ToNetMQMessage(_privateKey),
                TimeSpan.FromMilliseconds(300),
                cancellationToken);
        }

        internal async Task BroadcastTxsAsync<T>(
            IEnumerable<Transaction<T>> txs,
            CancellationToken cancellationToken = default(CancellationToken))
            where T : IAction
        {
            _logger.Debug("Broadcast Txs.");
            var message = new TxIds(txs.Select(tx => tx.Id));
            await BroadcastMessage(
                message.ToNetMQMessage(_privateKey),
                TimeSpan.FromMilliseconds(300),
                cancellationToken);
        }

        private static IEnumerable<Peer> FilterPeers(
            IDictionary<Peer, DateTime> peers,
            DateTime before,
            DateTime? after = null,
            bool remove = false)
        {
            foreach (KeyValuePair<Peer, DateTime> kv in peers.ToList())
            {
                if (after != null && kv.Value <= after)
                {
                    continue;
                }

                if (kv.Value <= before)
                {
                    if (remove)
                    {
                        peers.Remove(kv.Key);
                    }

                    yield return kv.Key;
                }
            }
        }

        private async Task ReceiveMessageAsync<T>(
            BlockChain<T> blockChain, CancellationToken cancellationToken)
            where T : IAction
        {
            CheckStarted();

            while (Running)
            {
                try
                {
                    NetMQMessage raw;
                    try
                    {
                        raw = await _router.ReceiveMultipartMessageAsync(
                            timeout: TimeSpan.FromMilliseconds(100),
                            cancellationToken: cancellationToken);
                    }
                    catch (TimeoutException)
                    {
                        // Ignore this exception because it's expected
                        // when there is no received message in duration.
                        continue;
                    }

                    Message message = Message.Parse(raw, reply: false);
                    _logger.Debug($"Message[{message}] received.");

                    // Queue a task per message to avoid blocking.
                    #pragma warning disable CS4014
                    Task.Run(async () =>
                    {
                        // it's still async because some method it relies are
                        // async yet.
                        await ProcessMessageAsync(
                            blockChain,
                            message,
                            cancellationToken
                        );
                    });
                    #pragma warning restore CS4014
                }
                catch (InvalidMessageException e)
                {
                    _logger.Error(
                        e,
                        "Could not parse NetMQMessage properly; ignore."
                    );
                }
            }
        }

        private async Task ProcessMessageAsync<T>(
            BlockChain<T> blockChain,
            Message message,
            CancellationToken cancellationToken)
            where T : IAction
        {
            switch (message)
            {
                case Ping ping:
                    {
                        _logger.Debug($"Ping received.");
                        var reply = new Pong
                        {
                            Identity = ping.Identity,
                        };
                        await ReplyAsync(reply, cancellationToken);
                        break;
                    }

                case Messages.PeerSetDelta peerSetDelta:
                    {
                        await ProcessDeltaAsync(
                            peerSetDelta.Delta, cancellationToken);
                        break;
                    }

                case GetBlockHashes getBlockHashes:
                    {
                        IAsyncEnumerable<HashDigest<SHA256>> hashes =
                            blockChain.FindNextHashes(
                                getBlockHashes.Locator,
                                getBlockHashes.Stop,
                                500);
                        var reply = new BlockHashes(
                            await hashes.ToListAsync(cancellationToken)
                        )
                        {
                            Identity = getBlockHashes.Identity,
                        };
                        await ReplyAsync(reply, cancellationToken);
                        break;
                    }

                case GetBlocks getBlocks:
                    {
                        await TransferBlocks(
                            blockChain, getBlocks, cancellationToken);
                        break;
                    }

                case GetTxs getTxs:
                    {
                        await TransferTxs(
                            blockChain, getTxs, cancellationToken);
                        break;
                    }

                case TxIds txIds:
                    {
                        await ProcessTxIds(
                            txIds, blockChain, cancellationToken);
                        break;
                    }

                case BlockHashes blockHashes:
                    {
                        await ProcessBlockHashes(
                            blockHashes, blockChain, cancellationToken);
                        break;
                    }

                default:
                    Trace.Fail($"Can't handle message. [{message}]");
                    break;
            }
        }

        private async Task ProcessBlockHashes<T>(
            BlockHashes message,
            BlockChain<T> blockChain,
            CancellationToken cancellationToken = default(CancellationToken))
            where T : IAction
        {
            if (!(message.Identity is Address from))
            {
                throw new NullReferenceException(
                    "BlockHashes doesn't have sender address.");
            }

            if (!_dealers.TryGetValue(from, out DealerSocket sock))
            {
                _logger.Information(
                    "BlockHashes was sent from unknown peer. ignored.");
                return;
            }

            IAsyncEnumerable<Block<T>> fetched = GetBlocksAsync<T>(
                sock, message.Hashes, cancellationToken);

            List<Block<T>> blocks = await fetched.ToListAsync();
            await AppendBlocksAsync(
                sock, blockChain, blocks, cancellationToken);
        }

        private async Task AppendBlocksAsync<T>(
            DealerSocket socket,
            BlockChain<T> blockChain,
            List<Block<T>> blocks,
            CancellationToken cancellationToken)
            where T : IAction
        {
            // We assume that the blocks are sorted in order.
            Block<T> oldest = blocks.First();
            Block<T> latest = blocks.Last();
            HashDigest<SHA256>? tip = await blockChain.Store.IndexBlockHash(-1);

            if (tip == null || oldest.PreviousHash == tip)
            {
                // Caught up with everything, so we just connect it.
                foreach (Block<T> block in blocks)
                {
                    await blockChain.Append(block);
                }
            }
            else if (latest.Index > blockChain.Tip.Index)
            {
                // We need some other blocks, so request to sender.
                BlockLocator locator = await blockChain.GetBlockLocator();
                IEnumerable<HashDigest<SHA256>> hashes =
                    await GetBlockHashesAsync(
                        socket, locator, oldest.Hash, cancellationToken);
                HashDigest<SHA256> branchPoint = hashes.First();

                await blockChain.DeleteAfter(branchPoint);

                await GetBlocksAsync<T>(
                    socket,
                    hashes.Skip(1),
                    cancellationToken
                ).ForEachAsync(block =>
                {
                    blockChain.Append(block);
                });

                await AppendBlocksAsync(
                    socket, blockChain, blocks, cancellationToken);
            }
            else
            {
                _logger.Information(
                    "Received index is older than current chain's tip." +
                    " ignored.");
            }
        }

        private async Task TransferTxs<T>(
            BlockChain<T> blockChain,
            GetTxs getTxs,
            CancellationToken cancellationToken)
            where T : IAction
        {
            IDictionary<TxId, Transaction<T>> txs = blockChain.Transactions;
            foreach (var txid in getTxs.TxIds)
            {
                if (txs.TryGetValue(txid, out Transaction<T> tx))
                {
                    Message response = new Messages.Tx(tx.ToBencodex(true))
                    {
                        Identity = getTxs.Identity,
                    };
                    await ReplyAsync(response, cancellationToken);
                }
            }
        }

        private async Task ProcessTxIds<T>(
            TxIds message,
            BlockChain<T> blockChain,
            CancellationToken cancellationToken = default(CancellationToken))
            where T : IAction
        {
            _logger.Debug("Trying to fetch txs...");

            IEnumerable<TxId> unknownTxIds = message.Ids
                .Where(id => !blockChain.Transactions.ContainsKey(id));

            if (!(message.Identity is Address from))
            {
                throw new NullReferenceException(
                    "TxIds doesn't have sender address.");
            }

            if (!_dealers.TryGetValue(from, out DealerSocket sock))
            {
                _logger.Information(
                    "TxIds was sent from unknown peer. ignored.");
                return;
            }

            IAsyncEnumerable<Transaction<T>> fetched =
                GetTxsAsync<T>(sock, unknownTxIds, cancellationToken);
            var toStage = new HashSet<Transaction<T>>(
                await fetched.ToListAsync(cancellationToken));

            blockChain.StageTransactions(toStage);
            TxReceived.Set();
            _logger.Debug("Txs staged successfully.");
        }

        private async Task ReplyAsync(Message message, CancellationToken token)
        {
            NetMQMessage netMQMessage = message.ToNetMQMessage(_privateKey);
            await _router.SendMultipartMessageAsync(
                netMQMessage,
                cancellationToken: token);
        }

        private async Task TransferBlocks<T>(
            BlockChain<T> blockChain,
            GetBlocks getData,
            CancellationToken cancellationToken)
            where T : IAction
        {
            foreach (HashDigest<SHA256> hash in getData.BlockHashes)
            {
                if (blockChain.Blocks.TryGetValue(hash, out Block<T> block))
                {
                    Message response = new Block(block.ToBencodex(true, true))
                    {
                        Identity = getData.Identity,
                    };
                    await ReplyAsync(response, cancellationToken);
                }
            }
        }

        private async Task ProcessDeltaAsync(
            PeerSetDelta delta,
            CancellationToken cancellationToken
        )
        {
            Peer sender = delta.Sender;
            PublicKey senderKey = sender.PublicKey;

            if (!_peers.ContainsKey(sender) &&
                _peers.Keys.All(p => senderKey != p.PublicKey))
            {
                delta = new PeerSetDelta(
                    delta.Sender,
                    delta.Timestamp,
                    delta.AddedPeers.Add(sender),
                    delta.RemovedPeers,
                    delta.ExistingPeers
                );
            }

            _logger.Debug($"Received the delta[{delta}].");

            using (await _receiveMutex.LockAsync(cancellationToken))
            {
                _logger.Debug($"Trying to apply the delta[{delta}]...");
                await ApplyDelta(delta, cancellationToken);

                LastReceived = delta.Timestamp;
                LastSeenTimestamps[delta.Sender] = delta.Timestamp;

                DeltaReceived.Set();
            }

            _logger.Debug($"The delta[{delta}] has been applied.");
        }

        private async Task ApplyDelta(
            PeerSetDelta delta,
            CancellationToken cancellationToken
        )
        {
            PublicKey senderPublicKey = delta.Sender.PublicKey;
            bool firstEncounter = _peers.Keys.All(
                p => p.PublicKey != senderPublicKey
            );
            RemovePeers(delta.RemovedPeers, delta.Timestamp);
            var addedPeers = new HashSet<Peer>(delta.AddedPeers);

            if (delta.ExistingPeers != null)
            {
                ImmutableHashSet<PublicKey> removedPublicKeys = _removedPeers
                    .Keys.Select(p => p.PublicKey)
                    .ToImmutableHashSet();
                addedPeers.UnionWith(
                    delta.ExistingPeers.Where(
                        p => !removedPublicKeys.Contains(p.PublicKey)
                    )
                );
            }

            _logger.Debug("Trying to add peers...");
            ISet<Peer> added = await AddPeersAsync(
                addedPeers, delta.Timestamp, cancellationToken);
            if (_logger.IsEnabled(LogEventLevel.Debug))
            {
                DumpDiffs(
                    delta,
                    added,
                    addedPeers.Except(added),
                    delta.RemovedPeers
                );
            }

            if (firstEncounter)
            {
                await DistributeDeltaAsync(true, cancellationToken);
            }
        }

        private void DumpDiffs(
            PeerSetDelta delta,
            IEnumerable<Peer> added,
            IEnumerable<Peer> existing,
            IEnumerable<Peer> removed)
        {
            DateTime timestamp = delta.Timestamp;

            foreach (Peer peer in added)
            {
                _logger.Debug($"{timestamp} {delta.Sender} > +{peer}");
            }

            foreach (Peer peer in existing)
            {
                _logger.Debug($"{timestamp} {delta.Sender} > {peer}");
            }

            foreach (Peer peer in removed)
            {
                _logger.Debug($"{timestamp} {delta.Sender} > -{peer}");
            }
        }

        private void RemovePeers(IEnumerable<Peer> peers, DateTime timestamp)
        {
            PublicKey publicKey = _privateKey.PublicKey;
            foreach (Peer peer in peers)
            {
                if (peer.PublicKey != publicKey)
                {
                    continue;
                }

                _removedPeers[peer] = timestamp;
            }

            Dictionary<PublicKey, Peer[]> existingPeers =
                _peers.Keys.ToDictionary(
                    p => p.PublicKey,
                    p => new[] { p }
                );

            using (_distributeMutex.Lock())
            {
                foreach (Peer peer in peers)
                {
                    _peers.Remove(peer);

                    _logger.Debug(
                        $"Trying to close dealers associated {peer}."
                    );
                    if (Running)
                    {
                        CloseDealer(peer);
                    }

                    var pubKey = peer.PublicKey;

                    if (existingPeers.TryGetValue(pubKey, out Peer[] remains))
                    {
                        foreach (Peer key in remains)
                        {
                            _peers.Remove(key);

                            if (Running)
                            {
                                CloseDealer(key);
                            }
                        }
                    }

                    _logger.Debug($"Dealers associated {peer} were closed.");
                }
            }
        }

        private void CloseDealer(Peer peer)
        {
            CheckStarted();
            if (_dealers.TryGetValue(peer.Address, out DealerSocket dealer))
            {
                dealer.Dispose();
                _dealers.Remove(peer.Address);
            }
        }

        private async Task<DealerSocket> DialAsync(
            Uri address,
            DealerSocket dealer,
            CancellationToken cancellationToken
        )
        {
            CheckStarted();

            dealer.Connect(address.ToString());

            _logger.Debug($"Trying to Ping to [{address}]...");
            var ping = new Ping();
            await dealer.SendMultipartMessageAsync(
                ping.ToNetMQMessage(_privateKey),
                cancellationToken: cancellationToken);

            _logger.Debug($"Waiting for Pong from [{address}]...");
            await dealer.ReceiveMultipartMessageAsync(
                timeout: _dialTimeout,
                cancellationToken: cancellationToken);

            _logger.Debug($"Pong received.");

            return dealer;
        }

        private async Task<Peer> DialPeerAsync(
            Peer peer, CancellationToken cancellationToken)
        {
            Peer original = peer;
            if (!_dealers.TryGetValue(peer.Address, out DealerSocket dealer))
            {
                dealer = new DealerSocket();
                dealer.Options.Identity =
                    _privateKey.PublicKey.ToAddress().ToByteArray();
            }

            foreach (var (url, i) in peer.Urls.Select((url, i) => (url, i)))
            {
                try
                {
                    _logger.Debug($"Trying to DialAsync({url})...");
                    await DialAsync(url, dealer, cancellationToken);
                    _logger.Debug($"DialAsync({url}) is complete.");
                }
                catch (IOException e)
                {
                    _logger.Error(
                        e,
                        $"IOException occured in DialAsync ({url})."
                    );
                    dealer.Disconnect(url.ToString());
                    continue;
                }

                if (i > 0)
                {
                    peer = new Peer(peer.PublicKey, peer.Urls.Skip(i));
                }

                _dealers[peer.Address] = dealer;
                break;
            }

            if (dealer == null)
            {
                throw new IOException($"not reachable at all to {original}");
            }

            return peer;
        }

        private async Task DistributeDeltaAsync(
            bool all, CancellationToken cancellationToken)
        {
            CheckStarted();

            DateTime now = DateTime.UtcNow;
            var addedPeers = FilterPeers(
                _peers,
                before: now,
                after: LastDistributed).ToImmutableHashSet();
            var removedPeers = FilterPeers(
                _removedPeers,
                before: now,
                remove: true).ToImmutableHashSet();
            var existingPeers = all
                    ? _peers.Keys.ToImmutableHashSet().Except(addedPeers)
                    : null;
            var delta = new PeerSetDelta(
                sender: AsPeer,
                timestamp: now,
                addedPeers: addedPeers,
                removedPeers: removedPeers,
                existingPeers: existingPeers
            );

            _logger.Debug(
                $"Trying to distribute own delta ({delta.AddedPeers.Count})..."
            );
            if (delta.AddedPeers.Any() || delta.RemovedPeers.Any() || all)
            {
                LastDistributed = now;

                using (await _distributeMutex.LockAsync(cancellationToken))
                {
                    var message = new Messages.PeerSetDelta(delta);
                    _logger.Debug("Send the delta to dealers...");

                    try
                    {
                        await BroadcastMessage(
                            message.ToNetMQMessage(_privateKey),
                            TimeSpan.FromMilliseconds(300),
                            cancellationToken);
                    }
                    catch (TimeoutException e)
                    {
                        _logger.Error(e, "TimeoutException occured.");
                    }

                    _logger.Debug("The delta has been sent.");
                    DeltaDistributed.Set();
                }
            }
        }

        private Task BroadcastMessage(
            NetMQMessage message,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return Task.WhenAll(
                _dealers.Values.Select(
                    s => s.SendMultipartMessageAsync(
                        message,
                        timeout: timeout,
                        cancellationToken: cancellationToken)));
        }

        private async Task RepeatDeltaDistributionAsync(
            TimeSpan interval, CancellationToken cancellationToken)
        {
            int i = 1;
            while (Running)
            {
                await DistributeDeltaAsync(i % 10 == 0, cancellationToken);
                await Task.Delay(interval, cancellationToken);
                i = (i + 1) % 10;
            }
        }

        private void CheckStarted()
        {
            if (!Running)
            {
                throw new NoSwarmContextException("Swarm hasn't started yet.");
            }
        }
    }
}
