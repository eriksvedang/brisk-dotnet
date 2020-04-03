namespace Piot.Brisk
{
    using System;
    using System.Collections.Concurrent;
    using Tend.Client;

    public struct PacketReceivedNotification
    {
        public SequenceId SequenceId;
        public bool WasReceived;

        public override string ToString()
        {
            return $"[receivednotifiy {SequenceId} received:{WasReceived}]";
        }
    }

    internal static class ConcurrentQueueExtension
    {
        public static void Clear<T>(this ConcurrentQueue<T> queue)
        {
            T item;
            while (queue.TryDequeue(out item))
            {
                // intentionally do nothing
            }
        }
    }

    public class PacketReceivedNotifications
    {
        private SequenceId expectedSequenceId = new SequenceId(0);
        private readonly ConcurrentQueue<PacketReceivedNotification> queue = new ConcurrentQueue<PacketReceivedNotification>();

        public void Enqueue(SequenceId sequenceId, bool wasReceived)
        {
            if (sequenceId.Value != expectedSequenceId.Value)
            {
                throw new Exception($"wrong packet notification. Expected {expectedSequenceId} but received {sequenceId}");
            }

            var info = new PacketReceivedNotification
            {
                SequenceId = sequenceId,
                WasReceived = wasReceived
            };

            queue.Enqueue(info);
            expectedSequenceId = expectedSequenceId.Next();
        }

        public void Clear()
        {
            queue.Clear();
            expectedSequenceId = new SequenceId(0);
        }

        public bool TryDequeue(out PacketReceivedNotification result)
        {
            var worked = queue.TryDequeue(out result);
            return worked;
        }
    }
}
