using System;
namespace Piot.Brisk.Stats.In
{
    public struct InStats
    {
        public PacketStatus[] packetInfo;

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
