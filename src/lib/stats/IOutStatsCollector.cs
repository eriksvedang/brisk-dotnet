using System;

namespace Piot.Brisk.Stats.In
{
    public interface IOutStatsCollector
    {
        void PacketSent(DateTime now, int octetCount);
        OutStats GetInfo(int packetCount);
    }
}
