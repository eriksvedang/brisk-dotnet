using System;

namespace Piot.Brisk.Stats.In
{
    public interface IInStatsCollector
    {
        void PacketsDropped(DateTime now, int packetCount);
        void PacketReceived(DateTime now, int octetCount);
        InStats GetInfo(int maxCount);
    }
}
