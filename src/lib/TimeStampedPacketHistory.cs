using System.Collections.Generic;
using Piot.Log;
using Piot.Tend.Client;

namespace Piot.Linger
{
    public class PacketHistoryItem
    {
        public long Time;
        public SequenceId SequenceId;
    }

    public class TimeStampedPacketHistory
    {
        Queue<PacketHistoryItem> items = new Queue<PacketHistoryItem>();
        SequenceId lastSequenceId = SequenceId.Max;

        public void SentPacket(SequenceId sequenceId, long time, ILog log)
        {
            //log.Info($"sent packet at timen {sequenceId} time {time}");
            var p = new PacketHistoryItem { Time = time, SequenceId = sequenceId };
            items.Enqueue(p);
        }

        public bool ReceivedConfirmation(SequenceId sequenceId, out PacketHistoryItem foundItem, ILog log)
        {
            //log.Info($"received confirmation {sequenceId} last {lastSequenceId}");
            if (sequenceId.Value == lastSequenceId.Value)
            {
                foundItem = null;
                return false;
            }
            while (true)
            {
                var item = items.Dequeue();
                if (sequenceId.Value == item.SequenceId.Value)
                {
                    lastSequenceId = sequenceId;
                    foundItem = item;
                    return true;
                }
            }
        }
    }
}
