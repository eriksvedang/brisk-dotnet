namespace Piot.Brisk.Stats
{

    public struct SimpleStats
    {
        public int PacketCount;
        public int PacketSize;
        public int OctetSize;
        public int Latency;

        public override string ToString()
        {
            return $"{PacketCount} packets/s, {OctetSize} octets/s, average packet size: {PacketSize} octets, latency: {Latency} ms  ";
        }
    }

    public class SimpleStatsCollector
    {
        private int totalCount;
        private long totalOctetSize;

        private int totalLatencyCount;
        private long totalLatency;

        public void AddPacket(int packetCount)
        {
            totalCount++;
            totalOctetSize += packetCount;
        }

        public void AddLatency(long latencyMs)
        {
            totalLatencyCount++;
            totalLatency += latencyMs;
        }

        public SimpleStats GetStatsAndClear(long timeInMs)
        {
            if (timeInMs < 10)
            {
                return new SimpleStats();
            }

            var f = timeInMs / 1000.0f;

            var latencyAverage = -1;
            if (totalLatencyCount > 0)
            {
                latencyAverage = (int)(totalLatency / totalLatencyCount);
            }

            var packetSizeAverage = -1;
            if (totalCount > 0)
            {
                packetSizeAverage = (int)(totalOctetSize / totalCount);
            }


            var stats = new SimpleStats
            {
                PacketCount = (int)(totalCount / f),
                OctetSize = (int)(totalOctetSize / f),
                PacketSize = packetSizeAverage,
                Latency = latencyAverage,
            };

            totalCount = 0;
            totalOctetSize = 0;
            totalLatency = 0;
            totalLatencyCount = 0;

            return stats;
        }
    }
}