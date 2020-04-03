namespace Piot.Brisk.Stats
{

    public struct SimpleStats
    {
        public int PacketCount;
        public int OctetSize;

        public override string ToString()
        {
            return $"Packets/s: {PacketCount} Octets/s: {OctetSize}";
        }
    }

    public class SimpleStatsCollector
    {
        private int totalCount;
        private int totalOctetSize;

        public void AddPacket(int packetCount)
        {
            totalCount++;
            totalOctetSize += packetCount;
        }

        public SimpleStats GetStatsAndClear(long timeInMs)
        {
            if (timeInMs < 10)
            {
                return new SimpleStats();
            }

            var f = timeInMs / 1000.0f;
            return new SimpleStats
            {
                PacketCount = (int)(totalCount / f),
                OctetSize = (int)(totalOctetSize / f),
            };
            totalCount = 0;
            totalOctetSize = 0;
        }
    }
}