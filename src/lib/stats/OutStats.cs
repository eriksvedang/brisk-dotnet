using System;
namespace Piot.Brisk.Stats.In
{
    public struct OutStats
    {
        public OutPacketStatus[] packetInfo;

        public override string ToString()
        {
            var s = "";

            foreach (var status in packetInfo)
            {
                s += $"{status}\n";
            }

            return s;
        }
    }
}
