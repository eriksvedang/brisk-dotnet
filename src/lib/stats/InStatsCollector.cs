using System;
using System.Collections.Concurrent;

namespace Piot.Brisk.Stats.In
{
    public enum PacketState
    {
        Dropped,
        Received
    }

    public struct PacketStatus
    {
        public PacketState State;
        public DateTime SavedAt;
        public int OctetSize;
        public int StatsPacketId;

        public override string ToString()
        {
            return $"[packet status {State} size: {OctetSize} packetId: {StatsPacketId}]";
        }
    }


    public class InStatsCollector : IInStatsCollector
    {
        public ConcurrentQueue<PacketStatus> queue = new ConcurrentQueue<PacketStatus>();
        int statsPacketId;

        void IInStatsCollector.PacketReceived(DateTime now, int octetCount)
        {
            var p = new PacketStatus { State = PacketState.Received, SavedAt = now, OctetSize = octetCount };
            Add(p);
        }

        void IInStatsCollector.PacketsDropped(DateTime now, int packetCount)
        {
            var p = new PacketStatus { State = PacketState.Dropped, SavedAt = now, OctetSize = 0 };
            Add(p);
        }

        void Add(PacketStatus p)
        {

            const int MaxCount = 127;
            if (queue.Count > MaxCount)
            {
                PacketStatus packetStatus;
                queue.TryDequeue(out packetStatus);
            }
            p.StatsPacketId = statsPacketId++;

            queue.Enqueue(p);
        }

        public InStats GetInfo(int maxCount)
        {
            var wholeArray = queue.ToArray();
            var clamp = maxCount;
            if (clamp > wholeArray.Length)
            {
                clamp = wholeArray.Length;
            }
            var index = wholeArray.Length - clamp;
            var copiedPackets = new PacketStatus[clamp];
            Array.Copy(wholeArray, index, copiedPackets, 0, clamp);
            return new InStats { packetInfo = copiedPackets };
        }
    }

}

