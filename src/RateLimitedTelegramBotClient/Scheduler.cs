using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpCollections.Generic;
using Telegram.Bot.Types;

namespace Arsam.RateLimitedTelegramBotClient;

public class Scheduler
{
    private readonly SchedulerSettings _settings;
    private readonly long _generalTimestampIncrement;
    private readonly long _privateTimestampIncrement;
    private readonly long _groupTimestampIncrement;
    private readonly long _timestampResetThreshold;

    private readonly struct RequestNode : IComparable<RequestNode>
    {
        public readonly long Bucket;
        public readonly long Timestamp;
        public readonly long TimestampIncrement;

        public RequestNode(long bucket, long timestamp, long timestampIncrement)
        {
            Bucket = bucket;
            Timestamp = timestamp;
            TimestampIncrement = timestampIncrement;
        }

        public RequestNode Next(long currentTimestamp)
        {
            return new RequestNode(Bucket, currentTimestamp + TimestampIncrement, TimestampIncrement);
        }

        public int CompareTo(RequestNode other)
        {
            return Timestamp.CompareTo(other.Timestamp);
        }
    }

    private readonly object _lock;
    private readonly BinaryHeap<RequestNode> _requestHeap;
    private readonly Dictionary<long, Queue<TaskCompletionSource<bool>>?> _buckets;

    private long _lastTimestamp;
    private readonly Timer _timer;

    private long Timestamp() => DateTime.UtcNow.Ticks;

    private void OnInterval()
    {
        long timestamp = Timestamp();

        lock (_lock)
        {
            while (_lastTimestamp < timestamp)
            {
                // If the timing is waaaay off, reset instead of spinning the thread
                if (timestamp - _lastTimestamp > _timestampResetThreshold)
                    _lastTimestamp = timestamp;

                _lastTimestamp += _generalTimestampIncrement;

                while (!_requestHeap.IsEmpty && _requestHeap.Top.Timestamp <= timestamp)
                {
                    var request = _requestHeap.Pop();

                    var queue = _buckets[request.Bucket];

                    if (queue is {Count: 0})
                    {
                        _buckets.Remove(request.Bucket);
                    }
                    else
                    {
                        queue?.Dequeue().SetResult(false);
                        timestamp = Timestamp();
                        _requestHeap.Push(request.Next(timestamp));
                        break;
                    }
                }
            }
        }

        _timer.Change(_settings.SafeGeneralInterval, Timeout.Infinite);
    }

    public Scheduler(SchedulerSettings? schedulerSettings = null)
    {
        _settings = schedulerSettings ?? SchedulerSettings.Default;

        _generalTimestampIncrement = _settings.SafeGeneralInterval * 10_000L;
        _privateTimestampIncrement = _settings.SafePrivateChatInterval * 10_000L;
        _groupTimestampIncrement = _settings.SafeGroupChatInterval * 10_000L;

        _timestampResetThreshold = _generalTimestampIncrement * 30;

        _lock = new object();
        _requestHeap = new BinaryHeap<RequestNode>(16);
        _buckets = new Dictionary<long, Queue<TaskCompletionSource<bool>>?>(16);

        _lastTimestamp = 0;
        _timer = new Timer(s => ((Scheduler) s!).OnInterval(), this, 0, Timeout.Infinite);
    }

    private Task YieldAsyncCore(long bucket, long timestampIncrement)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            if (_buckets.TryGetValue(bucket, out var queue))
            {
                queue?.Enqueue(tcs);
            }
            else
            {
                _buckets[bucket] = queue = new Queue<TaskCompletionSource<bool>>(4);
                queue.Enqueue(tcs);
                _requestHeap.Push(new RequestNode(bucket, Timestamp(), timestampIncrement));
            }
        }

        return tcs.Task;
    }

    public Task YieldAsync(ChatId chatId)
    {
        return chatId.Username != null
            ? YieldAsyncCore(chatId.Username.GetHashCode(), _groupTimestampIncrement)
            : YieldAsync(chatId.Identifier ?? 0);
    }

    public Task YieldAsync(long bucket = 0)
    {
        var timestampIncrement = bucket == 0 ? _generalTimestampIncrement
            : bucket > 0 ? _privateTimestampIncrement
            : _groupTimestampIncrement;

        return YieldAsyncCore(bucket, timestampIncrement);
    }

    public void Stop()
    {
        try
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _timer.Dispose();
        }
        catch
        {
            // ignored
        }
    }
}