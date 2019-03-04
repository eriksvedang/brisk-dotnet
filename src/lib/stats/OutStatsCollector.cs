using System;
using System.Collections.Generic;

namespace Piot.Brisk.Stats.In
{

    public struct OutPacketStatus
    {
        public DateTime SavedAt;
        public int OctetSize;
    }


    public class OutStatsCollector : IOutStatsCollector
    {
        public Queue<OutPacketStatus> queue = new Queue<OutPacketStatus>();

        void IOutStatsCollector.PacketSent(DateTime now, int octetCount)
        {
            var p = new OutPacketStatus { SavedAt = now, OctetSize = octetCount };
            Add(p);
        }

        void Add(OutPacketStatus p)
        {
            const int MaxCount = 256;
            if (queue.Count > MaxCount)
            {
                queue.Dequeue();
            }

            queue.Enqueue(p);
        }

        public OutStats GetInfo(int maxCount)
        {
            var wholeArray = queue.ToArray();
            var clamp = maxCount;
            if (clamp > wholeArray.Length)
            {
                clamp = wholeArray.Length;
            }
            var index = wholeArray.Length - clamp;
            var copiedPackets = new OutPacketStatus[clamp];
            Array.Copy(wholeArray, index, copiedPackets, 0, clamp);
            return new OutStats { packetInfo = copiedPackets };
        }
    }

}

