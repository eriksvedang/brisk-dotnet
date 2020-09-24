namespace Piot.Brisk
{
    using System;
    using System.Collections.Generic;
    using Piot.Brisk.Time;
    using Piot.Tend.Client;

    public struct PacketPayload
    {
        public byte[] Payload;
        public long ReceivedAtMonotonicMs;
        public SequenceId SequenceId;

        public PacketPayload(byte[] payload, SequenceId sequenceId, long receivedAt)
        {
            Payload = payload;
            ReceivedAtMonotonicMs = receivedAt;
            SequenceId = sequenceId;
        }

        public override string ToString()
        {
            var hexString = BitConverter.ToString(Payload).Replace("-", string.Empty);
            return $"[packet {SequenceId} time:{ReceivedAtMonotonicMs} payload:{hexString}]";
        }
    }

    public class PacketBuffer
    {
        private readonly IMonotonicClock monotonicClock;
        private readonly List<PacketPayload> packets = new List<PacketPayload>();

        public PacketBuffer(IMonotonicClock monotonicClock)
        {
            this.monotonicClock = monotonicClock;
        }

        public void Clear()
        {
            lock (packets)
            {
                packets.Clear();
            }
        }

        public void AddPacket(SequenceId sequenceId, byte[] octets)
        {
            var now = monotonicClock.NowMilliseconds();
            var snapshot = new PacketPayload(octets, sequenceId, now);
            lock (packets)
            {
                packets.Add(snapshot);
            }
        }

        public bool Pop(out PacketPayload payload)
        {
            lock (packets)
            {
                if (packets.Count == 0)
                {
                    payload = new PacketPayload();
                    return false;
                }

                payload = packets[0];
                packets.RemoveAt(0);
                return true;
            }
        }
    }
}