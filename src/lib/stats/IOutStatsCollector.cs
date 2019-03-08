using System;

namespace Piot.Brisk.Stats.In
{
    public interface IOutStatsCollector
    {
        void PacketSent(long now, int octetCount);
        void SequencePacketSent(uint sequenceId, long now, int octetCount);
        OutStats GetInfo(int packetCount);

        bool UpdateLatency(uint sequenceId, long ms);
        bool UpdateConfirmation(uint sequenceId, bool delivered);
    }
}
