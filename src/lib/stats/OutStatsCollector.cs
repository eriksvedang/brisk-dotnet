using System;
using System.Collections.Generic;

namespace Piot.Brisk.Stats.In
{
    public class OutPacketStatus
    {
        public uint SequenceId;
        public bool HasSequenceId;
        public bool Delivered;
        public long SavedAt;
        public int OctetSize;
        public bool HasLatency;
        public long Latency;
        public bool HasDelivery;
        public int StatsPacketId;

        public override string ToString()
        {
            var s = $"[outstatus size:{OctetSize}";
            if (HasLatency)
            {
                s += $" latency: {Latency}";
            }
            if (HasDelivery)
            {
                s += $" delivery: {Delivered}";
            }

            s += "]";
            return s;
        }
    }


    public class OutStatsCollector : IOutStatsCollector
    {
        public Queue<OutPacketStatus> queue = new Queue<OutPacketStatus>();
        int statsPacketId;

        void IOutStatsCollector.PacketSent(long now, int octetCount)
        {
            var p = new OutPacketStatus { SavedAt = now, OctetSize = octetCount };
            Add(p);
        }

        void IOutStatsCollector.SequencePacketSent(uint sequenceId, long now, int octetCount)
        {
            var p = new OutPacketStatus { SequenceId = sequenceId, HasSequenceId = true, SavedAt = now, OctetSize = octetCount };
            Add(p);
        }

        void Add(OutPacketStatus p)
        {
            const int MaxCount = 127;
            if (queue.Count > MaxCount)
            {
                queue.Dequeue();
            }
            p.StatsPacketId = statsPacketId++;

            queue.Enqueue(p);
        }


        public bool UpdateLatency(uint sequenceId, long ms)
        {
            var wholeArray = queue.ToArray();
            for (var i = 0; i < wholeArray.Length; ++i)
            {
                var packet = wholeArray[i];
                if (packet.HasSequenceId && packet.SequenceId == sequenceId)
                {
                    packet.Latency = ms;
                    packet.HasLatency = true;
                    return true;
                }
            }

            return false;
        }

        public bool UpdateConfirmation(uint sequenceId, bool delivered)
        {
            var wholeArray = queue.ToArray();
            for (var i = 0; i < wholeArray.Length; ++i)
            {
                var packet = wholeArray[i];

                if (packet.HasSequenceId && packet.SequenceId == sequenceId)
                {
                    packet.Delivered = delivered;
                    packet.HasDelivery = true;
                    return true;
                }
            }

            return false;
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

