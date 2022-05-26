using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Timers;
using Libplanet.Action;
using Libplanet.Blockchain;
using Libplanet.Blocks;
using Libplanet.Consensus;
using Libplanet.Crypto;
using Libplanet.Net.Messages;
using Serilog;

namespace Libplanet.Net.Consensus
{
    public class ConsensusContext<T>
        where T : IAction, new()
    {
        public const long TimeoutMillisecond = 10 * 1000;

        private readonly BlockChain<T> _blockChain;
        private readonly ILogger _logger;
        private readonly TimeoutTicker _timoutTicker;
        private readonly List<PublicKey> _validators;
        private readonly object _commitLock;
        private readonly PrivateKey _privateKey;

        private ConcurrentDictionary<long, RoundContext<T>> _roundContexts;

        public ConsensusContext(
            long nodeId,
            PrivateKey privateKey,
            List<PublicKey> validators,
            BlockChain<T> blockChain)
        {
            if (validators.Count <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(validators),
                    $"Number of validator should be greater than 0. ({validators.Count}is given)");
            }

            NodeId = nodeId;
            _blockChain = blockChain;
            _validators = validators;
            _privateKey = privateKey;
            _commitLock = new object();
            _roundContexts = new ConcurrentDictionary<long, RoundContext<T>>
            {
                [0] = new RoundContext<T>(NodeId, validators, Height, Round),
            };

            _timoutTicker = new TimeoutTicker(TimeoutMillisecond, TimerTimeoutCallback);
            VoteSets = new Dictionary<long, VoteSet?>();
            _logger = Log
                .ForContext<ConsensusContext<T>>()
                .ForContext("Source", nameof(ConsensusContext<T>));
        }

        /// <summary>
        /// Indicates current height of block.
        /// </summary>
        public long Height { get; internal set; }

        /// <summary>
        /// Indicates current round.
        /// </summary>
        public long Round { get; internal set; }

        /// <summary>
        /// Indicates current round.
        /// </summary>
        public long NodeId { get; internal set; }

        public RoundContext<T> CurrentRoundContext => RoundContextOf(Round);

        // FIXME: Storing all voteset on memory is not required. Leave only 1~2 votesets.
        public Dictionary<long, VoteSet?> VoteSets { get; }

        public void CommitBlock(long height, BlockHash hash)
        {
            // Unlike round, lock is required because block append may take time.
            lock (_commitLock)
            {
                if (height != Height)
                {
                    // Duplicated or invalid commit attempt, do nothing.
                    return;
                }

                _logger.Debug(
                    "Commit block {Hash} from #{Before} to #{After} in node id {Id}.",
                    Height,
                    Height + 1,
                    hash,
                    NodeId);

                Block<T> block = _blockChain.Store.GetBlock<T>(
                    _blockChain.Policy.GetHashAlgorithm,
                    hash);
                _blockChain.Append(block);

                // FIXME: Gets voteset by reference, it can be modified in other place.
                VoteSets.Add(Height, CurrentRoundContext.VoteSet);
                Height++;
                Round = 0;
                _roundContexts = new ConcurrentDictionary<long, RoundContext<T>>();
            }
        }

        public long NextRound(long round)
        {
            if (round != Round)
            {
                // Duplicated or invalid attempt, do nothing.
                return Round;
            }

            _logger.Debug(
                "Increase round from {Before} to {After} in node id {Id}",
                Round,
                Round + 1,
                NodeId);
            Round += 1;

            // NOTE: Reusing existing round context is valid?
            // FIXME: Should not re-create RoundContext. Instead, use new vote set.
            if (!_roundContexts.ContainsKey(Round))
            {
                _roundContexts[Round] = new RoundContext<T>(
                    NodeId,
                    _validators,
                    Height,
                    Round);
            }

            return Round;
        }

        public RoundContext<T> RoundContextOf(long round)
        {
            if (!_roundContexts.ContainsKey(round))
            {
                _roundContexts[round] = new RoundContext<T>(
                    NodeId,
                    _validators,
                    Height,
                    round);
            }

            return _roundContexts[round];
        }

        public Vote SignVote(Vote vote)
        {
            return vote.Sign(_privateKey);
        }

        public ConsensusMessage? HandleMessage(ConsensusMessage message)
        {
            var beforeRoundContext = CurrentRoundContext.State;

            ConsensusMessage? res = null;
            try
            {
                res = CurrentRoundContext.State.Handle(this, message);
            }
            catch (Exception e)
            {
                Log.Error(e, "Handle throws exception: {E}", e);
            }

            SetTimeoutByState(beforeRoundContext);
            return res;
        }

        public override string ToString()
        {
            var message = new Dictionary<string, object>
            {
                { "node_id", NodeId },
                { "number_of_validator", _validators.Count },
                { "height", Height },
                { "round", Round },
                { "step", CurrentRoundContext.State.Name },
            };
            return JsonSerializer.Serialize(message);
        }

        private void TimerTimeoutCallback(object? sender, ElapsedEventArgs eventArgs)
        {
            _logger.Debug(
                "NodeId: {Id}, Height: {RHeight}, Round: {RRound}, " +
                          "State: {State}, TimeoutTicker: " +
                          "Timeout occurred. Considering NIL in " +
                          "Round #{Round} of Height #{Height}.",
                NodeId,
                CurrentRoundContext.Height,
                CurrentRoundContext.Round,
                CurrentRoundContext.State.Name,
                Round,
                Height);

            switch (CurrentRoundContext.State)
            {
                case PreVoteState<T> _:
                    CurrentRoundContext.State = new PreCommitState<T>();
                    StartTimeout();
                    break;
                case PreCommitState<T> _:
                    NextRound(Round);
                    StopTimeout();
                    break;
            }
        }

        private void SetTimeoutByState(IState<T> beforeRoundContext)
        {
            switch (beforeRoundContext)
            {
                case DefaultState<T> _
                    when CurrentRoundContext.State is PreVoteState<T>:
                case PreVoteState<T> _
                    when CurrentRoundContext.State is PreCommitState<T>:
                    StartTimeout();
                    break;
                case PreCommitState<T> _
                    when CurrentRoundContext.State is DefaultState<T>:
                    StopTimeout();
                    break;
            }
        }

        private void StartTimeout()
        {
            _logger.Verbose(
                "NodeId: {Id}, Height: {Height}, Round: {Round}, " +
                          "State: {State}, TimeoutTicker: Timer Started. " +
                          "Timeout will be occurred in {Time}",
                CurrentRoundContext.NodeId,
                CurrentRoundContext.Height,
                CurrentRoundContext.Round,
                CurrentRoundContext.State.Name,
                DateTimeOffset.UtcNow.AddMilliseconds(TimeoutMillisecond));
            _timoutTicker.Set();
        }

        private void StopTimeout()
        {
            _logger.Verbose(
                "NodeId: {Id}, Height: {Height}, Round: {Round}, " +
                          "State: {State}, TimeoutTicker: Timer Stopped.",
                NodeId,
                CurrentRoundContext.Height,
                CurrentRoundContext.Round,
                CurrentRoundContext.State.Name);
            _timoutTicker.Stop();
        }
    }
}
