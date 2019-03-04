﻿using System;
using System.Collections.Generic;

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
    }


    public class InStatsCollector : IInStatsCollector
    {
        public Queue<PacketStatus> queue = new Queue<PacketStatus>();

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
            const int MaxCount = 256;
            if (queue.Count > MaxCount)
            {
                queue.Dequeue();
            }

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

